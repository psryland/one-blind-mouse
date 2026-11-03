namespace OneBlindMouse.Relay;

/// <summary>Abuse limits enforced by the relay. These are deployment policy, not part of the wire protocol.</summary>
public static class RelayLimits
{
	/// <summary>Oldest protocol version the relay accepts.</summary>
	public const int MinVersion = 1;

	/// <summary>How long a disconnected member's slot is kept so the client can resume it.</summary>
	public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(30);

	/// <summary>How long a new connection has to send hello.</summary>
	public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

	/// <summary>Wrong codes tried against one room before it locks. Joins then fail until the owner rekeys.</summary>
	public const int RoomFailuresBeforeLock = 5;

	/// <summary>Failed joins allowed from one IP address within <see cref="IpFailureWindow"/>.</summary>
	public const int IpFailures = 20;

	/// <summary>The sliding window for <see cref="IpFailures"/>.</summary>
	public static readonly TimeSpan IpFailureWindow = TimeSpan.FromMinutes(10);

	/// <summary>Concurrent connections allowed from one IP address. Offices behind one NAT address share this.</summary>
	public const int IpConnections = 20;

	/// <summary>Control messages per connection: sustained rate per second and burst.</summary>
	public const double TextRate = 20, TextBurst = 40;

	/// <summary>Pointer frames per connection: sustained rate per second and burst. Clients send about 30 per second.</summary>
	public const double BinaryRate = 60, BinaryBurst = 120;

	/// <summary>Room creations per connection: sustained rate per second (5 per minute) and burst.</summary>
	public const double CreateRate = 5.0 / 60.0, CreateBurst = 5;

	/// <summary>
	/// Misbehaviour budget per connection. Rejected messages spend from it and it refills slowly; when it runs out the connection is closed.
	/// A brief burst of excess traffic only costs an error reply, but a sustained flood ends the connection.
	/// </summary>
	public const double ViolationRate = 1, ViolationBurst = 20;

	/// <summary>Violation cost of a control message over the text rate.</summary>
	public const double ExcessTextCost = 1;

	/// <summary>Violation cost of a malformed control message. Valid clients never send these.</summary>
	public const double BadRequestCost = 5;

	/// <summary>Violation cost of a pointer frame over the binary rate. Small, so only a flood well above the limit closes the connection.</summary>
	public const double ExcessBinaryCost = 0.05;

	/// <summary>Outbound queue length per connection. Pointer frames are dropped when the queue is half full so control messages still fit.</summary>
	public const int OutboundQueue = 256;
}
