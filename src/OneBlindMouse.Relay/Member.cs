using OneBlindMouse.Core;

namespace OneBlindMouse.Relay;

/// <summary>
/// A member slot in a room. A member is either live (attached to a connection) or held (its connection dropped and it may be resumed).
/// All fields are guarded by the <see cref="Hub"/> lock.
/// </summary>
public sealed class Member
{
	/// <summary>Create a live member attached to <paramref name="session"/>.</summary>
	public Member(int id, int slot, long order, string token, Session session)
	{
		// The display details start from the connection's current hello/rename/target values
		Id = id;
		Slot = slot;
		Order = order;
		Token = token;
		Session = session;
		Name = session.Name;
		Aspect = session.Aspect;
		Label = session.Label;
	}

	/// <summary>Member id, unique within the room.</summary>
	public int Id { get; }

	/// <summary>Colour slot, unique within the room.</summary>
	public int Slot { get; }

	/// <summary>Join order within the room. The member with the lowest order owns the room.</summary>
	public long Order { get; }

	/// <summary>Secret that lets a new connection resume this slot.</summary>
	public string Token { get; }

	/// <summary>The connection, or null while the member is held.</summary>
	public Session? Session { get; set; }

	/// <summary>When a held member is removed for good.</summary>
	public DateTimeOffset HoldExpires { get; set; }

	/// <summary>Sanitised display name.</summary>
	public string Name { get; set; }

	/// <summary>Pointer target aspect ratio, or zero if unknown.</summary>
	public double Aspect { get; set; }

	/// <summary>Pointer target description.</summary>
	public string Label { get; set; }

	/// <summary>True if the member has a connection.</summary>
	public bool IsLive => Session is not null;

	/// <summary>The public description of this member.</summary>
	public MemberInfo Info => new(Id, Name, Slot, Aspect, Label);
}
