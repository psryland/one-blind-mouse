using System.Net.WebSockets;
using System.Threading.Channels;
using OneBlindMouse.Core;

namespace OneBlindMouse.Relay;

/// <summary>
/// One client WebSocket connection. Reads and validates frames, applies per-connection rate limits, and passes valid messages to the <see cref="Hub"/>.
/// Outbound frames go through a bounded queue drained by a single send loop, because a WebSocket allows only one send at a time.
/// </summary>
public sealed class Session
{
	private readonly Hub m_hub;
	private readonly WebSocket m_ws;
	private readonly TimeProvider m_time;
	private readonly Channel<(byte[] Data, WebSocketMessageType Type)> m_outbound;
	private readonly CancellationTokenSource m_cts = new();
	private readonly TokenBucket m_text_bucket;
	private readonly TokenBucket m_binary_bucket;
	private readonly TokenBucket m_violation_bucket;
	private WebSocketCloseStatus m_close_status = WebSocketCloseStatus.NormalClosure;
	private volatile bool m_closing;
	private volatile bool m_hello;

	/// <summary>Wrap an accepted WebSocket. Call <see cref="RunAsync"/> to service it.</summary>
	public Session(Hub hub, WebSocket ws, TimeProvider time, string ip)
	{
		// Rate limiters start full so normal start-up traffic is never limited
		m_hub = hub;
		m_ws = ws;
		m_time = time;
		Ip = ip;
		m_outbound = Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(RelayLimits.OutboundQueue) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
		m_text_bucket = new TokenBucket(time, RelayLimits.TextBurst, RelayLimits.TextRate);
		m_binary_bucket = new TokenBucket(time, RelayLimits.BinaryBurst, RelayLimits.BinaryRate);
		m_violation_bucket = new TokenBucket(time, RelayLimits.ViolationBurst, RelayLimits.ViolationRate);
		CreateBucket = new TokenBucket(time, RelayLimits.CreateBurst, RelayLimits.CreateRate);
	}

	/// <summary>The client's IP address, used for per-address limits.</summary>
	public string Ip { get; }

	/// <summary>Room creation limiter. Used by the hub, under its lock.</summary>
	public TokenBucket CreateBucket { get; }

	// The fields below are guarded by the hub lock

	/// <summary>Display name from hello or the latest rename.</summary>
	public string Name { get; set; } = "Anonymous";

	/// <summary>Preferred colour slot from hello, or -1.</summary>
	public int ColourPref { get; set; } = -1;

	/// <summary>Latest pointer target aspect ratio.</summary>
	public double Aspect { get; set; }

	/// <summary>Latest pointer target label.</summary>
	public string Label { get; set; } = string.Empty;

	/// <summary>The room this connection is in, or null.</summary>
	public Room? Room { get; set; }

	/// <summary>This connection's member slot in <see cref="Room"/>, or null.</summary>
	public Member? Member { get; set; }

	/// <summary>Service the connection until it closes. The hub is told when it disconnects.</summary>
	public async Task RunAsync()
	{
		// Close connections that never introduce themselves, so idle sockets cannot pile up
		var send_loop = Task.Run(SendLoop);
		using var hello_timer = m_time.CreateTimer(_ =>
		{
			if (!m_hello)
				Abort();
		}, null, RelayLimits.HelloTimeout, Timeout.InfiniteTimeSpan);

		// Read until the client goes away or breaks the rules
		try
		{
			await ReceiveLoop();
		}
		catch (Exception)
		{
			// Network errors and aborts both just end the connection
		}
		finally
		{
			// Leave the room (or hold the slot), then let the send loop flush and send the close frame
			m_hub.OnDisconnected(this);
			m_outbound.Writer.TryComplete();
			try
			{
				await send_loop.WaitAsync(TimeSpan.FromSeconds(5), m_time);
			}
			catch (Exception)
			{
				Abort();
			}
		}
	}

	/// <summary>Queue a control message. If the queue is full the client is not keeping up, so the connection is dropped.</summary>
	public void SendText(ControlMessage msg)
	{
		SendText(Protocol.Serialise(msg));
	}

	/// <summary>Queue an already serialised control message. See <see cref="SendText(ControlMessage)"/>.</summary>
	public void SendText(byte[] utf8)
	{
		// A full queue means control messages would be lost, which would desynchronise the client, so drop the connection instead
		if (!m_outbound.Writer.TryWrite((utf8, WebSocketMessageType.Text)) && !m_closing)
			Abort();
	}

	/// <summary>Queue an error reply.</summary>
	public void SendError(string code, string message)
	{
		SendText(new ErrorMsg(code, message));
	}

	/// <summary>Queue a pointer frame. Frames are disposable, so they are dropped when the queue is half full to leave space for control messages.</summary>
	public void SendBinary(byte[] frame)
	{
		if (m_outbound.Reader.Count < RelayLimits.OutboundQueue / 2)
			m_outbound.Writer.TryWrite((frame, WebSocketMessageType.Binary));
	}

	/// <summary>Drop the connection immediately without a close handshake. Safe to call from any thread.</summary>
	public void Abort()
	{
		m_closing = true;
		m_cts.Cancel();
		m_ws.Abort();
	}

	/// <summary>Stop reading and close politely with <paramref name="status"/> once queued messages are sent. Called from the receive loop only.</summary>
	private void Close(WebSocketCloseStatus status)
	{
		m_close_status = status;
		m_closing = true;
		m_outbound.Writer.TryComplete();
	}

	/// <summary>Write queued frames one at a time, then send the close frame when the queue is completed.</summary>
	private async Task SendLoop()
	{
		try
		{
			// Drain the queue until it is completed by a close
			await foreach (var (data, type) in m_outbound.Reader.ReadAllAsync(m_cts.Token))
				await m_ws.SendAsync(data, type, endOfMessage: true, m_cts.Token);

			// Say goodbye if the socket is still usable
			if (m_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
				await m_ws.CloseOutputAsync(m_close_status, null, m_cts.Token);
		}
		catch (Exception)
		{
			// Any send failure means the connection is broken
			Abort();
		}
	}

	/// <summary>Read and dispatch messages until the connection closes or must be closed.</summary>
	private async Task ReceiveLoop()
	{
		// One buffer large enough for the biggest valid message; anything larger is a protocol violation
		var buffer = new byte[Protocol.MaxTextBytes];
		while (!m_closing)
		{
			// Assemble one complete message
			var count = 0;
			ValueWebSocketReceiveResult result;
			do
			{
				if (count == buffer.Length)
				{
					Close(WebSocketCloseStatus.PolicyViolation);
					return;
				}
				result = await m_ws.ReceiveAsync(buffer.AsMemory(count), m_cts.Token);
				count += result.Count;
			}
			while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

			// Dispatch by frame type
			switch (result.MessageType)
			{
				case WebSocketMessageType.Close:
				{
					return;
				}
				case WebSocketMessageType.Binary:
				{
					OnBinary(buffer.AsSpan(0, count));
					break;
				}
				case WebSocketMessageType.Text:
				{
					OnText(buffer.AsSpan(0, count));
					break;
				}
				default:
				{
					throw new InvalidOperationException($"Unknown message type {result.MessageType}");
				}
			}
		}
	}

	/// <summary>Handle one binary frame.</summary>
	private void OnBinary(ReadOnlySpan<byte> data)
	{
		// Pointer frames have a fixed size and are only meaningful after hello
		if (!m_hello || data.Length != PointerFrame.Length)
		{
			Close(WebSocketCloseStatus.PolicyViolation);
			return;
		}

		// Excess frames are dropped silently; only a sustained flood uses up the violation budget
		if (!m_binary_bucket.TryTake())
		{
			Violation(RelayLimits.ExcessBinaryCost);
			return;
		}

		// Frames with bad contents are dropped; valid clients never send them
		if (!PointerFrame.TryRead(data, out var frame))
		{
			Violation(RelayLimits.BadRequestCost);
			return;
		}

		m_hub.OnPointer(this, frame);
	}

	/// <summary>Handle one text frame.</summary>
	private void OnText(ReadOnlySpan<byte> data)
	{
		// Over the rate limit: tell the client, and spend from the violation budget
		if (!m_text_bucket.TryTake())
		{
			SendError(ErrorCodes.RateLimited, "Too many messages");
			Violation(RelayLimits.ExcessTextCost);
			return;
		}

		// Strict validation. Before hello the only acceptable message is a valid hello.
		var valid = Protocol.TryParse(data, out var msg);
		if (!m_hello)
		{
			OnFirstMessage(valid ? msg : null);
			return;
		}
		if (!valid)
		{
			SendError(ErrorCodes.BadRequest, "Invalid message");
			Violation(RelayLimits.BadRequestCost);
			return;
		}

		m_hub.Handle(this, msg!);
	}

	/// <summary>Handle the first text message, which must be a hello with a supported version.</summary>
	private void OnFirstMessage(ControlMessage? msg)
	{
		// Anything other than hello ends the connection
		if (msg is not HelloMsg hello)
		{
			SendError(ErrorCodes.BadRequest, "Expected hello");
			Close(WebSocketCloseStatus.PolicyViolation);
			return;
		}

		// Old clients are told to update
		if (hello.Version < RelayLimits.MinVersion)
		{
			SendError(ErrorCodes.Version, "Please update One-Blind-Mouse");
			Close(WebSocketCloseStatus.PolicyViolation);
			return;
		}

		m_hello = true;
		m_hub.OnHello(this, hello);
	}

	/// <summary>Spend from the violation budget, closing the connection when it runs out.</summary>
	private void Violation(double cost)
	{
		if (!m_violation_bucket.TryTake(cost))
			Close(WebSocketCloseStatus.PolicyViolation);
	}
}
