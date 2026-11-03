using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Hosts the relay in memory with a controllable clock.</summary>
public sealed class RelayFactory : WebApplicationFactory<Program>
{
	/// <summary>The relay's clock. Advance it to expire holds or refill rate limits.</summary>
	public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

	/// <summary>The relay WebSocket endpoint on the test server.</summary>
	public Uri RelayUri => new(Server.BaseAddress, Protocol.Path);

	/// <summary>Open a raw WebSocket to the relay.</summary>
	public Task<WebSocket> ConnectAsync(CancellationToken ct = default)
	{
		return Server.CreateWebSocketClient().ConnectAsync(RelayUri, ct);
	}

	/// <summary>Open a test socket and complete the hello handshake.</summary>
	public async Task<TestSocket> HelloAsync(string name, int colour_pref = -1)
	{
		var socket = new TestSocket(await ConnectAsync());
		await socket.SendAsync(new HelloMsg(Protocol.Version, name, colour_pref));
		await socket.ExpectAsync<WelcomeMsg>();
		return socket;
	}

	protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
	{
		builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
	}
}

/// <summary>A raw protocol client for relay tests. Received messages are queued in arrival order.</summary>
public sealed class TestSocket : IAsyncDisposable
{
	/// <summary>How long to wait for an expected message before failing.</summary>
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	private readonly Channel<object> m_received = Channel.CreateUnbounded<object>();
	private readonly Task m_recv_loop;

	/// <summary>Wrap a connected WebSocket and start reading.</summary>
	public TestSocket(WebSocket ws)
	{
		Socket = ws;
		m_recv_loop = Task.Run(ReceiveLoop);
	}

	/// <summary>The underlying socket.</summary>
	public WebSocket Socket { get; }

	/// <summary>The close status sent by the relay, once closed.</summary>
	public WebSocketCloseStatus? CloseStatus { get; private set; }

	/// <summary>Send a control message.</summary>
	public Task SendAsync(ControlMessage msg)
	{
		return SendRawAsync(Protocol.Serialise(msg), WebSocketMessageType.Text);
	}

	/// <summary>Send a pointer frame.</summary>
	public Task SendAsync(PointerFrame frame)
	{
		return SendRawAsync(frame.ToArray(), WebSocketMessageType.Binary);
	}

	/// <summary>Send arbitrary bytes.</summary>
	public Task SendRawAsync(byte[] data, WebSocketMessageType type)
	{
		return Socket.SendAsync(data, type, true, CancellationToken.None);
	}

	/// <summary>The next received item: a <see cref="ControlMessage"/>, a <see cref="PointerFrame"/>, or <see cref="Closed"/>.</summary>
	public async Task<object> NextAsync()
	{
		using var cts = new CancellationTokenSource(Timeout);
		return await m_received.Reader.ReadAsync(cts.Token);
	}

	/// <summary>Read until a message of type <typeparamref name="T"/> arrives, skipping others. Fails if the socket closes first.</summary>
	public async Task<T> ExpectAsync<T>(Func<T, bool>? match = null)
	{
		for (;;)
		{
			// Skip unrelated traffic such as member updates
			var item = await NextAsync();
			if (item is T t && (match is null || match(t)))
				return t;

			if (item is Closed)
				Assert.Fail($"Socket closed while waiting for {typeof(T).Name}");
		}
	}

	/// <summary>Read until an error with <paramref name="code"/> arrives.</summary>
	public Task<ErrorMsg> ExpectErrorAsync(string code)
	{
		return ExpectAsync<ErrorMsg>(e => e.Code == code);
	}

	/// <summary>Read until the relay closes the socket.</summary>
	public async Task ExpectClosedAsync()
	{
		while (await NextAsync() is not Closed)
		{
		}
	}

	/// <summary>Close politely and wait for the relay's close frame, so the relay has processed the disconnect on return.</summary>
	public async Task CloseAsync()
	{
		using var cts = new CancellationTokenSource(Timeout);
		await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
		await ExpectClosedAsync();
	}

	public async ValueTask DisposeAsync()
	{
		Socket.Abort();
		await m_recv_loop;
		Socket.Dispose();
	}

	/// <summary>Marker queued when the socket closes.</summary>
	public sealed record Closed;

	/// <summary>Decode frames into the received queue.</summary>
	private async Task ReceiveLoop()
	{
		var buffer = new byte[64 * 1024];
		try
		{
			for (;;)
			{
				// Assemble one message
				var count = 0;
				WebSocketReceiveResult result;
				do
				{
					result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), CancellationToken.None);
					count += result.Count;
				}
				while (!result.EndOfMessage);

				// Queue it decoded; anything undecodable is a relay bug
				switch (result.MessageType)
				{
					case WebSocketMessageType.Close:
					{
						CloseStatus = result.CloseStatus;
						return;
					}
					case WebSocketMessageType.Binary:
					{
						Assert.True(PointerFrame.TryRead(buffer.AsSpan(0, count), out var frame));
						m_received.Writer.TryWrite(frame);
						break;
					}
					case WebSocketMessageType.Text:
					{
						Assert.True(Protocol.TryParse(buffer.AsSpan(0, count), out var msg));
						m_received.Writer.TryWrite(msg!);
						break;
					}
					default:
					{
						throw new InvalidOperationException($"Unknown message type {result.MessageType}");
					}
				}
			}
		}
		catch (Exception)
		{
			// Aborted or broken; either way the socket is finished
		}
		finally
		{
			m_received.Writer.TryWrite(new Closed());
		}
	}
}
