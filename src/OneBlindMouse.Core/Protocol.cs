using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneBlindMouse.Core;

/// <summary>
/// Limits and constants shared by the relay and clients.
/// Control messages are JSON text frames with a "t" type field. Pointer samples are binary frames; see <see cref="PointerFrame"/>.
/// </summary>
public static class Protocol
{
	/// <summary>The protocol version spoken by this build.</summary>
	public const int Version = 1;

	/// <summary>Path of the relay WebSocket endpoint.</summary>
	public const string Path = "/v1";

	/// <summary>Maximum members in one session. Each member gets a distinct colour slot.</summary>
	public const int MaxMembers = Palette.Count;

	/// <summary>Maximum length of a display name, in UTF-16 code units.</summary>
	public const int MaxNameLength = 24;

	/// <summary>Maximum length of a target label, in UTF-16 code units.</summary>
	public const int MaxLabelLength = 64;

	/// <summary>Maximum size of a control message, in bytes.</summary>
	public const int MaxTextBytes = 2048;

	/// <summary>Valid range of a target aspect ratio (width / height). Zero means "unknown".</summary>
	public const double MinAspect = 0.1, MaxAspect = 10.0;

	/// <summary>Serialise a control message to UTF-8 JSON.</summary>
	public static byte[] Serialise(ControlMessage msg)
	{
		return JsonSerializer.SerializeToUtf8Bytes(msg, ProtocolJson.Default.ControlMessage);
	}

	/// <summary>
	/// Parse and validate a control message. Returns false for malformed JSON, unknown types, missing or extra fields, and out-of-range values.
	/// Received data is untrusted, so every message must pass <see cref="ControlMessage.IsValid"/> before use.
	/// </summary>
	public static bool TryParse(ReadOnlySpan<byte> utf8, out ControlMessage? msg)
	{
		// Size limit first so oversized input never reaches the parser
		msg = null;
		if (utf8.Length == 0 || utf8.Length > MaxTextBytes)
			return false;

		// Strict parse: the source-generated options reject unknown members, missing required members and null for non-nullable members
		try
		{
			msg = JsonSerializer.Deserialize(utf8, ProtocolJson.Default.ControlMessage);
		}
		catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
		{
			return false;
		}
		if (msg is null || !msg.IsValid())
		{
			msg = null;
			return false;
		}
		return true;
	}

	/// <summary>True if <paramref name="name"/> is a sanitised, non-empty display name.</summary>
	public static bool IsValidName(string? name)
	{
		return name is not null && name.Length != 0 && name.Length <= MaxNameLength && Names.Sanitise(name) == name;
	}

	/// <summary>True if <paramref name="label"/> is a sanitised target label (empty is allowed).</summary>
	public static bool IsValidLabel(string? label)
	{
		return label is not null && label.Length <= MaxLabelLength && Names.Sanitise(label, MaxLabelLength) == label;
	}

	/// <summary>True if <paramref name="aspect"/> is zero (unknown) or within the allowed range.</summary>
	public static bool IsValidAspect(double aspect)
	{
		return aspect == 0 || (double.IsFinite(aspect) && aspect >= MinAspect && aspect <= MaxAspect);
	}

	/// <summary>True if <paramref name="member"/> is a valid member id. Zero is reserved for "everyone".</summary>
	public static bool IsValidMember(int member)
	{
		return member >= 1 && member <= 255;
	}

	/// <summary>True if <paramref name="token"/> has the shape of a resume token (32 lowercase hex characters).</summary>
	public static bool IsValidToken(string? token)
	{
		return token is not null && token.Length == 32 && token.All(char.IsAsciiHexDigitLower);
	}
}

/// <summary>Error codes carried by <see cref="ErrorMsg"/>.</summary>
public static class ErrorCodes
{
	public const string BadRequest = "bad_request";
	public const string Version = "version";
	public const string RateLimited = "rate_limited";
	public const string NoRoom = "no_room";
	public const string BadCode = "bad_code";
	public const string RoomFull = "room_full";
	public const string RoomLocked = "room_locked";
	public const string RoomUnavailable = "room_unavailable";
	public const string ResumeFailed = "resume_failed";
	public const string NotOwner = "not_owner";
	public const string NotInRoom = "not_in_room";
	public const string Busy = "busy";
}

/// <summary>Public information about one session member.</summary>
/// <param name="Id">Member id, unique within the room (1..255).</param>
/// <param name="Name">Sanitised display name.</param>
/// <param name="Slot">Colour slot (index into <see cref="Palette"/>), unique within the room.</param>
/// <param name="Aspect">Aspect ratio of the member's pointer target, or zero if unknown.</param>
/// <param name="Label">Description of the member's pointer target, e.g. "Monitor 1".</param>
public sealed record MemberInfo(int Id, string Name, int Slot, double Aspect, string Label)
{
	/// <summary>True if every field is within its documented range.</summary>
	public bool IsValid()
	{
		return Protocol.IsValidMember(Id) && Protocol.IsValidName(Name) && Palette.IsValidSlot(Slot) && Protocol.IsValidAspect(Aspect) && Protocol.IsValidLabel(Label);
	}
}

/// <summary>Base type of all JSON control messages. The "t" field selects the concrete type.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMsg), "hello")]
[JsonDerivedType(typeof(CreateMsg), "create")]
[JsonDerivedType(typeof(JoinMsg), "join")]
[JsonDerivedType(typeof(ResumeMsg), "resume")]
[JsonDerivedType(typeof(LeaveMsg), "leave")]
[JsonDerivedType(typeof(KickMsg), "kick")]
[JsonDerivedType(typeof(RekeyMsg), "rekey")]
[JsonDerivedType(typeof(RenameMsg), "rename")]
[JsonDerivedType(typeof(TargetMsg), "target")]
[JsonDerivedType(typeof(WelcomeMsg), "welcome")]
[JsonDerivedType(typeof(JoinedMsg), "joined")]
[JsonDerivedType(typeof(MemberJoinedMsg), "member_joined")]
[JsonDerivedType(typeof(MemberUpdatedMsg), "member_updated")]
[JsonDerivedType(typeof(MemberLeftMsg), "member_left")]
[JsonDerivedType(typeof(OwnerChangedMsg), "owner_changed")]
[JsonDerivedType(typeof(RekeyedMsg), "rekeyed")]
[JsonDerivedType(typeof(LockedMsg), "locked")]
[JsonDerivedType(typeof(KickedMsg), "kicked")]
[JsonDerivedType(typeof(ErrorMsg), "error")]
public abstract record ControlMessage
{
	/// <summary>True if every field is within its documented range.</summary>
	public abstract bool IsValid();
}

// Client → relay ─────────────────────────────────────────────────────────────

/// <summary>First message on every connection. Name is sanitised; colour_pref is a palette slot or -1 for "any".</summary>
public sealed record HelloMsg(int Version, string Name, int ColourPref) : ControlMessage
{
	public override bool IsValid()
	{
		return Version >= 1 && Protocol.IsValidName(Name) && (ColourPref == -1 || Palette.IsValidSlot(ColourPref));
	}
}

/// <summary>
/// Create a room owned by the sender, with a room id chosen by the sender. The client chooses the id because the verifier depends on it.
/// The relay replies room_unavailable if the id is in use; the client then picks another id. On success the sender leaves its current room.
/// </summary>
public sealed record CreateMsg(string Room, string Verifier) : ControlMessage
{
	public override bool IsValid()
	{
		return SessionCode.IsValidRoom(Room) && SessionCode.IsValidVerifier(Verifier);
	}
}

/// <summary>Join an existing room. On success the sender leaves its current room.</summary>
public sealed record JoinMsg(string Room, string Verifier) : ControlMessage
{
	public override bool IsValid()
	{
		return SessionCode.IsValidRoom(Room) && SessionCode.IsValidVerifier(Verifier);
	}
}

/// <summary>Reclaim a member slot after a dropped connection.</summary>
public sealed record ResumeMsg(string Room, int Member, string Token) : ControlMessage
{
	public override bool IsValid()
	{
		return SessionCode.IsValidRoom(Room) && Protocol.IsValidMember(Member) && Protocol.IsValidToken(Token);
	}
}

/// <summary>Leave the current room.</summary>
public sealed record LeaveMsg : ControlMessage
{
	public override bool IsValid()
	{
		return true;
	}
}

/// <summary>Remove a member from the room (owner only).</summary>
public sealed record KickMsg(int Member) : ControlMessage
{
	public override bool IsValid()
	{
		return Protocol.IsValidMember(Member);
	}
}

/// <summary>Change the room secret (owner only). The relay shares the new secret with members so everyone can show the current code.</summary>
public sealed record RekeyMsg(string Verifier, string Secret) : ControlMessage
{
	public override bool IsValid()
	{
		return SessionCode.IsValidVerifier(Verifier) && SessionCode.IsValidSecret(Secret);
	}
}

/// <summary>Change the sender's display name.</summary>
public sealed record RenameMsg(string Name) : ControlMessage
{
	public override bool IsValid()
	{
		return Protocol.IsValidName(Name);
	}
}

/// <summary>Describe the area on the sender's screen where other members' pointers appear.</summary>
public sealed record TargetMsg(double Aspect, string Label) : ControlMessage
{
	public override bool IsValid()
	{
		return Protocol.IsValidAspect(Aspect) && Protocol.IsValidLabel(Label);
	}
}

// Relay → client ─────────────────────────────────────────────────────────────

/// <summary>Reply to hello. Clients older than min_version must update.</summary>
public sealed record WelcomeMsg(int MinVersion) : ControlMessage
{
	public override bool IsValid()
	{
		return MinVersion >= 1;
	}
}

/// <summary>The sender is now a member of a room, after create, join or resume.</summary>
public sealed record JoinedMsg(string Room, int Member, string Token, int Owner, MemberInfo[] Members, bool Resumed) : ControlMessage
{
	public override bool IsValid()
	{
		return SessionCode.IsValidRoom(Room) && Protocol.IsValidMember(Member) && Protocol.IsValidToken(Token) && Protocol.IsValidMember(Owner)
			&& Members.Length <= Protocol.MaxMembers && Members.All(m => m is not null && m.IsValid());
	}
}

/// <summary>Another member joined the room.</summary>
public sealed record MemberJoinedMsg(MemberInfo Member) : ControlMessage
{
	public override bool IsValid()
	{
		return Member is not null && Member.IsValid();
	}
}

/// <summary>A member's name or target changed.</summary>
public sealed record MemberUpdatedMsg(MemberInfo Member) : ControlMessage
{
	public override bool IsValid()
	{
		return Member is not null && Member.IsValid();
	}
}

/// <summary>A member left the room.</summary>
public sealed record MemberLeftMsg(int Member) : ControlMessage
{
	public override bool IsValid()
	{
		return Protocol.IsValidMember(Member);
	}
}

/// <summary>The room owner changed, because the previous owner left.</summary>
public sealed record OwnerChangedMsg(int Owner) : ControlMessage
{
	public override bool IsValid()
	{
		return Protocol.IsValidMember(Owner);
	}
}

/// <summary>The room secret changed. The current code is the room id plus this secret.</summary>
public sealed record RekeyedMsg(string Secret) : ControlMessage
{
	public override bool IsValid()
	{
		return SessionCode.IsValidSecret(Secret);
	}
}

/// <summary>Sent to the owner when too many wrong codes were tried. Joins are refused until the owner sends a rekey.</summary>
public sealed record LockedMsg : ControlMessage
{
	public override bool IsValid()
	{
		return true;
	}
}

/// <summary>The owner removed the receiver from the room.</summary>
public sealed record KickedMsg : ControlMessage
{
	public override bool IsValid()
	{
		return true;
	}
}

/// <summary>A request failed. See <see cref="ErrorCodes"/>.</summary>
public sealed record ErrorMsg(string Code, string Message) : ControlMessage
{
	public override bool IsValid()
	{
		return Code is not null && Code.Length is > 0 and <= 32 && Message is not null && Message.Length <= 256;
	}
}

/// <summary>Source-generated, strict JSON settings for control messages. Strictness is part of the input validation.</summary>
[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	RespectNullableAnnotations = true,
	RespectRequiredConstructorParameters = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.Never,
	NumberHandling = JsonNumberHandling.Strict,
	MaxDepth = 8)]
[JsonSerializable(typeof(ControlMessage))]
internal sealed partial class ProtocolJson : JsonSerializerContext
{
}
