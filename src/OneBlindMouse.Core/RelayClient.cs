using System.Net.WebSockets;
using System.Threading.Channels;

namespace OneBlindMouse.Core;

/// <summary>Connection state of a <see cref="RelayClient"/>.</summary>
public enum EConnection
{
	/// <summary>Trying to reach the relay or enter a room.</summary>
	Connecting,

	/// <summary>Connected and a member of a room.</summary>
	Online,

	/// <summary>The relay cannot be reached. The client keeps retrying.</summary>
	Offline,
}

/// <summary>Severity of a <see cref="RelayClient.Notice"/>.</summary>
public enum ENotice
{
	Info,
	Warning,
	Error,
}

/// <summary>An immutable view of the client's session.</summary>
/// <param name="Connection">Connection state.</param>
/// <param name="Code">The code of the current room, or null if not in a room.</param>
/// <param name="Me">This client's member id, or 0 if not in a room.</param>
/// <param name="Owner">The member id of the room owner, or 0 if not in a room.</param>
/// <param name="Members">All members of the room, including this client.</param>
/// <param name="Direct">The members whose pointer samples travel directly rather than through the relay.</param>
public sealed record SessionSnapshot(EConnection Connection, SessionCode? Code, int Me, int Owner, IReadOnlyList<MemberInfo> Members, IReadOnlyCollection<int> Direct)
{
	/// <summary>True if this client owns the room.</summary>
	public bool IsOwner => Me != 0 && Me == Owner;

	/// <summary>True if anyone else is in the room.</summary>
	public bool IsShared => Members.Count > 1;

	/// <summary>This client's member info, if in a room.</summary>
	public MemberInfo? Self => Members.FirstOrDefault(m => m.Id == Me);

	/// <summary>How pointer samples travel between this client and <paramref name="member"/>.</summary>
	public EPath PathTo(int member)
	{
		return Direct.Contains(member) ? EPath.Direct : EPath.Relayed;
	}
}

/// <summary>
/// Client side of the relay protocol. Every client is always in a room: its own room by default, or another room after joining a code.
/// The client reconnects automatically: it first resumes its member slot, then re-joins the room by code, and finally recreates the room
/// (if it was the owner) or falls back to its own room.
/// All protocol state is owned by one internal loop. Public methods may be called from any thread and only queue work.
/// Events are raised on internal threads; UI callers must marshal them.
///
/// With peer-to-peer enabled, pointer samples go directly to members that a <see cref="PeerTransport"/> can reach, and through the relay to the rest.
/// While paths change, a sample may arrive by both routes, so received samples are de-duplicated by sequence number per member.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
	/// <summary>Opens a WebSocket to the relay endpoint.</summary>
	public delegate Task<WebSocket> ConnectFn(CancellationToken ct);

	/// <summary>The requests that expect a 'joined' or 'error' reply. Only one is outstanding at a time.</summary>
	private enum ERequest
	{
		None,
		Create,
		Join,
		Resume,
	}

	/// <summary>Work for the protocol loop.</summary>
	private abstract record Item;
	private sealed record CommandItem(Action Action) : Item;
	private sealed record MessageItem(int Gen, ControlMessage Msg) : Item;
	private sealed record ClosedItem(int Gen) : Item;

	/// <summary>How long to wait between re-join attempts while a room's owner may still be reconnecting.</summary>
	private static readonly TimeSpan RejoinInterval = TimeSpan.FromSeconds(2);

	/// <summary>How many re-join attempts to make before falling back to the client's own room.</summary>
	private const int MaxRejoinAttempts = 10;

	private readonly ConnectFn m_connect;
	private readonly Channel<Item> m_items = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
	private readonly CancellationTokenSource m_cts = new();
	private Task? m_run;

	// Protocol loop state. Only touched by the protocol loop.
	private Connection? m_conn;
	private volatile int m_gen;
	private string m_name;
	private readonly int m_colour_pref;
	private SessionCode m_own_code;
	private SessionCode? m_code;
	private int m_owner;
	private string? m_token;
	private readonly List<MemberInfo> m_members = [];
	private ERequest m_request;
	private SessionCode? m_request_code;
	private bool m_request_by_user;
	private SessionCode? m_pending_join;
	private bool m_pending_leave;
	private int m_resume_member;
	private int m_rejoin_attempts;
	private bool m_was_owner;
	private (double Aspect, string Label) m_target = (0, string.Empty);
	private bool m_version_rejected;
	private int m_epoch;

	// Read from other threads
	private volatile int m_me;
	private volatile bool m_shared;
	private volatile Connection? m_live;
	private readonly PeerTransport? m_peers;

	// The newest sequence number delivered from each member, across both routes. Guarded by m_seq_lock.
	private readonly Lock m_seq_lock = new();
	private readonly Dictionary<int, ushort> m_last_seq = [];

	/// <summary>Create a client. Call <see cref="Start"/> to connect. With <paramref name="peers"/>, pointer samples go directly between members where possible.</summary>
	public RelayClient(ConnectFn connect, string name, int colour_pref = -1, PeerOptions? peers = null)
	{
		m_connect = connect;
		m_name = Names.SanitiseOr(name, "Anonymous");
		m_colour_pref = Palette.IsValidSlot(colour_pref) ? colour_pref : -1;
		m_own_code = new SessionCode(SessionCode.NewRoom(), SessionCode.NewSecret());
		Snapshot = new SessionSnapshot(EConnection.Connecting, null, 0, 0, [], []);

		// Direct delivery is an optimisation, so a machine that cannot open a UDP socket just uses the relay
		if (peers is not null)
		{
			try
			{
				m_peers = new PeerTransport(peers, SendSignal, Deliver, () => Post(() => Publish(Snapshot.Connection)));
			}
			catch (System.Net.Sockets.SocketException)
			{
				m_peers = null;
			}
		}
	}

	/// <summary>The latest session state. Also delivered by <see cref="SessionChanged"/>.</summary>
	public SessionSnapshot Snapshot { get; private set; }

	/// <summary>Raised when the connection state, room, code or member list changes.</summary>
	public event Action<SessionSnapshot>? SessionChanged;

	/// <summary>Raised for each valid pointer sample sent to this client by another member.</summary>
	public event Action<PointerFrame>? PointerReceived;

	/// <summary>Raised with user-facing messages such as "Alice joined".</summary>
	public event Action<ENotice, string>? Notice;

	/// <summary>Create a <see cref="ConnectFn"/> that opens a client WebSocket to <paramref name="relay"/>.</summary>
	public static ConnectFn WebSocketConnector(Uri relay)
	{
		return async ct =>
		{
			// Keep-alive pings detect dead connections that TCP alone would not notice for minutes
			var ws = new ClientWebSocket();
			ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
			ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
			try
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeout.CancelAfter(TimeSpan.FromSeconds(15));
				await ws.ConnectAsync(relay, timeout.Token);
				return ws;
			}
			catch
			{
				ws.Dispose();
				throw;
			}
		};
	}

	/// <summary>Start connecting. The client creates its own room once connected.</summary>
	public void Start()
	{
		m_run ??= Task.Run(() => RunAsync(m_cts.Token));
	}

	/// <summary>Join the room for <paramref name="code"/>. On failure the client stays in its current room.</summary>
	public void Join(SessionCode code)
	{
		Post(() =>
		{
			// Joining the room we are already in is a no-op
			if (m_code == code && m_me != 0)
			{
				Notify(ENotice.Info, $"Already in session {code}");
				return;
			}

			// Queue the join; it is sent now if idle, or when the current request completes or the connection returns
			m_pending_join = code;
			m_pending_leave = false;
			SendPending();
		});
	}

	/// <summary>Leave the current session and return to this client's own room.</summary>
	public void Leave()
	{
		Post(() =>
		{
			// Already in our own room with no one else is a no-op
			m_pending_join = null;
			if (m_code?.Room == m_own_code.Room && m_members.Count <= 1)
				return;

			// Sent now if idle, otherwise when the current request completes or the connection returns
			m_pending_leave = true;
			SendPending();
		});
	}

	/// <summary>Replace the room secret, invalidating the old code (owner only).</summary>
	public void NewCode()
	{
		Post(() =>
		{
			// Only the owner may rekey; the relay enforces this too
			if (m_conn is null || m_code is not { } code || m_owner != m_me)
			{
				Notify(ENotice.Warning, "Only the session owner can change the code");
				return;
			}

			var secret = SessionCode.NewSecret();
			m_conn.SendText(new RekeyMsg(SessionCode.ComputeVerifier(code.Room, secret), secret));
		});
	}

	/// <summary>Remove a member from the session (owner only).</summary>
	public void Kick(int member)
	{
		Post(() =>
		{
			if (m_conn is not null && member != m_me)
				m_conn.SendText(new KickMsg(member));
		});
	}

	/// <summary>Describe this client's pointer target area to other members.</summary>
	public void SetTarget(double aspect, string label)
	{
		Post(() =>
		{
			// Normalise into the protocol's ranges so a bad caller value cannot get the connection rejected
			aspect = Protocol.IsValidAspect(aspect) ? aspect : 0;
			label = Names.Sanitise(label, Protocol.MaxLabelLength);
			if (m_target == (aspect, label))
				return;

			m_target = (aspect, label);
			if (m_conn is not null && m_me != 0)
				m_conn.SendText(new TargetMsg(aspect, label));
		});
	}

	/// <summary>Change this client's display name.</summary>
	public void Rename(string name)
	{
		Post(() =>
		{
			m_name = Names.SanitiseOr(name, "Anonymous");
			if (m_conn is not null)
				m_conn.SendText(new RenameMsg(m_name));
		});
	}

	/// <summary>
	/// Send a pointer sample to another member (or 0 for everyone). The 'From' field is ignored; the receiver learns it from the relay or the direct path.
	/// Returns false if the sample was dropped because the client is not in a shared session or the connection is congested.
	/// </summary>
	public bool SendPointer(PointerFrame frame)
	{
		// Samples are frequent and disposable, so they bypass the protocol loop and are dropped rather than queued when not deliverable
		var conn = m_live;
		if (conn is null || m_me == 0 || !m_shared)
			return false;

		// Directly where possible. The relay carries the sample to anyone without a direct path; members that got it directly drop the copy.
		frame = frame with { From = 0 };
		if (m_peers is not null && m_peers.TrySend(frame))
			return true;

		return conn.TrySendBinary(frame);
	}

	/// <summary>Leave the session and close the connection.</summary>
	public async ValueTask DisposeAsync()
	{
		// Ask the loop to say goodbye, then close politely before stopping the loop
		var closing = new TaskCompletionSource<Connection?>();
		Post(() =>
		{
			m_conn?.SendText(new LeaveMsg());
			closing.TrySetResult(m_conn);
		});
		if (m_run is not null && await Task.WhenAny(closing.Task, Task.Delay(500)) == closing.Task && closing.Task.Result is { } conn)
			await conn.CloseAsync(TimeSpan.FromSeconds(1));

		m_cts.Cancel();
		if (m_run is not null)
		{
			try
			{
				await m_run;
			}
			catch (OperationCanceledException)
			{
			}
		}
		m_peers?.Dispose();
		m_cts.Dispose();
	}

	/// <summary>Queue an action for the protocol loop.</summary>
	private void Post(Action action)
	{
		m_items.Writer.TryWrite(new CommandItem(action));
	}

	/// <summary>The protocol loop: connect, process messages until disconnected, back off, repeat.</summary>
	private async Task RunAsync(CancellationToken ct)
	{
		// Count consecutive failures for exponential back-off. A connection that reaches a room resets the count.
		var failures = 0;
		while (!ct.IsCancellationRequested)
		{
			// Open the socket
			Publish(failures == 0 ? EConnection.Connecting : EConnection.Offline);
			try
			{
				var ws = await m_connect(ct);
				m_conn = new Connection(ws, ++m_gen, this);
				m_live = m_conn;
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (Exception)
			{
				await DrainWhileWaiting(Backoff(++failures), ct);
				continue;
			}

			// Introduce ourselves and get into a room
			m_conn.SendText(new HelloMsg(Protocol.Version, m_name, m_colour_pref));
			Establish();

			// Process work until this connection closes
			var reached_room = false;
			try
			{
				while (await m_items.Reader.WaitToReadAsync(ct))
				{
					if (!m_items.Reader.TryRead(out var item))
						continue;

					if (item is ClosedItem closed && closed.Gen == m_gen)
						break;

					Process(item);
					reached_room |= m_me != 0;
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
			}

			// Tear down the connection. Membership details are kept so the next connection can resume.
			var conn = m_conn;
			m_conn = null;
			m_live = null;
			m_me = 0;
			m_request = ERequest.None;
			await conn.DisposeAsync();
			if (ct.IsCancellationRequested)
				break;

			failures = reached_room ? 0 : failures + 1;
			Publish(EConnection.Offline);
			await DrainWhileWaiting(m_version_rejected ? TimeSpan.FromMinutes(10) : Backoff(Math.Max(failures, 1)), ct);
		}
	}

	/// <summary>Wait for <paramref name="delay"/> while still running commands, so user actions update intent while offline.</summary>
	private async Task DrainWhileWaiting(TimeSpan delay, CancellationToken ct)
	{
		// Commands only change intent while there is no connection; stale messages from old connections are ignored
		using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timer.CancelAfter(delay);
		try
		{
			while (await m_items.Reader.WaitToReadAsync(timer.Token))
			{
				while (m_items.Reader.TryRead(out var item))
				{
					if (item is CommandItem)
						Process(item);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Exponential back-off with jitter: about 0.5s, 1s, 2s, ... up to 30s.</summary>
	private static TimeSpan Backoff(int failures)
	{
		var seconds = Math.Min(30.0, 0.5 * Math.Pow(2, Math.Min(failures - 1, 6)));
		return TimeSpan.FromSeconds(seconds * (0.75 + 0.5 * Random.Shared.NextDouble()));
	}

	/// <summary>Handle one work item on the protocol loop.</summary>
	private void Process(Item item)
	{
		switch (item)
		{
			case CommandItem cmd:
			{
				cmd.Action();
				break;
			}
			case MessageItem msg:
			{
				// Messages from a previous connection are meaningless now
				if (msg.Gen == m_gen && m_conn is not null)
					HandleMessage(msg.Msg);

				break;
			}
			case ClosedItem:
			{
				// A close from an old connection; the current one is handled by the run loop
				break;
			}
			default:
			{
				throw new InvalidOperationException($"Unknown work item {item.GetType().Name}");
			}
		}
	}

	/// <summary>Get into a room on a fresh connection, using the best available option.</summary>
	private void Establish()
	{
		// A user request made while offline takes priority over returning to the previous room
		if (m_pending_leave)
			LeaveRoomState();

		if (m_pending_join is not null || m_pending_leave)
		{
			SendPending();
			return;
		}

		// Reclaim our member slot if we were in a room recently; the relay holds it briefly after a disconnect
		if (m_code is { } code && m_token is { } token && m_resume_member != 0)
		{
			m_request = ERequest.Resume;
			m_request_code = code;
			m_request_by_user = false;
			m_conn!.SendText(new ResumeMsg(code.Room, m_resume_member, token));
			return;
		}

		// Otherwise re-join the previous room by code, or create our own
		if (m_code is { } previous)
			SendJoin(previous, by_user: false);
		else
			SendCreate(m_own_code, by_user: false);
	}

	/// <summary>Send the queued user join or leave if the connection is idle.</summary>
	private void SendPending()
	{
		// Requests are strictly one at a time, because replies do not identify the request they answer
		if (m_conn is null || m_request != ERequest.None)
			return;

		if (m_pending_leave)
		{
			m_pending_leave = false;
			SendCreate(m_own_code, by_user: true);
		}
		else if (m_pending_join is { } code)
		{
			m_pending_join = null;
			SendJoin(code, by_user: true);
		}
	}

	/// <summary>Send a join request for <paramref name="code"/>.</summary>
	private void SendJoin(SessionCode code, bool by_user)
	{
		m_request = ERequest.Join;
		m_request_code = code;
		m_request_by_user = by_user;
		m_conn!.SendText(new JoinMsg(code.Room, code.Verifier));
	}

	/// <summary>Send a create request for the room in <paramref name="code"/>.</summary>
	private void SendCreate(SessionCode code, bool by_user)
	{
		m_request = ERequest.Create;
		m_request_code = code;
		m_request_by_user = by_user;
		m_conn!.SendText(new CreateMsg(code.Room, code.Verifier));
	}

	/// <summary>Apply a validated message from the relay.</summary>
	private void HandleMessage(ControlMessage msg)
	{
		switch (msg)
		{
			case WelcomeMsg welcome:
			{
				// The relay accepted our version
				m_version_rejected = false;
				if (welcome.MinVersion > Protocol.Version)
					Notify(ENotice.Warning, "A newer version of One-Blind-Mouse is available");

				break;
			}
			case JoinedMsg joined:
			{
				OnJoined(joined);
				break;
			}
			case ErrorMsg error:
			{
				OnError(error);
				break;
			}
			case MemberJoinedMsg added:
			{
				// Replace any stale entry with the same id
				m_members.RemoveAll(m => m.Id == added.Member.Id);
				m_members.Add(added.Member);
				Notify(ENotice.Info, $"{added.Member.Name} joined");
				Publish(EConnection.Online);
				break;
			}
			case MemberUpdatedMsg updated:
			{
				var index = m_members.FindIndex(m => m.Id == updated.Member.Id);
				if (index >= 0)
					m_members[index] = updated.Member;

				Publish(EConnection.Online);
				break;
			}
			case MemberLeftMsg left:
			{
				var member = m_members.Find(m => m.Id == left.Member);
				m_members.RemoveAll(m => m.Id == left.Member);
				if (member is not null)
					Notify(ENotice.Info, $"{member.Name} left");

				Publish(EConnection.Online);
				break;
			}
			case OwnerChangedMsg owner:
			{
				m_owner = owner.Owner;
				m_was_owner = m_owner == m_me;
				Publish(EConnection.Online);
				break;
			}
			case RekeyedMsg rekeyed:
			{
				// The code changed; our own room keeps its id, so remember the new secret for it too
				if (m_code is not { } code)
					break;

				m_code = new SessionCode(code.Room, rekeyed.Secret);
				if (code.Room == m_own_code.Room)
					m_own_code = m_code.Value;

				Notify(ENotice.Info, $"Session code changed to {m_code}");
				Publish(EConnection.Online);
				break;
			}
			case LockedMsg:
			{
				// Too many wrong codes were tried; replace the secret so guessing has to start again
				Notify(ENotice.Warning, "Several wrong codes were tried for your session, so it has a new code");
				if (m_conn is not null && m_code is { } code)
				{
					var secret = SessionCode.NewSecret();
					m_conn.SendText(new RekeyMsg(SessionCode.ComputeVerifier(code.Room, secret), secret));
				}
				break;
			}
			case KickedMsg:
			{
				// We are no longer in any room; go back to our own
				Notify(ENotice.Warning, "You were removed from the session");
				LeaveRoomState();
				SendCreate(m_own_code, by_user: false);
				break;
			}
			case PeerMsg peer:
			{
				// Direct-path signalling from another member; ignored when peer-to-peer is off
				m_peers?.OnSignal(peer);
				break;
			}
			default:
			{
				// Client-to-relay message types are never valid from the relay; ignore them
				break;
			}
		}
	}

	/// <summary>Handle a successful create, join or resume.</summary>
	private void OnJoined(JoinedMsg joined)
	{
		// Adopt the room state
		var request = m_request;
		var by_user = m_request_by_user;
		var code = request == ERequest.None || m_request_code is null ? m_code : m_request_code;
		if (code is null || code.Value.Room != joined.Room)
		{
			// A 'joined' we did not ask for, or for a different room; the relay is confused, so reconnect
			m_conn?.Abort();
			return;
		}
		m_request = ERequest.None;
		m_code = code;
		m_me = joined.Member;
		m_resume_member = joined.Member;
		m_owner = joined.Owner;
		m_was_owner = joined.Owner == joined.Member;
		m_token = joined.Token;
		m_rejoin_attempts = 0;
		m_members.Clear();
		m_members.AddRange(joined.Members);

		// Entering a room other than by resuming means member ids may now belong to different people, so direct paths and sequence history restart
		if (!joined.Resumed)
		{
			++m_epoch;
			lock (m_seq_lock)
			{
				m_last_seq.Clear();
			}
		}

		// Tell the room about our pointer target
		if (m_target.Aspect != 0 || m_target.Label.Length != 0)
			m_conn!.SendText(new TargetMsg(m_target.Aspect, m_target.Label));

		// Report the change
		if (request == ERequest.Join && by_user)
			Notify(ENotice.Info, $"Joined session {code}");
		else if (request == ERequest.Create && by_user)
			Notify(ENotice.Info, "Back in your own session");

		Publish(EConnection.Online);

		// A join or leave the user asked for while this request was in flight
		SendPending();
	}

	/// <summary>Handle an error reply.</summary>
	private void OnError(ErrorMsg error)
	{
		// Version errors are fatal for this build; slow the retries right down
		if (error.Code == ErrorCodes.Version)
		{
			m_version_rejected = true;
			Notify(ENotice.Error, "This version of One-Blind-Mouse is too old. Please download the latest version.");
			m_conn?.Abort();
			return;
		}

		// Errors not related to a room request are only reported
		var request = m_request;
		if (request == ERequest.None || error.Code == ErrorCodes.NotOwner)
		{
			Notify(ENotice.Warning, error.Message);
			return;
		}

		var code = m_request_code!.Value;
		var by_user = m_request_by_user;
		m_request = ERequest.None;
		switch (request)
		{
			case ERequest.Resume:
			{
				// The slot has expired (or the relay restarted); try the code instead
				m_token = null;
				SendJoin(code, by_user: false);
				break;
			}
			case ERequest.Join:
			{
				OnJoinFailed(error, code, by_user);
				break;
			}
			case ERequest.Create:
			{
				OnCreateFailed(error, code);
				break;
			}
			default:
			{
				throw new InvalidOperationException($"Unknown request {request}");
			}
		}
	}

	/// <summary>Handle a failed join request.</summary>
	private void OnJoinFailed(ErrorMsg error, SessionCode code, bool by_user)
	{
		// A user join that fails leaves us where we were, unless we are not in any room
		if (by_user)
		{
			Notify(ENotice.Warning, JoinErrorText(error));
			if (m_me == 0)
				Establish();
			else
				SendPending();

			return;
		}

		// Re-joining after a disconnect. If the room is gone the relay probably restarted: the owner recreates it and others wait for that.
		if (error.Code == ErrorCodes.NoRoom && m_was_owner)
		{
			SendCreate(code, by_user: false);
			return;
		}
		if (error.Code == ErrorCodes.NoRoom && m_rejoin_attempts < MaxRejoinAttempts)
		{
			++m_rejoin_attempts;
			var gen = m_gen;
			_ = Task.Delay(RejoinInterval).ContinueWith(_ => Post(() =>
			{
				// Only retry if nothing else has happened in the meantime
				if (m_gen == gen && m_conn is not null && m_request == ERequest.None && m_me == 0 && m_code == code)
				{
					if (m_pending_join is not null || m_pending_leave)
						Establish();
					else
						SendJoin(code, by_user: false);
				}
			}));
			return;
		}

		// Give up on the old session
		Notify(ENotice.Warning, $"Lost the connection to session {code}");
		LeaveRoomState();
		SendCreate(m_own_code, by_user: false);
	}

	/// <summary>Handle a failed create request.</summary>
	private void OnCreateFailed(ErrorMsg error, SessionCode code)
	{
		// Our own room id is taken by someone else; pick another id (the code changes)
		if (error.Code == ErrorCodes.RoomUnavailable && code.Room == m_own_code.Room && m_code?.Room != code.Room)
		{
			m_own_code = new SessionCode(SessionCode.NewRoom(), m_own_code.Secret);
			SendCreate(m_own_code, by_user: false);
			return;
		}

		// Recreating a shared room failed because another member got there first; join it instead
		if (error.Code == ErrorCodes.RoomUnavailable)
		{
			m_was_owner = false;
			SendJoin(code, by_user: false);
			return;
		}

		// Anything else (e.g. rate limiting) is retried by reconnecting
		Notify(ENotice.Warning, error.Message);
		m_conn?.Abort();
	}

	/// <summary>Forget the current room.</summary>
	private void LeaveRoomState()
	{
		m_code = null;
		m_token = null;
		m_me = 0;
		m_resume_member = 0;
		m_owner = 0;
		m_was_owner = false;
		m_members.Clear();
		m_rejoin_attempts = 0;
	}

	/// <summary>A user-facing description of a join failure.</summary>
	private static string JoinErrorText(ErrorMsg error)
	{
		return error.Code switch
		{
			ErrorCodes.NoRoom => "No session has that code",
			ErrorCodes.BadCode => "That code is not correct",
			ErrorCodes.RoomFull => "That session is full",
			ErrorCodes.RoomLocked => "That session is locked. Ask for a new code.",
			ErrorCodes.RateLimited => "Too many attempts. Please wait a minute.",
			_ => error.Message,
		};
	}

	/// <summary>Raise <see cref="Notice"/>.</summary>
	private void Notify(ENotice level, string text)
	{
		Notice?.Invoke(level, text);
	}

	/// <summary>Publish a new snapshot.</summary>
	private void Publish(EConnection connection)
	{
		// The connection state is 'Online' only while in a room
		if (connection == EConnection.Online && m_me == 0)
			connection = EConnection.Connecting;

		var members = m_members.OrderBy(m => m.Id).ToArray();
		m_shared = m_me != 0 && members.Length > 1;

		// Keep direct paths in step with the member list. They are kept while the relay connection is down, so a quick resume needs no new handshake.
		var ids = members.Select(m => m.Id).ToHashSet();
		if (m_peers is not null && m_me != 0)
			m_peers.Update(m_epoch, m_me, ids);

		// Forget the sequence history of members who left, because their ids can be reused
		lock (m_seq_lock)
		{
			foreach (var id in m_last_seq.Keys.Where(id => !ids.Contains(id)).ToList())
				m_last_seq.Remove(id);
		}

		Snapshot = new SessionSnapshot(connection, m_code, m_me, m_owner, members, m_peers?.DirectMembers() ?? []);
		SessionChanged?.Invoke(Snapshot);
	}

	/// <summary>Raise <see cref="PointerReceived"/> from a connection's receive loop.</summary>
	private void OnPointer(int gen, PointerFrame frame)
	{
		// Ignore our own echoes and samples from a previous connection
		if (gen != m_gen || frame.From == 0 || frame.From == m_me)
			return;

		Deliver(frame);
	}

	/// <summary>Raise <see cref="PointerReceived"/> for a sample from either route, unless a newer sample from that member was already delivered.</summary>
	private void Deliver(PointerFrame frame)
	{
		// Raised under the lock so samples from the two routes reach the handler in sequence order
		lock (m_seq_lock)
		{
			if (m_last_seq.TryGetValue(frame.From, out var last) && !PointerFrame.IsNewer(frame.Seq, last))
				return;

			m_last_seq[frame.From] = frame.Seq;
			PointerReceived?.Invoke(frame);
		}
	}

	/// <summary>Forward a direct-path signal to the relay. Signals are dropped while not in a room; the transport repeats unanswered offers.</summary>
	private void SendSignal(PeerMsg msg)
	{
		Post(() =>
		{
			if (m_conn is not null && m_me != 0)
				m_conn.SendText(msg);
		});
	}

	/// <summary>One WebSocket connection with its own send and receive loops.</summary>
	private sealed class Connection : IAsyncDisposable
	{
		private readonly WebSocket m_ws;
		private readonly int m_gen;
		private readonly RelayClient m_client;
		private readonly Channel<(byte[] Data, WebSocketMessageType Type)> m_outbound;
		private readonly CancellationTokenSource m_cts = new();
		private readonly Task m_send_loop;
		private readonly Task m_recv_loop;

		public Connection(WebSocket ws, int gen, RelayClient owner)
		{
			// Bounded so a stalled connection cannot grow memory; pointer samples are dropped first when it fills
			m_ws = ws;
			m_gen = gen;
			m_client = owner;
			m_outbound = Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
			m_send_loop = Task.Run(SendLoop);
			m_recv_loop = Task.Run(ReceiveLoop);
		}

		/// <summary>Queue a control message. If the queue is full the connection is unusable, so it is aborted and will be re-established.</summary>
		public void SendText(ControlMessage msg)
		{
			if (!m_outbound.Writer.TryWrite((Protocol.Serialise(msg), WebSocketMessageType.Text)))
				Abort();
		}

		/// <summary>Queue a pointer sample. Returns false if dropped.</summary>
		public bool TrySendBinary(PointerFrame frame)
		{
			return m_outbound.Reader.Count < 32 && m_outbound.Writer.TryWrite((frame.ToArray(), WebSocketMessageType.Binary));
		}

		/// <summary>Close the socket abruptly.</summary>
		public void Abort()
		{
			m_cts.Cancel();
			m_ws.Abort();
		}

		/// <summary>Flush queued messages and close politely, giving up after <paramref name="timeout"/>.</summary>
		public async Task CloseAsync(TimeSpan timeout)
		{
			// Let the send loop drain, then send a close frame
			m_outbound.Writer.TryComplete();
			using var cts = new CancellationTokenSource(timeout);
			try
			{
				await m_send_loop.WaitAsync(cts.Token);
				await m_ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
			}
			catch (Exception)
			{
				// Closing is best effort
			}
		}

		public async ValueTask DisposeAsync()
		{
			Abort();
			m_outbound.Writer.TryComplete();
			await Task.WhenAll(m_send_loop, m_recv_loop).ConfigureAwait(false);
			m_ws.Dispose();
			m_cts.Dispose();
		}

		/// <summary>Write queued frames to the socket one at a time (WebSocket allows only one send at a time).</summary>
		private async Task SendLoop()
		{
			try
			{
				await foreach (var (data, type) in m_outbound.Reader.ReadAllAsync(m_cts.Token))
					await m_ws.SendAsync(data, type, endOfMessage: true, m_cts.Token);
			}
			catch (Exception)
			{
				// Any send failure means the connection is broken
				Abort();
			}
		}

		/// <summary>Read frames, validate them, and pass them to the client.</summary>
		private async Task ReceiveLoop()
		{
			// One buffer large enough for the biggest valid message; anything larger is a protocol violation
			var buffer = new byte[Protocol.MaxTextBytes];
			try
			{
				while (!m_cts.IsCancellationRequested)
				{
					// Assemble one complete message
					var count = 0;
					ValueWebSocketReceiveResult result;
					do
					{
						if (count == buffer.Length)
							throw new WebSocketException("Message too large");

						result = await m_ws.ReceiveAsync(buffer.AsMemory(count), m_cts.Token);
						count += result.Count;
					}
					while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

					// Dispatch by frame type. Invalid data from the relay ends the connection.
					switch (result.MessageType)
					{
						case WebSocketMessageType.Close:
						{
							return;
						}
						case WebSocketMessageType.Binary:
						{
							if (!PointerFrame.TryRead(buffer.AsSpan(0, count), out var frame))
								return;

							m_client.OnPointer(m_gen, frame);
							break;
						}
						case WebSocketMessageType.Text:
						{
							if (!Protocol.TryParse(buffer.AsSpan(0, count), out var msg))
								return;

							m_client.m_items.Writer.TryWrite(new MessageItem(m_gen, msg!));
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
				// Network errors and protocol violations both just end the connection
			}
			finally
			{
				m_cts.Cancel();
				m_client.m_items.Writer.TryWrite(new ClosedItem(m_gen));
			}
		}
	}
}
