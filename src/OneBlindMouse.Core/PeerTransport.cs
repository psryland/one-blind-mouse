using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OneBlindMouse.Core;

/// <summary>How a member's pointer samples travel between this client and that member.</summary>
public enum EPath
{
	/// <summary>Through the relay.</summary>
	Relayed,

	/// <summary>Directly over UDP, confirmed working in both directions.</summary>
	Direct,
}

/// <summary>Settings for direct peer-to-peer pointer delivery.</summary>
/// <param name="StunServers">"host:port" STUN servers used to learn this client's public address. With none, only local addresses are offered.</param>
/// <param name="LoopbackOnly">Bind to the loopback address and offer only that. Members must then run on the same machine, as in tests.</param>
public sealed record PeerOptions(IReadOnlyList<string> StunServers, bool LoopbackOnly)
{
	/// <summary>Public STUN servers and all local addresses.</summary>
	public static PeerOptions Default { get; } = new(["stun.cloudflare.com:3478", "stun.l.google.com:19302"], false);
}

/// <summary>
/// Sends pointer samples directly between members over UDP when the network allows it. The relay remains the fallback, and carries all control messages.
///
/// Connection set-up, for each pair of members:
/// - Each side makes a new P-256 key for the pair and sends its public key and its candidate endpoints to the other side through the relay (<see cref="PeerMsg"/>).
///   Candidates are the local addresses of this client's UDP socket, plus its public address as seen by a STUN server.
/// - Both sides combine the keys into a shared secret and derive one AES-GCM key per direction. The relay sees only public keys, so it cannot read or forge
///   direct packets. The relay is still trusted to forward the right keys; see the security section of the design.
/// - Both sides send encrypted pings to every candidate of the other side. Sending from both ends at once opens a path through most home and office NATs.
///   A pong proves the path works in both directions, so the pair is then 'direct'. Only authenticated packets count, and replies go to the address the
///   authenticated packet came from, because a NAT may present an address that neither side offered.
/// - Pings continue once a second. If pongs stop, the pair falls back to the relay and probes again for a while.
///
/// Packet layout: [0] magic, [1] sender member id, [2] receiver member id, [3..10] packet counter (little endian), then the encrypted payload and a 16-byte tag.
/// The header is authenticated. The counter is the nonce, and a sliding window rejects replayed packets. The payload is a kind byte plus, for pointer samples,
/// the 12-byte <see cref="PointerFrame"/>.
///
/// Thread safety: all methods may be called from any thread. Callbacks are raised on internal threads, never while holding the internal lock.
/// </summary>
public sealed class PeerTransport : IDisposable
{
	/// <summary>Kinds of direct packet.</summary>
	private enum EKind : byte
	{
		Ping = 1,
		Pong = 2,
		Pointer = 3,
	}

	/// <summary>How often pair state is checked and probes or keepalives are sent.</summary>
	private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

	/// <summary>Timings in milliseconds. See the class comment for how they fit together.</summary>
	private const long ProbeWindowMs = 15_000, KeepaliveMs = 1_000, PathTimeoutMs = 3_500, OfferRetryMs = 5_000, ReofferMs = 30_000, CandidateLifetimeMs = 60_000;

	/// <summary>Unanswered offers sent before giving up, until membership changes.</summary>
	private const int MaxOfferAttempts = 6;

	/// <summary>Packet framing.</summary>
	private const byte Magic = 0xB1;
	private const int HeaderLength = 11, TagLength = 16, NonceLength = 12, MaxPayload = 1 + PointerFrame.Length;

	/// <summary>Windows reports ICMP 'port unreachable' as an error on the next receive unless this is turned off.</summary>
	private const int SioUdpConnReset = unchecked((int)0x9800000C);

	private readonly PeerOptions m_options;
	private readonly Action<PeerMsg> m_signal;
	private readonly Action<PointerFrame> m_received;
	private readonly Action m_paths_changed;
	private readonly Socket m_socket;
	private readonly bool m_dual;
	private readonly CancellationTokenSource m_cts = new();
	private readonly Task m_recv_loop;
	private readonly Task m_tick_loop;

	// Guarded by m_lock
	private readonly Lock m_lock = new();
	private readonly Dictionary<int, Peer> m_peers = [];
	private readonly Dictionary<string, TaskCompletionSource<IPEndPoint>> m_stun = [];
	private int m_epoch = -1;
	private int m_me;
	private string[]? m_candidates;
	private long m_candidates_time;
	private Task? m_gathering;
	private bool m_disposed;

	/// <summary>
	/// Open the UDP socket and start the background loops.
	/// <paramref name="signal"/> must deliver a <see cref="PeerMsg"/> to the relay; <paramref name="received"/> receives authenticated pointer samples with
	/// 'From' set to the sender; <paramref name="paths_changed"/> is raised when a member's <see cref="EPath"/> changes.
	/// </summary>
	public PeerTransport(PeerOptions options, Action<PeerMsg> signal, Action<PointerFrame> received, Action paths_changed)
	{
		m_options = options;
		m_signal = signal;
		m_received = received;
		m_paths_changed = paths_changed;
		(m_socket, m_dual) = CreateSocket(options.LoopbackOnly);
		m_recv_loop = Task.Run(ReceiveLoop);
		m_tick_loop = Task.Run(TickLoop);
	}

	/// <summary>The local UDP endpoint, for diagnostics and tests.</summary>
	public IPEndPoint LocalEndPoint => (IPEndPoint)m_socket.LocalEndPoint!;

	/// <summary>
	/// Set the room membership. <paramref name="epoch"/> must change whenever the client enters a room other than by resuming, because member ids in a new
	/// room may belong to different people. Pairs are created for new members and dropped for members who left.
	/// </summary>
	public void Update(int epoch, int me, IReadOnlyCollection<int> others)
	{
		lock (m_lock)
		{
			// A new room, or a new id in it, invalidates every pair
			if (m_disposed)
				return;

			var now = Environment.TickCount64;
			if (epoch != m_epoch || me != m_me)
			{
				foreach (var peer in m_peers.Values)
					peer.Dispose();

				m_peers.Clear();
				m_epoch = epoch;
				m_me = me;
			}

			// Drop pairs for members who left and add pairs for new members
			foreach (var id in m_peers.Keys.Where(id => !others.Contains(id)).ToList())
			{
				m_peers[id].Dispose();
				m_peers.Remove(id);
			}
			foreach (var id in others)
			{
				if (id != me && !m_peers.ContainsKey(id))
					m_peers.Add(id, new Peer(id, initiator: me < id, now));
			}
		}
	}

	/// <summary>Handle a valid <see cref="PeerMsg"/> from the relay. Its Member field is the sender.</summary>
	public void OnSignal(PeerMsg msg)
	{
		var outbound = new List<PeerMsg>();
		lock (m_lock)
		{
			// Signals for members we do not know yet (or any more) are dropped; the sender retries
			if (m_disposed || !m_peers.TryGetValue(msg.Member, out var peer) || !Protocol.TryDecodePeerKey(msg.Key, out var key))
				return;

			// A new key from the other side means a new shared secret. Keys that are not valid curve points are rejected here.
			if (peer.RemoteKey is null || !peer.RemoteKey.AsSpan().SequenceEqual(key))
			{
				if (!peer.Derive(key, m_me))
					return;
			}

			// Use the latest candidates that this socket can reach, and probe them afresh
			peer.Candidates = msg.Candidates
				.Select(c => Protocol.TryParseCandidate(c, out var ep) ? ep : null)
				.OfType<IPEndPoint>()
				.Where(ep => m_dual || ep.AddressFamily == AddressFamily.InterNetwork)
				.ToArray();
			peer.ProbeUntil = Environment.TickCount64 + ProbeWindowMs;

			// Answer offers, but never answers, so an exchange always ends
			if (!msg.Reply)
				peer.ReplyDue = true;

			SendOffers(outbound, Environment.TickCount64);
		}
		Signal(outbound);
	}

	/// <summary>
	/// Send a pointer sample directly. A sample for one member (To != 0) is sent if that member has a direct path.
	/// A sample for everyone is sent to each member with a direct path. Returns true only if every intended receiver was sent the sample directly.
	/// </summary>
	public bool TrySend(PointerFrame frame)
	{
		// The receiver sets 'From' from the authenticated header, so it is not sent
		Span<byte> body = stackalloc byte[PointerFrame.Length];
		(frame with { From = 0 }).Write(body);
		lock (m_lock)
		{
			// One receiver
			if (m_disposed)
				return false;

			if (frame.To != 0)
				return m_peers.TryGetValue(frame.To, out var target) && target.Direct && Send(target, EKind.Pointer, body, target.Endpoint!);

			// Everyone: report whether anyone still needs the relay
			var all = m_peers.Count != 0;
			foreach (var peer in m_peers.Values)
				all &= peer.Direct && Send(peer, EKind.Pointer, body, peer.Endpoint!);

			return all;
		}
	}

	/// <summary>The members that currently have a direct path.</summary>
	public int[] DirectMembers()
	{
		lock (m_lock)
		{
			return m_peers.Values.Where(p => p.Direct).Select(p => p.Id).ToArray();
		}
	}

	/// <summary>Close the socket and stop the background loops.</summary>
	public void Dispose()
	{
		// Mark disposed first so no new work starts, then stop the loops
		lock (m_lock)
		{
			if (m_disposed)
				return;

			m_disposed = true;
			foreach (var peer in m_peers.Values)
				peer.Dispose();

			m_peers.Clear();
		}
		m_cts.Cancel();
		m_socket.Dispose();
		Task.WaitAll([m_recv_loop, m_tick_loop], TimeSpan.FromSeconds(2));
		m_cts.Dispose();
	}

	/// <summary>Create the UDP socket: dual-stack where IPv6 is available, otherwise IPv4.</summary>
	private static (Socket Socket, bool Dual) CreateSocket(bool loopback_only)
	{
		// Loopback only, for tests
		Socket socket;
		var dual = false;
		if (loopback_only)
		{
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
		}
		else
		{
			// One dual-stack socket serves both IPv4 and IPv6 peers from the same port, which keeps one NAT mapping per client
			try
			{
				socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
				socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
				dual = true;
			}
			catch (SocketException)
			{
				socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				socket.Bind(new IPEndPoint(IPAddress.Any, 0));
			}
		}

		// Otherwise a probe to a closed port would make the next receive fail
		if (OperatingSystem.IsWindows())
			socket.IOControl(SioUdpConnReset, new byte[4], null);

		return (socket, dual);
	}

	/// <summary>Read datagrams until the socket closes, and dispatch them as STUN responses or direct packets.</summary>
	private async Task ReceiveLoop()
	{
		// Datagrams larger than any valid packet are truncated and then rejected
		var buffer = new byte[2048];
		var any = new IPEndPoint(m_dual ? IPAddress.IPv6Any : IPAddress.Any, 0);
		while (!m_cts.IsCancellationRequested)
		{
			try
			{
				var result = await m_socket.ReceiveFromAsync(buffer, SocketFlags.None, any, m_cts.Token);
				var from = Normalise((IPEndPoint)result.RemoteEndPoint);
				var data = buffer.AsSpan(0, result.ReceivedBytes);
				if (Stun.IsStun(data))
					OnStun(data);
				else
					OnPacket(data, from);
			}
			catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException)
			{
				break;
			}
			catch (SocketException)
			{
				// A bad datagram or a transient network change. Pause briefly so a persistent error cannot spin.
				try
				{
					await Task.Delay(100, m_cts.Token);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
	}

	/// <summary>Run <see cref="Tick"/> periodically until disposed.</summary>
	private async Task TickLoop()
	{
		using var timer = new PeriodicTimer(TickInterval);
		try
		{
			while (await timer.WaitForNextTickAsync(m_cts.Token))
				Tick();
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Update path states, and send due offers, probes and keepalives.</summary>
	private void Tick()
	{
		var outbound = new List<PeerMsg>();
		var changed = false;
		lock (m_lock)
		{
			if (m_disposed)
				return;

			var now = Environment.TickCount64;
			foreach (var peer in m_peers.Values)
			{
				// A path is direct while pongs keep arriving. When it is lost, probe again, and the initiator re-offers in case addresses changed.
				var direct = peer.LastPong != 0 && now - peer.LastPong < PathTimeoutMs;
				if (direct != peer.Direct)
				{
					peer.Direct = direct;
					changed = true;
					if (!direct)
					{
						peer.ProbeUntil = now + ProbeWindowMs;
						if (peer.Initiator && now - peer.LastOffer >= ReofferMs)
							peer.OfferDue = true;
					}
				}

				// Repeat unanswered offers a few times, because a signal can be lost while the relay connection is re-established
				if (peer.RemoteKey is null && peer.OfferAttempts < MaxOfferAttempts && now - peer.LastOffer >= OfferRetryMs)
					peer.OfferDue = true;

				// Nothing can be sent until both keys are known
				if (peer.Tx is null)
					continue;

				// Keep a direct path alive (and NAT mappings open) with one ping a second
				if (peer.Direct)
				{
					if (now - peer.LastPing >= KeepaliveMs)
					{
						peer.LastPing = now;
						Send(peer, EKind.Ping, [], peer.Endpoint!);
					}
					continue;
				}

				// Otherwise probe every candidate, and the last address that worked, until the probe window ends
				if (now < peer.ProbeUntil)
				{
					peer.LastPing = now;
					foreach (var ep in peer.Candidates)
						Send(peer, EKind.Ping, [], ep);

					if (peer.Endpoint is { } last && !peer.Candidates.Contains(last))
						Send(peer, EKind.Ping, [], last);
				}
			}

			SendOffers(outbound, now);
		}
		Signal(outbound);
		if (changed)
			m_paths_changed();
	}

	/// <summary>Queue due offers and replies into <paramref name="outbound"/>. Gathers candidates first if they are missing or old. Called under the lock.</summary>
	private void SendOffers(List<PeerMsg> outbound, long now)
	{
		// Anything to send?
		if (!m_peers.Values.Any(p => p.OfferDue || p.ReplyDue))
			return;

		// Offers wait for fresh candidates, because public addresses can change while no one is connected
		if (m_candidates is null || now - m_candidates_time >= CandidateLifetimeMs)
		{
			m_gathering ??= Task.Run(GatherAsync);
			return;
		}

		// A reply covers a due offer too
		foreach (var peer in m_peers.Values.Where(p => p.OfferDue || p.ReplyDue))
		{
			outbound.Add(new PeerMsg(peer.Id, Convert.ToBase64String(peer.PublicKey), m_candidates, Reply: !peer.OfferDue && peer.ReplyDue));
			if (peer.OfferDue)
			{
				peer.LastOffer = now;
				++peer.OfferAttempts;
			}
			peer.OfferDue = false;
			peer.ReplyDue = false;
		}
	}

	/// <summary>Deliver queued signals. Called outside the lock.</summary>
	private void Signal(List<PeerMsg> outbound)
	{
		foreach (var msg in outbound)
			m_signal(msg);
	}

	/// <summary>Find this client's candidate endpoints: public addresses from STUN first, because they work across networks, then local addresses.</summary>
	private async Task GatherAsync()
	{
		// Failures just mean fewer candidates; with none at all the relay is used
		var port = LocalEndPoint.Port;
		var candidates = new List<string>();
		try
		{
			var mapped = await Task.WhenAll(m_options.StunServers.Select(QueryStunAsync));
			candidates.AddRange(mapped.SelectMany(m => m).Select(ep => ep.ToString()));
			candidates.AddRange(LocalCandidates(port));
		}
		catch (Exception)
		{
		}

		// Publish the result; the next tick sends any waiting offers
		lock (m_lock)
		{
			m_candidates = candidates.Distinct().Take(PeerMsg.MaxCandidates).ToArray();
			m_candidates_time = Environment.TickCount64;
			m_gathering = null;
		}
	}

	/// <summary>The local addresses of this socket that another machine might reach.</summary>
	private IEnumerable<string> LocalCandidates(int port)
	{
		// Loopback only, for tests
		if (m_options.LoopbackOnly)
			return [new IPEndPoint(IPAddress.Loopback, port).ToString()];

		// Usable unicast addresses of interfaces that are up. Link-local addresses are left out because they need an interface scope to use.
		var v4 = new List<IPAddress>();
		var v6 = new List<IPAddress>();
		foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				continue;

			foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
			{
				var address = unicast.Address;
				if (address.AddressFamily == AddressFamily.InterNetwork && !(address.GetAddressBytes() is [169, 254, ..]))
					v4.Add(address);
				else if (address.AddressFamily == AddressFamily.InterNetworkV6 && m_dual && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv4MappedToIPv6)
					v6.Add(address);
			}
		}

		// IPv4 first: it is the most likely to share a LAN
		return v4.Concat(v6).Select(a => new IPEndPoint(a, port).ToString());
	}

	/// <summary>Ask one STUN server ("host:port") for this socket's public address, over IPv4 and (if available) IPv6.</summary>
	private async Task<IPEndPoint[]> QueryStunAsync(string server)
	{
		// Resolve the server, with a time limit
		var colon = server.LastIndexOf(':');
		if (colon <= 0 || !int.TryParse(server.AsSpan(colon + 1), out var port))
			return [];

		IPAddress[] addresses;
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(m_cts.Token);
			cts.CancelAfter(TimeSpan.FromSeconds(2));
			addresses = await Dns.GetHostAddressesAsync(server[..colon], cts.Token);
		}
		catch (Exception)
		{
			return [];
		}

		// One query per address family, because a client may have a different public address for each
		var targets = addresses
			.Where(a => a.AddressFamily == AddressFamily.InterNetwork || (m_dual && a.AddressFamily == AddressFamily.InterNetworkV6))
			.GroupBy(a => a.AddressFamily)
			.Select(g => new IPEndPoint(g.First(), port));
		var mapped = await Task.WhenAll(targets.Select(BindingAsync));
		return mapped.OfType<IPEndPoint>().ToArray();
	}

	/// <summary>Send a STUN binding request to <paramref name="server"/>, retrying a few times, and return the mapped address or null.</summary>
	private async Task<IPEndPoint?> BindingAsync(IPEndPoint server)
	{
		// Responses are matched by the random transaction id in the receive loop
		var transaction = RandomNumberGenerator.GetBytes(Stun.TransactionIdLength);
		var key = Convert.ToHexString(transaction);
		var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (m_lock)
		{
			m_stun[key] = tcs;
		}
		try
		{
			// UDP may drop the request or the response, so send up to three times
			var request = Stun.Request(transaction);
			for (var attempt = 0; attempt != 3; ++attempt)
			{
				try
				{
					m_socket.SendTo(request, server);
					return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(500), m_cts.Token);
				}
				catch (TimeoutException)
				{
				}
				catch (SocketException)
				{
					return null;
				}
			}
			return null;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			lock (m_lock)
			{
				m_stun.Remove(key);
			}
		}
	}

	/// <summary>Complete the matching STUN query, if any.</summary>
	private void OnStun(ReadOnlySpan<byte> data)
	{
		// The 96-bit random transaction id is what ties a response to our request
		if (!Stun.TryParseResponse(data, out var transaction, out var mapped))
			return;

		TaskCompletionSource<IPEndPoint>? tcs;
		lock (m_lock)
		{
			m_stun.TryGetValue(Convert.ToHexString(transaction), out tcs);
		}
		tcs?.TrySetResult(Normalise(mapped));
	}

	/// <summary>Authenticate and handle one direct packet. Anything that fails a check is dropped silently, because anyone can send to this port.</summary>
	private void OnPacket(ReadOnlySpan<byte> data, IPEndPoint from)
	{
		// Shape and addressing
		if (data.Length < HeaderLength + 1 + TagLength || data.Length > HeaderLength + MaxPayload + TagLength || data[0] != Magic)
			return;

		PointerFrame? frame = null;
		var changed = false;
		lock (m_lock)
		{
			// Only packets for this member, from a member with an agreed key
			if (m_disposed || data[2] != m_me || !m_peers.TryGetValue(data[1], out var peer) || peer.Rx is null)
				return;

			// Cheap replay check before decrypting, then authenticate the header and payload together
			var counter = BinaryPrimitives.ReadUInt64LittleEndian(data[3..]);
			if (!peer.IsFresh(counter))
				return;

			Span<byte> nonce = stackalloc byte[NonceLength];
			BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
			Span<byte> plain = stackalloc byte[data.Length - HeaderLength - TagLength];
			try
			{
				peer.Rx.Decrypt(nonce, data[HeaderLength..^TagLength], data[^TagLength..], plain, data[..HeaderLength]);
			}
			catch (CryptographicException)
			{
				return;
			}
			peer.Accept(counter);

			// The packet is genuine, so its source address is a working path to the peer (possibly an address neither side offered)
			peer.Endpoint = from;
			switch ((EKind)plain[0])
			{
				case EKind.Ping:
				{
					Send(peer, EKind.Pong, [], from);
					break;
				}
				case EKind.Pong:
				{
					// A pong answers our ping, so packets get through in both directions
					peer.LastPong = Environment.TickCount64;
					changed = !peer.Direct;
					peer.Direct = true;
					break;
				}
				case EKind.Pointer:
				{
					// The sender is the authenticated member, whatever the sample says
					if (PointerFrame.TryRead(plain[1..], out var f) && (f.To == 0 || f.To == m_me))
						frame = f with { From = (byte)peer.Id };

					break;
				}
				default:
				{
					// Unknown kinds are ignored
					break;
				}
			}
		}

		// Report outside the lock
		if (changed)
			m_paths_changed();

		if (frame is { } received)
			m_received(received);
	}

	/// <summary>Encrypt and send one packet to <paramref name="to"/>. Returns false if the network refused it. Called under the lock.</summary>
	private bool Send(Peer peer, EKind kind, ReadOnlySpan<byte> body, IPEndPoint to)
	{
		// Header: magic, from, to, counter. The counter also forms the nonce; it never repeats for a key because each direction has its own key.
		var counter = ++peer.TxCounter;
		Span<byte> packet = stackalloc byte[HeaderLength + 1 + body.Length + TagLength];
		packet[0] = Magic;
		packet[1] = (byte)m_me;
		packet[2] = (byte)peer.Id;
		BinaryPrimitives.WriteUInt64LittleEndian(packet[3..], counter);

		// Encrypt the kind and body, authenticating the header too
		Span<byte> nonce = stackalloc byte[NonceLength];
		BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
		Span<byte> plain = stackalloc byte[1 + body.Length];
		plain[0] = (byte)kind;
		body.CopyTo(plain[1..]);
		peer.Tx!.Encrypt(nonce, plain, packet[HeaderLength..^TagLength], packet[^TagLength..], packet[..HeaderLength]);

		// Unreachable addresses (e.g. an IPv6 candidate without IPv6 routing) are expected while probing
		try
		{
			m_socket.SendTo(packet, SocketFlags.None, to);
			return true;
		}
		catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
		{
			return false;
		}
	}

	/// <summary>Present IPv4 peers on a dual-stack socket as plain IPv4 addresses, so they compare equal to IPv4 candidates.</summary>
	private static IPEndPoint Normalise(IPEndPoint ep)
	{
		return ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;
	}

	/// <summary>The direct-path state for one other member. Guarded by the transport lock.</summary>
	private sealed class Peer : IDisposable
	{
		private readonly ECDiffieHellman m_key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
		private ulong m_rx_max;
		private ulong m_rx_window;

		/// <summary>Create a pair with a new key. The initiator offers first; the other side offers only if no offer arrives.</summary>
		public Peer(int id, bool initiator, long now)
		{
			// Export the public key as an uncompressed point
			var q = m_key.ExportParameters(false).Q;
			Id = id;
			Initiator = initiator;
			PublicKey = [0x04, .. q.X!, .. q.Y!];
			LastOffer = initiator ? long.MinValue / 2 : now;
		}

		/// <summary>The other member's id.</summary>
		public int Id { get; }

		/// <summary>True if this side (the lower member id) offers first.</summary>
		public bool Initiator { get; }

		/// <summary>This side's public key for the pair.</summary>
		public byte[] PublicKey { get; }

		/// <summary>The other side's public key, once known.</summary>
		public byte[]? RemoteKey { get; private set; }

		/// <summary>Packet keys for each direction, once both public keys are known.</summary>
		public AesGcm? Tx { get; private set; }
		public AesGcm? Rx { get; private set; }

		/// <summary>The counter of the last packet sent with <see cref="Tx"/>.</summary>
		public ulong TxCounter { get; set; }

		/// <summary>Where the other side may be reached.</summary>
		public IPEndPoint[] Candidates { get; set; } = [];

		/// <summary>The source address of the last authenticated packet, which is where packets are sent once the path is direct.</summary>
		public IPEndPoint? Endpoint { get; set; }

		/// <summary>True while the path is confirmed working in both directions.</summary>
		public bool Direct { get; set; }

		/// <summary>Times in <see cref="Environment.TickCount64"/> milliseconds. LastPong is zero until the first pong.</summary>
		public long ProbeUntil { get; set; }
		public long LastPing { get; set; }
		public long LastPong { get; set; }
		public long LastOffer { get; set; }

		/// <summary>Unanswered offers sent since the pair was created.</summary>
		public int OfferAttempts { get; set; }

		/// <summary>An offer, or a reply to the other side's offer, is waiting to be sent.</summary>
		public bool OfferDue { get; set; }
		public bool ReplyDue { get; set; }

		/// <summary>
		/// Agree packet keys with the other side's public key <paramref name="remote"/>. Returns false if it is not a valid P-256 point.
		/// Each direction gets its own key, derived with both public keys (in member id order) and the direction, so both sides derive the same pair of keys.
		/// </summary>
		public bool Derive(byte[] remote, int me)
		{
			// Import the point; this rejects points that are not on the curve
			byte[] secret;
			try
			{
				using var other = ECDiffieHellman.Create(new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					Q = new ECPoint { X = remote[1..33], Y = remote[33..65] },
				});
				secret = m_key.DeriveRawSecretAgreement(other.PublicKey);
			}
			catch (CryptographicException)
			{
				return false;
			}

			// Mix in both public keys so the keys belong to this exact exchange
			byte[] salt = me < Id ? [.. PublicKey, .. remote] : [.. remote, .. PublicKey];
			var tx = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.ASCII.GetBytes($"obm-p2p {me}>{Id}"));
			var rx = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.ASCII.GetBytes($"obm-p2p {Id}>{me}"));
			CryptographicOperations.ZeroMemory(secret);

			// New keys restart the counters and the path
			Tx?.Dispose();
			Rx?.Dispose();
			Tx = new AesGcm(tx, TagLength);
			Rx = new AesGcm(rx, TagLength);
			RemoteKey = remote;
			TxCounter = 0;
			m_rx_max = 0;
			m_rx_window = 0;
			Endpoint = null;
			LastPong = 0;
			return true;
		}

		/// <summary>True if <paramref name="counter"/> has not been received before and is not too old to tell. The window covers the last 64 counters.</summary>
		public bool IsFresh(ulong counter)
		{
			if (counter == 0)
				return false;

			if (counter > m_rx_max)
				return true;

			var age = m_rx_max - counter;
			return age < 64 && (m_rx_window & (1UL << (int)age)) == 0;
		}

		/// <summary>Record an authenticated counter as received.</summary>
		public void Accept(ulong counter)
		{
			// Bit n of the window is counter (max - n)
			if (counter > m_rx_max)
			{
				var shift = counter - m_rx_max;
				m_rx_window = shift >= 64 ? 0 : m_rx_window << (int)shift;
				m_rx_window |= 1;
				m_rx_max = counter;
			}
			else
			{
				m_rx_window |= 1UL << (int)(m_rx_max - counter);
			}
		}

		public void Dispose()
		{
			m_key.Dispose();
			Tx?.Dispose();
			Rx?.Dispose();
		}
	}
}
