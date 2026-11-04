using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Tests of direct peer-to-peer pointer delivery: signalling validation, STUN parsing, the UDP transport, and its use by <see cref="RelayClient"/>.</summary>
public sealed class PeerTests : IAsyncLifetime
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
	private static readonly PeerOptions Loopback = new([], LoopbackOnly: true);

	private readonly RelayFactory m_relay = new();
	private readonly List<IAsyncDisposable> m_disposables = [];

	public Task InitializeAsync()
	{
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		// Clients first, so they close their relay connections before the relay stops
		foreach (var item in Enumerable.Reverse(m_disposables))
			await item.DisposeAsync();

		await m_relay.DisposeAsync();
	}

	/// <summary>A valid public key in signalling form.</summary>
	private static string NewKey()
	{
		// An uncompressed P-256 point: 0x04, X, Y
		using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
		var q = ecdh.ExportParameters(false).Q;
		return Convert.ToBase64String([0x04, .. q.X!, .. q.Y!]);
	}

	[Fact]
	public void PeerMsgValidation()
	{
		// A valid message survives a round trip
		var key = NewKey();
		var good = new PeerMsg(3, key, ["203.0.113.7:50000", "[2001:db8::1]:50000"], Reply: false);
		Assert.True(good.IsValid());
		Assert.True(Protocol.TryParse(Protocol.Serialise(good), out var parsed));
		var back = Assert.IsType<PeerMsg>(parsed);
		Assert.Equal(good.Candidates, back.Candidates);
		Assert.Equal(key, back.Key);

		// Bad member, bad keys, bad candidates and too many candidates are all rejected
		var short_key = Convert.ToBase64String(new byte[64]);
		var wrong_prefix = Convert.ToBase64String([0x02, .. new byte[64]]);
		Assert.False((good with { Member = 0 }).IsValid());
		Assert.False((good with { Key = short_key }).IsValid());
		Assert.False((good with { Key = wrong_prefix }).IsValid());
		Assert.False((good with { Key = "not base64!" }).IsValid());
		Assert.False((good with { Candidates = ["203.0.113.7:0"] }).IsValid());
		Assert.False((good with { Candidates = ["0.0.0.0:5000"] }).IsValid());
		Assert.False((good with { Candidates = ["224.0.0.1:5000"] }).IsValid());
		Assert.False((good with { Candidates = ["[ff02::1]:5000"] }).IsValid());
		Assert.False((good with { Candidates = ["example.com:5000"] }).IsValid());
		Assert.False((good with { Candidates = Enumerable.Repeat("203.0.113.7:50000", PeerMsg.MaxCandidates + 1).ToArray() }).IsValid());
		Assert.True((good with { Candidates = [] }).IsValid());
	}

	[Fact]
	public void StunResponseParses()
	{
		// A binding success with XOR-MAPPED-ADDRESS for 203.0.113.7:50000, built as a server would
		var transaction = RandomNumberGenerator.GetBytes(Stun.TransactionIdLength);
		var msg = new byte[Stun.HeaderLength + 12];
		BinaryPrimitives.WriteUInt16BigEndian(msg, 0x0101);
		BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 12);
		BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), 0x2112A442);
		transaction.CopyTo(msg.AsSpan(8));
		BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(20), 0x0020);
		BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(22), 8);
		msg[25] = 0x01;
		BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(26), (ushort)(50000 ^ 0x2112));
		BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(28), 0xCB007107u ^ 0x2112A442u);

		Assert.True(Stun.IsStun(msg));
		Assert.True(Stun.TryParseResponse(msg, out var id, out var mapped));
		Assert.Equal(transaction, id);
		Assert.Equal(IPEndPoint.Parse("203.0.113.7:50000"), mapped);

		// A request, a truncated message and a wrong length are not responses
		Assert.False(Stun.TryParseResponse(Stun.Request(transaction), out _, out _));
		Assert.False(Stun.TryParseResponse(msg.AsSpan(0, msg.Length - 4), out _, out _));
		Assert.False(Stun.IsStun(new byte[Stun.HeaderLength]));
	}

	/// <summary>Two transports whose signals are passed straight to each other, as the relay would, as members 1 and 2.</summary>
	private (PeerTransport A, PeerTransport B, Func<PointerFrame, Task> ExpectAtB) Pair()
	{
		// Signals name the receiver; the relay rewrites them to name the sender
		PeerTransport? a = null, b = null;
		var received = System.Threading.Channels.Channel.CreateUnbounded<PointerFrame>();
		a = new PeerTransport(Loopback, msg => b!.OnSignal(msg with { Member = 1 }), _ => { }, () => { });
		b = new PeerTransport(Loopback, msg => a.OnSignal(msg with { Member = 2 }), f => received.Writer.TryWrite(f), () => { });
		m_disposables.Add(new Disposer(a));
		m_disposables.Add(new Disposer(b));

		// Wait for the next sample at B and check it is the expected one
		async Task ExpectAtB(PointerFrame expected)
		{
			var frame = await received.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
			Assert.Equal(expected with { From = 1 }, frame);
		}
		return (a, b, ExpectAtB);
	}

	/// <summary>Wait until <paramref name="condition"/> holds.</summary>
	private static async Task Until(Func<bool> condition)
	{
		// Polled, because path changes are reported without detail
		var deadline = DateTime.UtcNow + Timeout;
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "Timed out");
			await Task.Delay(50);
		}
	}

	[Fact]
	public async Task TransportDeliversDirectlyAndRejectsForgeries()
	{
		// Both sides reach 'direct'
		var (a, b, expect_at_b) = Pair();
		a.Update(1, 1, [1, 2]);
		b.Update(1, 2, [1, 2]);
		await Until(() => a.DirectMembers().Contains(2) && b.DirectMembers().Contains(1));

		// A sample arrives with the authenticated sender id, whatever 'From' was set to
		var frame = new PointerFrame(From: 77, To: 0, Seq: 5, X: 100, Y: 200, Flags: EPointerFlags.Visible, Size: 100);
		Assert.True(a.TrySend(frame));
		await expect_at_b(frame with { From = 0 });

		// Forged and garbage packets are dropped and do not disturb the path
		using (var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
		{
			byte[] forged = [0xB1, 1, 2, .. BitConverter.GetBytes(1_000_000UL), .. new byte[1 + PointerFrame.Length + 16]];
			attacker.SendTo(forged, b.LocalEndPoint);
			attacker.SendTo(RandomNumberGenerator.GetBytes(40), b.LocalEndPoint);
		}
		var next = frame with { Seq = 6, X = 300 };
		Assert.True(a.TrySend(next));
		await expect_at_b(next with { From = 0 });
		Assert.Contains(1, b.DirectMembers());

		// A sample for a member without a direct path is refused, so the caller uses the relay
		Assert.False(a.TrySend(frame with { To = 3 }));
	}

	[Fact]
	public async Task MembershipChangeDropsPath()
	{
		var (a, b, _) = Pair();
		a.Update(1, 1, [1, 2]);
		b.Update(1, 2, [1, 2]);
		await Until(() => a.DirectMembers().Contains(2));

		// Leaving the pair, or a new epoch, ends direct delivery at once
		a.Update(1, 1, [1]);
		Assert.Empty(a.DirectMembers());
		Assert.False(a.TrySend(new PointerFrame(0, 0, 1, 0, 0, EPointerFlags.Visible, 100)));
	}

	/// <summary>Start a client against the test relay and wait until it is online in its own room.</summary>
	private async Task<RelayClient> StartClient(string name, PeerOptions? peers)
	{
		var client = new RelayClient(ct => m_relay.ConnectAsync(ct), name, peers: peers);
		m_disposables.Add(client);
		var online = WaitFor(client, s => s.Connection == EConnection.Online && s.Code is not null);
		client.Start();
		await online;
		return client;
	}

	/// <summary>Wait until the client's session snapshot satisfies <paramref name="predicate"/>.</summary>
	private static async Task<SessionSnapshot> WaitFor(RelayClient client, Func<SessionSnapshot, bool> predicate)
	{
		// Subscribe before checking the current snapshot so a change in between is not missed
		var tcs = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
		void Handler(SessionSnapshot s)
		{
			if (predicate(s))
				tcs.TrySetResult(s);
		}
		client.SessionChanged += Handler;
		try
		{
			// The snapshot may already match
			if (predicate(client.Snapshot))
				return client.Snapshot;

			return await tcs.Task.WaitAsync(Timeout);
		}
		finally
		{
			client.SessionChanged -= Handler;
		}
	}

	[Fact]
	public async Task ClientsGoDirectAndDeliverEachSampleOnce()
	{
		// Bob joins Alice; both report a direct path to the other
		var a = await StartClient("Alice", Loopback);
		var b = await StartClient("Bob", Loopback);
		var a_direct = WaitFor(a, s => s.Members.Count == 2 && s.Members.All(m => m.Id == s.Me || s.PathTo(m.Id) == EPath.Direct));
		var b_direct = WaitFor(b, s => s.Members.Count == 2 && s.Members.All(m => m.Id == s.Me || s.PathTo(m.Id) == EPath.Direct));
		b.Join(a.Snapshot.Code!.Value);
		await a_direct;
		var b_snap = await b_direct;

		// Each sample arrives once, from Bob, in order
		var received = System.Threading.Channels.Channel.CreateUnbounded<PointerFrame>();
		a.PointerReceived += f => received.Writer.TryWrite(f);
		for (ushort seq = 1; seq != 6; ++seq)
			Assert.True(b.SendPointer(new PointerFrame(0, 0, seq, seq, 0, EPointerFlags.Visible, 100)));

		for (ushort seq = 1; seq != 6; ++seq)
		{
			var frame = await received.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
			Assert.Equal(seq, frame.Seq);
			Assert.Equal(b_snap.Me, frame.From);
		}
		await Task.Delay(300);
		Assert.False(received.Reader.TryRead(out _));
	}

	[Fact]
	public async Task ClientWithoutPeersUsesRelay()
	{
		// Alice can go direct but Bob cannot, so their pair stays relayed and samples still arrive
		var a = await StartClient("Alice", Loopback);
		var b = await StartClient("Bob", null);
		var b_shared = WaitFor(b, s => s.Members.Count == 2);
		b.Join(a.Snapshot.Code!.Value);
		var b_snap = await b_shared;
		await WaitFor(a, s => s.Members.Count == 2);

		var received = new TaskCompletionSource<PointerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
		a.PointerReceived += f => received.TrySetResult(f);
		Assert.True(a.SendPointer(new PointerFrame(0, 0, 1, 1, 1, EPointerFlags.Visible, 100)));
		Assert.True(b.SendPointer(new PointerFrame(0, 0, 1, 1, 1, EPointerFlags.Visible, 100)));
		Assert.Equal(b_snap.Me, (await received.Task.WaitAsync(Timeout)).From);

		// Give the offers time to fail
		await Task.Delay(1000);
		Assert.Equal(EPath.Relayed, a.Snapshot.PathTo(b_snap.Me));
		Assert.Equal(EPath.Relayed, b.Snapshot.PathTo(a.Snapshot.Me));
	}

	[Fact]
	public async Task RelayForwardsSignalsWithinRoomOnly()
	{
		// Alice and Bob share a room; Carol is in another
		var code = new SessionCode("K7Q", "M4PZX");
		var alice = await m_relay.HelloAsync("Alice");
		m_disposables.Add(alice);
		await alice.SendAsync(new CreateMsg(code.Room, code.Verifier));
		var a_joined = await alice.ExpectAsync<JoinedMsg>();
		var bob = await m_relay.HelloAsync("Bob");
		m_disposables.Add(bob);
		await bob.SendAsync(new JoinMsg(code.Room, code.Verifier));
		var b_joined = await bob.ExpectAsync<JoinedMsg>();
		var other = new SessionCode("Z9X", "M4PZX");
		var carol = await m_relay.HelloAsync("Carol");
		m_disposables.Add(carol);
		await carol.SendAsync(new CreateMsg(other.Room, other.Verifier));
		await carol.ExpectAsync<JoinedMsg>();

		// Alice's signal reaches Bob naming Alice as the sender
		var signal = new PeerMsg(b_joined.Member, NewKey(), ["127.0.0.1:5000"], Reply: false);
		await alice.SendAsync(signal);
		var forwarded = await bob.ExpectAsync<PeerMsg>();
		Assert.Equal(a_joined.Member, forwarded.Member);
		Assert.Equal(signal.Key, forwarded.Key);

		// Carol cannot reach Bob, whose id in her room means no one. Alice's later pointer proves no signal arrived before it.
		await carol.SendAsync(signal);
		await Task.Delay(200);
		await alice.SendAsync(new PointerFrame(0, 0, 9, 0, 0, EPointerFlags.Visible, 100));
		Assert.Equal(9, Assert.IsType<PointerFrame>(await bob.NextAsync()).Seq);
	}

	/// <summary>Adapts <see cref="IDisposable"/> for the disposal list.</summary>
	private sealed class Disposer(IDisposable inner) : IAsyncDisposable
	{
		public ValueTask DisposeAsync()
		{
			inner.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
