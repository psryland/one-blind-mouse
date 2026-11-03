using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>End-to-end tests of <see cref="RelayClient"/> against the in-memory relay.</summary>
public sealed class RelayClientTests : IAsyncLifetime
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	private readonly RelayFactory m_relay = new();
	private readonly List<RelayClient> m_clients = [];

	public Task InitializeAsync()
	{
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		foreach (var client in m_clients)
			await client.DisposeAsync();

		await m_relay.DisposeAsync();
	}

	/// <summary>Start a client connected to the test relay and wait until it is online in its own room.</summary>
	private async Task<RelayClient> StartClient(string name)
	{
		var client = new RelayClient(ct => m_relay.ConnectAsync(ct), name);
		m_clients.Add(client);
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
	public async Task JoinShareNewCodeAndKick()
	{
		// Two clients, each online in its own room
		var a = await StartClient("Alice");
		var b = await StartClient("Bob");
		var a_code = a.Snapshot.Code!.Value;
		var b_own_room = b.Snapshot.Code!.Value.Room;
		Assert.NotEqual(a_code.Room, b_own_room);

		// Bob joins Alice's code; both see two members
		var a_shared = WaitFor(a, s => s.Members.Count == 2);
		var b_shared = WaitFor(b, s => s.Members.Count == 2 && s.Code == a_code);
		b.Join(a_code);
		await a_shared;
		var b_snapshot = await b_shared;
		Assert.True(a.Snapshot.IsOwner);
		Assert.False(b_snapshot.IsOwner);
		Assert.Equal(["Alice", "Bob"], a.Snapshot.Members.OrderBy(m => m.Id).Select(m => m.Name));

		// A pointer from Bob reaches Alice with Bob's member id
		var received = new TaskCompletionSource<PointerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
		a.PointerReceived += f => received.TrySetResult(f);
		Assert.True(b.SendPointer(new PointerFrame(0, 0, 1, 1000, 2000, EPointerFlags.Visible, 100)));
		var frame = await received.Task.WaitAsync(Timeout);
		Assert.Equal(b_snapshot.Me, frame.From);
		Assert.Equal(1000, frame.X);

		// Alice changes the code; both see the same new code
		var a_rekeyed = WaitFor(a, s => s.Code is { } c && c != a_code);
		var b_rekeyed = WaitFor(b, s => s.Code is { } c && c != a_code);
		a.NewCode();
		var new_code = (await a_rekeyed).Code;
		Assert.Equal(new_code, (await b_rekeyed).Code);
		Assert.Equal(a_code.Room, new_code!.Value.Room);

		// Alice kicks Bob; Bob returns to a room of his own and Alice is alone again
		var a_alone = WaitFor(a, s => s.Members.Count == 1);
		var b_home = WaitFor(b, s => s.Connection == EConnection.Online && s.Members.Count == 1 && s.Code is { } c && c.Room != a_code.Room);
		a.Kick(b_snapshot.Me);
		await a_alone;
		var home = await b_home;
		Assert.Equal(b_own_room, home.Code!.Value.Room);
		Assert.True(home.IsOwner);
	}

	[Fact]
	public async Task LeaveReturnsToOwnRoom()
	{
		var a = await StartClient("Alice");
		var b = await StartClient("Bob");
		var b_own_room = b.Snapshot.Code!.Value.Room;

		// Bob joins, then leaves
		var b_shared = WaitFor(b, s => s.Members.Count == 2);
		b.Join(a.Snapshot.Code!.Value);
		await b_shared;
		var a_alone = WaitFor(a, s => s.Members.Count == 1);
		var b_home = WaitFor(b, s => s.Connection == EConnection.Online && s.Code is { } c && c.Room == b_own_room);
		b.Leave();
		await a_alone;
		await b_home;
	}

	[Fact]
	public async Task WrongCodeLeavesClientInItsRoom()
	{
		var a = await StartClient("Alice");
		var b = await StartClient("Bob");
		var b_code = b.Snapshot.Code;

		// The wrong secret is reported and Bob stays where he was
		var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		b.Notice += (level, text) =>
		{
			if (level == ENotice.Warning)
				notice.TrySetResult(text);
		};
		b.Join(new SessionCode(a.Snapshot.Code!.Value.Room, "WRNG0"));
		Assert.Equal("That code is not correct", await notice.Task.WaitAsync(Timeout));
		Assert.Equal(b_code, b.Snapshot.Code);
		Assert.Single(a.Snapshot.Members);
	}
}
