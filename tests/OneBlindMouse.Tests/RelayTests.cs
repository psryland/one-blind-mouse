using System.Net;
using System.Net.WebSockets;
using System.Text;
using OneBlindMouse.Core;
using OneBlindMouse.Relay;

namespace OneBlindMouse.Tests;

/// <summary>Integration tests of the relay over in-memory WebSockets. Each test gets its own relay so per-IP limits do not interact.</summary>
public sealed class RelayTests : IAsyncLifetime
{
	private readonly RelayFactory m_relay = new();
	private readonly List<TestSocket> m_sockets = [];

	public Task InitializeAsync()
	{
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		foreach (var socket in m_sockets)
			await socket.DisposeAsync();

		await m_relay.DisposeAsync();
	}

	/// <summary>Connect and say hello.</summary>
	private async Task<TestSocket> Hello(string name, int colour_pref = -1)
	{
		var socket = await m_relay.HelloAsync(name, colour_pref);
		m_sockets.Add(socket);
		return socket;
	}

	/// <summary>Connect, say hello and create a room with <paramref name="code"/>.</summary>
	private async Task<(TestSocket Socket, JoinedMsg Joined)> Create(string name, SessionCode code)
	{
		var socket = await Hello(name);
		await socket.SendAsync(new CreateMsg(code.Room, code.Verifier));
		return (socket, await socket.ExpectAsync<JoinedMsg>());
	}

	/// <summary>Connect, say hello and join the room for <paramref name="code"/>.</summary>
	private async Task<(TestSocket Socket, JoinedMsg Joined)> Join(string name, SessionCode code)
	{
		var socket = await Hello(name);
		await socket.SendAsync(new JoinMsg(code.Room, code.Verifier));
		return (socket, await socket.ExpectAsync<JoinedMsg>());
	}

	/// <summary>A pointer frame with a recognisable sequence number.</summary>
	private static PointerFrame Pointer(ushort seq, byte to = 0)
	{
		return new PointerFrame(From: 99, To: to, Seq: seq, X: 100, Y: 200, Flags: EPointerFlags.Visible, Size: 100);
	}

	private static readonly SessionCode Code = new("K7Q", "M4PZX");

	[Fact]
	public async Task HealthCheckReturnsOk()
	{
		var client = m_relay.CreateClient();
		Assert.Equal("ok", await client.GetStringAsync("/healthz"));
		Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Protocol.Path)).StatusCode);
	}

	[Fact]
	public async Task FirstMessageMustBeHello()
	{
		var socket = new TestSocket(await m_relay.ConnectAsync());
		m_sockets.Add(socket);
		await socket.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		await socket.ExpectErrorAsync(ErrorCodes.BadRequest);
		await socket.ExpectClosedAsync();
		Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
	}

	[Fact]
	public async Task InvalidHelloIsRejected()
	{
		var socket = new TestSocket(await m_relay.ConnectAsync());
		m_sockets.Add(socket);
		await socket.SendRawAsync(Encoding.UTF8.GetBytes("""{"t":"hello","version":0,"name":"A","colour_pref":-1}"""), WebSocketMessageType.Text);
		await socket.ExpectErrorAsync(ErrorCodes.BadRequest);
		await socket.ExpectClosedAsync();
	}

	[Fact]
	public async Task OversizeAndWrongSizeFramesClose()
	{
		// Text over the limit
		var a = await Hello("A");
		await a.SendRawAsync(new byte[Protocol.MaxTextBytes + 1], WebSocketMessageType.Text);
		await a.ExpectClosedAsync();
		Assert.Equal(WebSocketCloseStatus.PolicyViolation, a.CloseStatus);

		// Binary of the wrong length
		var b = await Hello("B");
		await b.SendRawAsync(new byte[PointerFrame.Length + 1], WebSocketMessageType.Binary);
		await b.ExpectClosedAsync();
		Assert.Equal(WebSocketCloseStatus.PolicyViolation, b.CloseStatus);
	}

	[Fact]
	public async Task MalformedMessagesGetBadRequestThenClose()
	{
		// Each bad request costs a share of the violation budget; once spent, the relay closes the connection
		var a = await Hello("A");
		for (var i = 0; i != 10; ++i)
			await a.SendRawAsync(Encoding.UTF8.GetBytes("""{"t":"nope"}"""), WebSocketMessageType.Text);

		await a.ExpectErrorAsync(ErrorCodes.BadRequest);
		await a.ExpectClosedAsync();
		Assert.Equal(WebSocketCloseStatus.PolicyViolation, a.CloseStatus);
	}

	[Fact]
	public async Task RoomMessagesNeedARoom()
	{
		var a = await Hello("A");
		await a.SendAsync(new KickMsg(2));
		await a.ExpectErrorAsync(ErrorCodes.NotInRoom);
		await a.SendAsync(new RekeyMsg(Code.Verifier, Code.Secret));
		await a.ExpectErrorAsync(ErrorCodes.NotInRoom);
	}

	[Fact]
	public async Task CreateAndJoin()
	{
		// The creator owns the room
		var (a, a_joined) = await Create("Alice", Code);
		Assert.Equal(Code.Room, a_joined.Room);
		Assert.Equal(a_joined.Member, a_joined.Owner);
		Assert.False(a_joined.Resumed);
		Assert.Single(a_joined.Members);

		// The joiner sees both members, and the owner hears about the joiner
		var (_, b_joined) = await Join("Bob", Code);
		Assert.Equal(a_joined.Member, b_joined.Owner);
		Assert.Equal(["Alice", "Bob"], b_joined.Members.Select(m => m.Name));
		Assert.NotEqual(b_joined.Members[0].Slot, b_joined.Members[1].Slot);
		var added = await a.ExpectAsync<MemberJoinedMsg>();
		Assert.Equal(b_joined.Member, added.Member.Id);

		// A room id in use cannot be created again
		var c = await Hello("Carol");
		await c.SendAsync(new CreateMsg(Code.Room, Code.Verifier));
		await c.ExpectErrorAsync(ErrorCodes.RoomUnavailable);

		// Joining a room that does not exist
		await c.SendAsync(new JoinMsg("ZZZ", SessionCode.ComputeVerifier("ZZZ", Code.Secret)));
		await c.ExpectErrorAsync(ErrorCodes.NoRoom);
	}

	[Fact]
	public async Task HonoursColourPreferenceWhenFree()
	{
		var a = await Hello("A", colour_pref: 5);
		await a.SendAsync(new CreateMsg(Code.Room, Code.Verifier));
		var a_joined = await a.ExpectAsync<JoinedMsg>();
		Assert.Equal(5, a_joined.Members.Single().Slot);

		var b = await Hello("B", colour_pref: 5);
		await b.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		var b_joined = await b.ExpectAsync<JoinedMsg>();
		Assert.Equal(0, b_joined.Members.Single(m => m.Id == b_joined.Member).Slot);
	}

	[Fact]
	public async Task WrongCodesLockTheRoomUntilRekey()
	{
		// Five wrong codes lock the room and tell the owner
		var (a, _) = await Create("Alice", Code);
		var b = await Hello("Bob");
		var wrong = SessionCode.ComputeVerifier(Code.Room, "WRONG");
		for (var i = 0; i != RelayLimits.RoomFailuresBeforeLock; ++i)
		{
			await b.SendAsync(new JoinMsg(Code.Room, wrong));
			await b.ExpectErrorAsync(ErrorCodes.BadCode);
		}
		await a.ExpectAsync<LockedMsg>();

		// Even the right code is refused while locked
		await b.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		await b.ExpectErrorAsync(ErrorCodes.RoomLocked);

		// Rekeying unlocks with the new code only
		var code2 = new SessionCode(Code.Room, "NEW22");
		await a.SendAsync(new RekeyMsg(code2.Verifier, code2.Secret));
		Assert.Equal(code2.Secret, (await a.ExpectAsync<RekeyedMsg>()).Secret);
		await b.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		await b.ExpectErrorAsync(ErrorCodes.BadCode);
		await b.SendAsync(new JoinMsg(code2.Room, code2.Verifier));
		await b.ExpectAsync<JoinedMsg>();
	}

	[Fact]
	public async Task RekeyIsOwnerOnlyAndSharedWithMembers()
	{
		var (a, _) = await Create("Alice", Code);
		var (b, _) = await Join("Bob", Code);
		var code2 = new SessionCode(Code.Room, "NEW22");

		// Members cannot rekey
		await b.SendAsync(new RekeyMsg(code2.Verifier, code2.Secret));
		await b.ExpectErrorAsync(ErrorCodes.NotOwner);

		// A verifier that does not match the secret is refused
		await a.SendAsync(new RekeyMsg(Code.Verifier, code2.Secret));
		await a.ExpectErrorAsync(ErrorCodes.BadRequest);

		// The owner's rekey reaches everyone
		await a.SendAsync(new RekeyMsg(code2.Verifier, code2.Secret));
		Assert.Equal(code2.Secret, (await a.ExpectAsync<RekeyedMsg>()).Secret);
		Assert.Equal(code2.Secret, (await b.ExpectAsync<RekeyedMsg>()).Secret);
	}

	[Fact]
	public async Task RepeatedFailuresFromOneAddressAreRateLimited()
	{
		// Failures are counted per address across rooms
		var a = await Hello("A");
		for (var i = 0; i != RelayLimits.IpFailures; ++i)
		{
			var room = $"Z{i / 10}{i % 10}";
			await a.SendAsync(new JoinMsg(room, SessionCode.ComputeVerifier(room, Code.Secret)));
			await a.ExpectErrorAsync(ErrorCodes.NoRoom);
		}
		await a.SendAsync(new JoinMsg("ZZZ", SessionCode.ComputeVerifier("ZZZ", Code.Secret)));
		await a.ExpectErrorAsync(ErrorCodes.RateLimited);

		// The window slides, so the address recovers
		m_relay.Time.Advance(RelayLimits.IpFailureWindow + TimeSpan.FromSeconds(1));
		await a.SendAsync(new JoinMsg("ZZZ", SessionCode.ComputeVerifier("ZZZ", Code.Secret)));
		await a.ExpectErrorAsync(ErrorCodes.NoRoom);
	}

	[Fact]
	public async Task KickRemovesMemberButKeepsConnection()
	{
		var (a, a_joined) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);

		// Only the owner can kick
		await b.SendAsync(new KickMsg(a_joined.Member));
		await b.ExpectErrorAsync(ErrorCodes.NotOwner);

		// The kicked member is told; the owner sees it leave
		await a.SendAsync(new KickMsg(b_joined.Member));
		await b.ExpectAsync<KickedMsg>();
		Assert.Equal(b_joined.Member, (await a.ExpectAsync<MemberLeftMsg>()).Member);

		// The kicked connection is roomless but usable
		await b.SendAsync(new KickMsg(a_joined.Member));
		await b.ExpectErrorAsync(ErrorCodes.NotInRoom);
		var own = new SessionCode("B0B", "SECRT");
		await b.SendAsync(new CreateMsg(own.Room, own.Verifier));
		Assert.Equal(own.Room, (await b.ExpectAsync<JoinedMsg>()).Room);
	}

	[Fact]
	public async Task OwnershipPassesToOldestMember()
	{
		var (a, a_joined) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);
		var (c, _) = await Join("Carol", Code);

		// The owner leaves; the next oldest member takes over
		await a.SendAsync(new LeaveMsg());
		Assert.Equal(a_joined.Member, (await b.ExpectAsync<MemberLeftMsg>()).Member);
		Assert.Equal(b_joined.Member, (await b.ExpectAsync<OwnerChangedMsg>()).Owner);
		Assert.Equal(b_joined.Member, (await c.ExpectAsync<OwnerChangedMsg>()).Owner);

		// The new owner can use owner commands
		var code2 = new SessionCode(Code.Room, "NEW22");
		await b.SendAsync(new RekeyMsg(code2.Verifier, code2.Secret));
		await c.ExpectAsync<RekeyedMsg>();
	}

	[Fact]
	public async Task JoiningAnotherRoomLeavesThePreviousOne()
	{
		var (a, _) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);

		// Bob creates his own room; Alice sees him leave hers
		var own = new SessionCode("B0B", "SECRT");
		await b.SendAsync(new CreateMsg(own.Room, own.Verifier));
		await b.ExpectAsync<JoinedMsg>(j => j.Room == own.Room);
		Assert.Equal(b_joined.Member, (await a.ExpectAsync<MemberLeftMsg>()).Member);

		// Alice leaves her room, which is then deleted
		await a.SendAsync(new LeaveMsg());

		// Leave has no reply, so a request with a known reply on Alice's connection proves the leave was handled
		var nowhere = new SessionCode("ZZZ", "SECRT");
		await a.SendAsync(new JoinMsg(nowhere.Room, nowhere.Verifier));
		await a.ExpectErrorAsync(ErrorCodes.NoRoom);
		await b.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		await b.ExpectErrorAsync(ErrorCodes.NoRoom);
	}

	[Fact]
	public async Task PointersAreRoutedWithRelaySetSender()
	{
		var (a, a_joined) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);
		var (c, c_joined) = await Join("Carol", Code);

		// Broadcast: everyone else receives it, with 'From' set by the relay
		await b.SendAsync(Pointer(1));
		var at_a = await a.ExpectAsync<PointerFrame>();
		var at_c = await c.ExpectAsync<PointerFrame>();
		Assert.Equal(b_joined.Member, at_a.From);
		Assert.Equal(b_joined.Member, at_c.From);
		Assert.Equal(1, at_a.Seq);

		// Targeted: only Carol receives frame 2; Alice's next frame is 3, sent to her afterwards
		await b.SendAsync(Pointer(2, to: (byte)c_joined.Member));
		await b.SendAsync(Pointer(3, to: (byte)a_joined.Member));
		Assert.Equal(2, (await c.ExpectAsync<PointerFrame>()).Seq);
		Assert.Equal(3, (await a.ExpectAsync<PointerFrame>()).Seq);

		// Senders never receive their own frames: Bob's next frame is from Alice
		await a.SendAsync(Pointer(4));
		var at_b = await b.ExpectAsync<PointerFrame>();
		Assert.Equal(a_joined.Member, at_b.From);
		Assert.Equal(4, at_b.Seq);
	}

	[Fact]
	public async Task PointersOutsideARoomAreDropped()
	{
		var (a, _) = await Create("Alice", Code);
		var b = await Hello("Bob");

		// Frame 1 is sent before joining and goes nowhere; frame 2 after joining arrives
		await b.SendAsync(Pointer(1));
		await b.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		await b.ExpectAsync<JoinedMsg>();
		await b.SendAsync(Pointer(2));
		Assert.Equal(2, (await a.ExpectAsync<PointerFrame>()).Seq);
	}

	[Fact]
	public async Task ResumeAfterDisconnectKeepsTheSlot()
	{
		var (a, a_joined) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);

		// Bob drops; his slot is held, so Alice sees no change
		await b.CloseAsync();

		// Bob comes back on a new connection and resumes the same member id
		var b2 = await Hello("Bob");
		await b2.SendAsync(new ResumeMsg(Code.Room, b_joined.Member, b_joined.Token));
		var resumed = await b2.ExpectAsync<JoinedMsg>();
		Assert.True(resumed.Resumed);
		Assert.Equal(b_joined.Member, resumed.Member);
		Assert.Equal(a_joined.Member, resumed.Owner);
		Assert.Equal(2, resumed.Members.Length);

		// Pointers flow to the resumed connection, and Alice saw no 'left' message before this frame
		await a.SendAsync(Pointer(7));
		Assert.Equal(7, (await b2.ExpectAsync<PointerFrame>()).Seq);
		await b2.SendAsync(Pointer(8));
		var next = await a.NextAsync();
		Assert.IsType<MemberJoinedMsg>(next);
		Assert.Equal(8, Assert.IsType<PointerFrame>(await a.NextAsync()).Seq);
	}

	[Fact]
	public async Task ResumeNeedsTheRightToken()
	{
		_ = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);
		await b.CloseAsync();

		var b2 = await Hello("Bob");
		await b2.SendAsync(new ResumeMsg(Code.Room, b_joined.Member, new string('0', 32)));
		await b2.ExpectErrorAsync(ErrorCodes.ResumeFailed);
	}

	[Fact]
	public async Task HeldMembersLeaveWhenTheHoldExpires()
	{
		var (a, _) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);
		await b.CloseAsync();

		// After the hold, Alice is told and the slot cannot be resumed
		m_relay.Time.Advance(RelayLimits.HoldTime + TimeSpan.FromSeconds(1));
		Assert.Equal(b_joined.Member, (await a.ExpectAsync<MemberLeftMsg>()).Member);
		var b2 = await Hello("Bob");
		await b2.SendAsync(new ResumeMsg(Code.Room, b_joined.Member, b_joined.Token));
		await b2.ExpectErrorAsync(ErrorCodes.ResumeFailed);
	}

	[Fact]
	public async Task HeldOwnerKeepsOwnershipUntilTheHoldExpires()
	{
		var (a, a_joined) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);
		await a.CloseAsync();

		// Ownership moves only when the held owner is removed
		m_relay.Time.Advance(RelayLimits.HoldTime + TimeSpan.FromSeconds(1));
		Assert.Equal(a_joined.Member, (await b.ExpectAsync<MemberLeftMsg>()).Member);
		Assert.Equal(b_joined.Member, (await b.ExpectAsync<OwnerChangedMsg>()).Owner);
	}

	[Fact]
	public async Task EmptyRoomsAreDeletedAfterTheHold()
	{
		var hub = (Hub)m_relay.Services.GetService(typeof(Hub))!;
		var (a, _) = await Create("Alice", Code);
		await a.CloseAsync();
		Assert.Equal(1, hub.RoomCount);

		m_relay.Time.Advance(RelayLimits.HoldTime + TimeSpan.FromSeconds(1));
		Assert.Equal(0, hub.RoomCount);
	}

	[Fact]
	public async Task RoomFullAfterMaxMembers()
	{
		await Create("Owner", Code);
		for (var i = 1; i != Protocol.MaxMembers; ++i)
			await Join($"M{i}", Code);

		var extra = await Hello("Extra");
		await extra.SendAsync(new JoinMsg(Code.Room, Code.Verifier));
		await extra.ExpectErrorAsync(ErrorCodes.RoomFull);
	}

	[Fact]
	public async Task RenameAndTargetAreBroadcast()
	{
		var (a, _) = await Create("Alice", Code);
		var (b, b_joined) = await Join("Bob", Code);

		await b.SendAsync(new RenameMsg("Robert"));
		var renamed = await a.ExpectAsync<MemberUpdatedMsg>();
		Assert.Equal("Robert", renamed.Member.Name);
		Assert.Equal(b_joined.Member, renamed.Member.Id);

		await b.SendAsync(new TargetMsg(1.6, "Monitor 2"));
		var target = await a.ExpectAsync<MemberUpdatedMsg>(m => m.Member.Label.Length != 0);
		Assert.Equal(1.6, target.Member.Aspect);
		Assert.Equal("Monitor 2", target.Member.Label);
	}

	[Fact]
	public async Task ExcessControlMessagesAreRateLimited()
	{
		// The burst allowance is spent with the clock frozen, so the next message is limited
		var a = await Hello("A");
		for (var i = 0; i != (int)RelayLimits.TextBurst; ++i)
			await a.SendAsync(new RenameMsg("A"));

		await a.ExpectErrorAsync(ErrorCodes.RateLimited);
	}
}
