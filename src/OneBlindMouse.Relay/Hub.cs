using System.Security.Cryptography;
using System.Text;
using OneBlindMouse.Core;

namespace OneBlindMouse.Relay;

/// <summary>
/// The relay's shared state: rooms, members and per-IP limits. All state is guarded by one lock, which keeps the rules simple to check.
/// Work under the lock is short and never blocks: sends only queue frames on the connection's outbound channel.
///
/// Held members: when a connection drops, its member slot is held for <see cref="RelayLimits.HoldTime"/> so the client can resume it.
/// A held member stays in the member list that others see (and keeps ownership), so a quick reconnect is seamless and causes no
/// "left"/"joined" churn. Others receive member_left only when the hold expires. Pointer frames are never routed to held members.
/// </summary>
public sealed class Hub : IDisposable
{
	private readonly Lock m_lock = new();
	private readonly TimeProvider m_time;
	private readonly ILogger<Hub> m_log;
	private readonly ITimer m_sweep_timer;
	private readonly Dictionary<string, Room> m_rooms = [];
	private readonly Dictionary<string, int> m_ip_connections = [];
	private readonly Dictionary<string, Queue<DateTimeOffset>> m_ip_failures = [];

	/// <summary>Create the hub. A timer sweeps expired holds and old failure records once a second.</summary>
	public Hub(TimeProvider time, ILogger<Hub> log)
	{
		m_time = time;
		m_log = log;
		m_sweep_timer = time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}

	/// <summary>Number of rooms, for diagnostics and tests.</summary>
	public int RoomCount
	{
		get
		{
			lock (m_lock)
			{
				return m_rooms.Count;
			}
		}
	}

	/// <summary>Stop the sweep timer.</summary>
	public void Dispose()
	{
		m_sweep_timer.Dispose();
	}

	/// <summary>Register a new connection from <paramref name="ip"/>. Returns false if that address already has too many connections.</summary>
	public bool TryOpen(string ip)
	{
		lock (m_lock)
		{
			// Limit concurrent sockets per address so one host cannot exhaust the relay
			var count = m_ip_connections.GetValueOrDefault(ip);
			if (count >= RelayLimits.IpConnections)
				return false;

			m_ip_connections[ip] = count + 1;
			return true;
		}
	}

	/// <summary>Unregister a connection opened with <see cref="TryOpen"/>.</summary>
	public void Closed(string ip)
	{
		lock (m_lock)
		{
			// Drop the entry when the count reaches zero so the table does not grow forever
			var count = m_ip_connections.GetValueOrDefault(ip) - 1;
			if (count > 0)
				m_ip_connections[ip] = count;
			else
				m_ip_connections.Remove(ip);
		}
	}

	/// <summary>Handle a valid hello: remember the client's details and reply with welcome.</summary>
	public void OnHello(Session session, HelloMsg hello)
	{
		lock (m_lock)
		{
			session.Name = hello.Name;
			session.ColourPref = hello.ColourPref;
			session.SendText(new WelcomeMsg(RelayLimits.MinVersion));
		}
	}

	/// <summary>Handle a valid control message received after hello.</summary>
	public void Handle(Session session, ControlMessage msg)
	{
		lock (m_lock)
		{
			switch (msg)
			{
				case CreateMsg create:
				{
					OnCreate(session, create);
					break;
				}
				case JoinMsg join:
				{
					OnJoin(session, join);
					break;
				}
				case ResumeMsg resume:
				{
					OnResume(session, resume);
					break;
				}
				case LeaveMsg:
				{
					LeaveRoom(session);
					break;
				}
				case KickMsg kick:
				{
					OnKick(session, kick);
					break;
				}
				case RekeyMsg rekey:
				{
					OnRekey(session, rekey);
					break;
				}
				case RenameMsg rename:
				{
					// Remembered for future rooms even when not in one, because clients may rename at any time
					session.Name = rename.Name;
					UpdateMember(session);
					break;
				}
				case TargetMsg target:
				{
					session.Aspect = target.Aspect;
					session.Label = target.Label;
					UpdateMember(session);
					break;
				}
				case PeerMsg peer:
				{
					OnPeer(session, peer);
					break;
				}
				default:
				{
					// Hello again, or a relay-to-client message type
					session.SendError(ErrorCodes.BadRequest, "Unexpected message");
					break;
				}
			}
		}
	}

	/// <summary>Route a pointer frame from <paramref name="session"/> to other live members of its room.</summary>
	public void OnPointer(Session session, PointerFrame frame)
	{
		lock (m_lock)
		{
			// Pointer frames only make sense in a room with someone else in it
			if (session.Room is not { } room || session.Member is not { } sender)
				return;

			// The relay sets 'From' so members cannot impersonate each other. Encode once and share the bytes.
			var data = (frame with { From = (byte)sender.Id }).ToArray();
			if (frame.To == 0)
			{
				// Broadcast to everyone else who is connected
				foreach (var member in room.LiveMembers)
				{
					if (member != sender)
						member.Session!.SendBinary(data);
				}
			}
			else if (room.Find(frame.To) is { IsLive: true } target && target != sender)
			{
				// Targeted at one member of the same room
				target.Session!.SendBinary(data);
			}
		}
	}

	/// <summary>The connection closed. Its member slot, if any, is held so the client can resume it.</summary>
	public void OnDisconnected(Session session)
	{
		lock (m_lock)
		{
			// Detach the connection but keep the slot; see the class comment for why held members stay listed
			if (session.Member is { } member)
			{
				member.Session = null;
				member.HoldExpires = m_time.GetUtcNow() + RelayLimits.HoldTime;
			}
			session.Room = null;
			session.Member = null;
		}
	}

	/// <summary>Remove members whose hold has expired and forget old per-IP failures. Called by a timer; public for tests.</summary>
	public void Sweep()
	{
		lock (m_lock)
		{
			// Expired holds leave their rooms for good
			var now = m_time.GetUtcNow();
			foreach (var room in m_rooms.Values.ToList())
			{
				foreach (var member in room.Members.Where(m => !m.IsLive && m.HoldExpires <= now).ToList())
					RemoveMember(room, member);
			}

			// Failure records outside the window no longer count
			foreach (var (ip, failures) in m_ip_failures.ToList())
			{
				PruneFailures(failures, now);
				if (failures.Count == 0)
					m_ip_failures.Remove(ip);
			}
		}
	}

	/// <summary>Create a room with the client's chosen id.</summary>
	private void OnCreate(Session session, CreateMsg create)
	{
		// Limit room creation so a client cannot churn through room ids
		if (!session.CreateBucket.TryTake())
		{
			session.SendError(ErrorCodes.RateLimited, "Too many new sessions. Please wait a minute.");
			return;
		}

		// The id must be free; the client picks another if not
		if (m_rooms.ContainsKey(create.Room))
		{
			session.SendError(ErrorCodes.RoomUnavailable, "That session id is in use");
			return;
		}

		// Move the connection into the new room as its owner
		LeaveRoom(session);
		var room = new Room(create.Room, create.Verifier);
		m_rooms.Add(room.Id, room);
		Enter(session, room);
	}

	/// <summary>Join an existing room by code.</summary>
	private void OnJoin(Session session, JoinMsg join)
	{
		// Addresses that keep guessing are refused without checking the code
		var now = m_time.GetUtcNow();
		if (IpFailureCount(session.Ip, now) >= RelayLimits.IpFailures)
		{
			session.SendError(ErrorCodes.RateLimited, "Too many attempts. Please wait a few minutes.");
			return;
		}

		// Unknown room ids count as failures too, so room ids cannot be scanned freely
		if (!m_rooms.TryGetValue(join.Room, out var room))
		{
			AddIpFailure(session.Ip, now);
			session.SendError(ErrorCodes.NoRoom, "No session has that code");
			return;
		}

		// A locked room refuses everyone until the owner rekeys
		if (room.Locked)
		{
			session.SendError(ErrorCodes.RoomLocked, "That session is locked");
			return;
		}

		// Wrong code: count it against the room and the address, and lock the room after too many
		if (!room.VerifierMatches(join.Verifier))
		{
			AddIpFailure(session.Ip, now);
			room.Failures += 1;
			if (room.Failures >= RelayLimits.RoomFailuresBeforeLock)
			{
				room.Locked = true;
				room.Find(room.Owner)?.Session?.SendText(new LockedMsg());
				m_log.LogInformation("A room was locked after repeated wrong codes");
			}
			session.SendError(ErrorCodes.BadCode, "That code is not correct");
			return;
		}

		// Joining the room we are already in changes nothing
		if (session.Room == room)
		{
			SendJoined(session, room, session.Member!, resumed: false);
			return;
		}

		// Held members keep their place, so they count towards the limit
		if (room.IsFull)
		{
			session.SendError(ErrorCodes.RoomFull, "That session is full");
			return;
		}

		// Leaving the previous room may delete it, but not this one, because the session is not in this room
		LeaveRoom(session);
		Enter(session, room);
	}

	/// <summary>Reclaim a member slot with its resume token.</summary>
	private void OnResume(Session session, ResumeMsg resume)
	{
		// The slot must exist and the token must match
		if (!m_rooms.TryGetValue(resume.Room, out var room) ||
			room.Find(resume.Member) is not { } member ||
			!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(member.Token), Encoding.ASCII.GetBytes(resume.Token)))
		{
			session.SendError(ErrorCodes.ResumeFailed, "The session slot has expired");
			return;
		}

		// Resuming our own slot is a no-op apart from the reply
		if (member.Session == session)
		{
			SendJoined(session, room, member, resumed: true);
			return;
		}

		// The old connection may not have noticed it is dead yet (e.g. a half-open socket); the new connection takes over
		if (member.Session is { } old)
		{
			old.Room = null;
			old.Member = null;
			old.Abort();
		}

		// Leave our previous room, unless that is the room being resumed (which must not be deleted on the way)
		if (session.Room != room)
			LeaveRoom(session);
		else if (session.Member is { } current)
			RemoveMember(room, current);

		// Re-attach the slot to this connection
		member.Session = session;
		session.Room = room;
		session.Member = member;
		SendJoined(session, room, member, resumed: true);

		// Pick up a name or target change made while disconnected
		if (member.Name != session.Name || member.Aspect != session.Aspect || member.Label != session.Label)
			UpdateMember(session);

		// The owner missed any lock notice while away
		if (room.Locked && room.Owner == member.Id)
			session.SendText(new LockedMsg());
	}

	/// <summary>Remove another member from the room (owner only).</summary>
	private void OnKick(Session session, KickMsg kick)
	{
		// Only the owner may kick, and not itself
		if (!IsOwner(session, out var room))
			return;

		if (room.Find(kick.Member) is not { } member || member == session.Member)
		{
			session.SendError(ErrorCodes.BadRequest, "No such member");
			return;
		}

		// The kicked connection stays open but roomless; it decides where to go next
		if (member.Session is { } kicked)
		{
			kicked.Room = null;
			kicked.Member = null;
			kicked.SendText(new KickedMsg());
		}
		RemoveMember(room, member);
	}

	/// <summary>Forward direct-path signalling to another live member of the same room, naming the sender so it cannot be forged.</summary>
	private static void OnPeer(Session session, PeerMsg peer)
	{
		// Signals for a member that is gone or offline are dropped silently; the sender retries or gives up and uses the relay
		if (session.Room is not { } room || session.Member is not { } sender)
			return;

		if (room.Find(peer.Member) is { IsLive: true } target && target != sender)
			target.Session!.SendText(peer with { Member = sender.Id });
	}

	/// <summary>Replace the room secret (owner only) and share the new secret with all members.</summary>
	private void OnRekey(Session session, RekeyMsg rekey)
	{
		// Only the owner may rekey, and the verifier must match the secret so members are told the code that really works
		if (!IsOwner(session, out var room))
			return;

		if (SessionCode.ComputeVerifier(room.Id, rekey.Secret) != rekey.Verifier)
		{
			session.SendError(ErrorCodes.BadRequest, "Verifier does not match");
			return;
		}

		// A new code makes earlier guesses useless, so the lock and failure count reset
		room.SetVerifier(rekey.Verifier);
		room.Locked = false;
		room.Failures = 0;
		Broadcast(room, new RekeyedMsg(rekey.Secret), except: null);
	}

	/// <summary>True if the session owns its room. Otherwise sends the appropriate error.</summary>
	private static bool IsOwner(Session session, out Room room)
	{
		// Owner-only commands need a room first
		room = session.Room!;
		if (session.Room is null || session.Member is null)
		{
			session.SendError(ErrorCodes.NotInRoom, "Not in a session");
			return false;
		}
		if (room.Owner != session.Member.Id)
		{
			session.SendError(ErrorCodes.NotOwner, "Only the session owner can do that");
			return false;
		}
		return true;
	}

	/// <summary>Add the session to <paramref name="room"/> as a new member, and tell everyone.</summary>
	private void Enter(Session session, Room room)
	{
		// Allocate the slot, then tell existing members before replying, so the newcomer's list and theirs agree
		var member = room.Add(session, session.ColourPref);
		session.Room = room;
		session.Member = member;
		Broadcast(room, new MemberJoinedMsg(member.Info), except: member);
		SendJoined(session, room, member, resumed: false);
	}

	/// <summary>Send the joined reply describing the room.</summary>
	private static void SendJoined(Session session, Room room, Member member, bool resumed)
	{
		var members = room.Members.OrderBy(m => m.Id).Select(m => m.Info).ToArray();
		session.SendText(new JoinedMsg(room.Id, member.Id, member.Token, room.Owner, members, resumed));
	}

	/// <summary>Take the session out of its current room, if any.</summary>
	private void LeaveRoom(Session session)
	{
		// Detach first so the departing connection gets no further room messages
		if (session.Room is not { } room || session.Member is not { } member)
			return;

		session.Room = null;
		session.Member = null;
		RemoveMember(room, member);
	}

	/// <summary>Remove a member for good: tell the others, pass on ownership, and delete the room when it is empty.</summary>
	private void RemoveMember(Room room, Member member)
	{
		// Tell the remaining live members
		var owner_changed = room.Remove(member);
		Broadcast(room, new MemberLeftMsg(member.Id), except: null);
		if (owner_changed)
			Broadcast(room, new OwnerChangedMsg(room.Owner), except: null);

		// Rooms exist only while they have members
		if (room.IsEmpty)
			m_rooms.Remove(room.Id);
	}

	/// <summary>Apply the session's current name and target to its member and tell the room.</summary>
	private void UpdateMember(Session session)
	{
		// Outside a room the details are only remembered for later
		if (session.Room is not { } room || session.Member is not { } member)
			return;

		member.Name = session.Name;
		member.Aspect = session.Aspect;
		member.Label = session.Label;
		Broadcast(room, new MemberUpdatedMsg(member.Info), except: null);
	}

	/// <summary>Send a control message to every live member of <paramref name="room"/> except <paramref name="except"/>.</summary>
	private static void Broadcast(Room room, ControlMessage msg, Member? except)
	{
		// Serialise once and share the bytes
		var data = Protocol.Serialise(msg);
		foreach (var member in room.LiveMembers)
		{
			if (member != except)
				member.Session!.SendText(data);
		}
	}

	/// <summary>Number of recent join failures from <paramref name="ip"/>.</summary>
	private int IpFailureCount(string ip, DateTimeOffset now)
	{
		// Prune lazily so the count is exact even between sweeps
		if (!m_ip_failures.TryGetValue(ip, out var failures))
			return 0;

		PruneFailures(failures, now);
		return failures.Count;
	}

	/// <summary>Record a join failure from <paramref name="ip"/>.</summary>
	private void AddIpFailure(string ip, DateTimeOffset now)
	{
		if (!m_ip_failures.TryGetValue(ip, out var failures))
			m_ip_failures.Add(ip, failures = new Queue<DateTimeOffset>());

		failures.Enqueue(now);
	}

	/// <summary>Drop failure records that are older than the window.</summary>
	private static void PruneFailures(Queue<DateTimeOffset> failures, DateTimeOffset now)
	{
		while (failures.Count != 0 && now - failures.Peek() >= RelayLimits.IpFailureWindow)
			failures.Dequeue();
	}
}
