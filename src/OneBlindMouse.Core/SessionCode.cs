using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OneBlindMouse.Core;

/// <summary>
/// A session join code. It combines a relay-allocated room id with a secret that the room owner generates.
/// Codes use Crockford base32 so they are easy to read aloud: input ignores case and hyphens, and maps 'O' to '0' and 'I'/'L' to '1'.
/// The display form is "K7QM-4PZX", where the first <see cref="RoomLength"/> characters are the room id and the rest are the secret.
/// </summary>
public readonly partial record struct SessionCode
{
	/// <summary>The Crockford base32 alphabet (no I, L, O or U).</summary>
	public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

	/// <summary>Number of characters in the room id part.</summary>
	public const int RoomLength = 3;

	/// <summary>Number of characters in the secret part.</summary>
	public const int SecretLength = 5;

	/// <summary>Total number of significant characters in a code.</summary>
	public const int Length = RoomLength + SecretLength;

	/// <summary>Create a code from a valid room id and secret.</summary>
	public SessionCode(string room, string secret)
	{
		// Callers own validation of external input; this only guards against programmer error
		if (!IsValidRoom(room) || !IsValidSecret(secret))
			throw new ArgumentException("Invalid session code parts");

		Room = room;
		Secret = secret;
	}

	/// <summary>The room id, which routes the join on the relay. It is not secret.</summary>
	public string Room { get; }

	/// <summary>The secret that proves knowledge of the code.</summary>
	public string Secret { get; }

	/// <summary>The code without separators, e.g. "K7QM4PZX".</summary>
	public string Compact => Room + Secret;

	/// <summary>The code as shown to users, e.g. "K7QM-4PZX".</summary>
	public string Display => $"{Compact[..4]}-{Compact[4..]}";

	/// <summary>The value the relay compares to admit a member. See <see cref="ComputeVerifier"/>.</summary>
	public string Verifier => ComputeVerifier(Room, Secret);

	/// <inheritdoc/>
	public override string ToString()
	{
		return Display;
	}

	/// <summary>Generate a new random secret.</summary>
	public static string NewSecret()
	{
		return RandomString(SecretLength);
	}

	/// <summary>Generate a new random room id.</summary>
	public static string NewRoom()
	{
		return RandomString(RoomLength);
	}

	/// <summary>
	/// Compute the join verifier for a room and secret: lowercase hex of HMAC-SHA256(key: secret, message: room).
	/// The verifier is bound to the room so it cannot be reused for a different room id.
	/// </summary>
	public static string ComputeVerifier(string room, string secret)
	{
		var mac = HMACSHA256.HashData(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(room));
		return Convert.ToHexStringLower(mac);
	}

	/// <summary>True if <paramref name="verifier"/> has the shape produced by <see cref="ComputeVerifier"/>.</summary>
	public static bool IsValidVerifier(string? verifier)
	{
		return verifier is not null && VerifierRegex().IsMatch(verifier);
	}

	/// <summary>True if <paramref name="room"/> is a canonical room id.</summary>
	public static bool IsValidRoom(string? room)
	{
		return room is not null && room.Length == RoomLength && room.All(c => Alphabet.Contains(c));
	}

	/// <summary>True if <paramref name="secret"/> is a canonical secret.</summary>
	public static bool IsValidSecret(string? secret)
	{
		return secret is not null && secret.Length == SecretLength && secret.All(c => Alphabet.Contains(c));
	}

	/// <summary>
	/// Parse user input into a code. The input may be a bare code ("k7qm-4pzx"), a web link ("https://host/j/K7QM4PZX"),
	/// or a protocol link ("obm://join/K7QM4PZX"). Surrounding whitespace is ignored.
	/// </summary>
	public static bool TryParse(string? text, out SessionCode code)
	{
		// Reject missing or unreasonably long input before any further work
		code = default;
		if (string.IsNullOrWhiteSpace(text) || text.Length > 256)
			return false;

		// Extract the code part from a link, otherwise treat the whole input as a code
		var candidate = text.Trim();
		var link = LinkRegex().Match(candidate);
		if (link.Success)
			candidate = link.Groups["code"].Value;

		// Normalise the Crockford aliases and drop separators, then require exactly the canonical alphabet
		var sb = new StringBuilder(Length);
		foreach (var ch in candidate)
		{
			// Separators are allowed anywhere because people group codes differently
			if (ch == '-' || ch == ' ')
				continue;

			var c = char.ToUpperInvariant(ch) switch
			{
				'O' => '0',
				'I' => '1',
				'L' => '1',
				var other => other,
			};
			if (!Alphabet.Contains(c) || sb.Length == Length)
				return false;

			sb.Append(c);
		}
		if (sb.Length != Length)
			return false;

		// Split into the room and secret parts
		var compact = sb.ToString();
		code = new SessionCode(compact[..RoomLength], compact[RoomLength..]);
		return true;
	}

	/// <summary>Return <paramref name="count"/> random characters from <see cref="Alphabet"/>.</summary>
	private static string RandomString(int count)
	{
		// The alphabet has 32 symbols, so a value in [0,32) maps to one symbol without bias
		Span<char> chars = stackalloc char[count];
		for (var i = 0; i != count; ++i)
			chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

		return new string(chars);
	}

	[GeneratedRegex(@"^(?:https?://[^/\s]+/j/|obm://join/)(?<code>[0-9A-Za-z -]{1,16})/?$", RegexOptions.IgnoreCase)]
	private static partial Regex LinkRegex();

	[GeneratedRegex(@"^[0-9a-f]{64}$")]
	private static partial Regex VerifierRegex();
}
