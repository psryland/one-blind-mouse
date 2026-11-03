using System.Security.Cryptography;
using System.Text;
using OneBlindMouse.Core;

namespace OneBlindMouse.Relay;

/// <summary>
/// One relay room: its join verifier, lock state and member slots. Holds data and simple queries only; the <see cref="Hub"/> applies the rules.
/// All fields are guarded by the <see cref="Hub"/> lock.
/// </summary>
public sealed class Room
{
	private readonly Dictionary<int, Member> m_members = [];
	private byte[] m_verifier;
	private int m_next_id = 1;
	private long m_next_order;

	/// <summary>Create an empty room.</summary>
	public Room(string id, string verifier)
	{
		Id = id;
		m_verifier = Encoding.ASCII.GetBytes(verifier);
	}

	/// <summary>The room id (the first part of the session code).</summary>
	public string Id { get; }

	/// <summary>Member id of the owner, or 0 when the room is empty.</summary>
	public int Owner { get; set; }

	/// <summary>Wrong codes tried since the last rekey.</summary>
	public int Failures { get; set; }

	/// <summary>True when too many wrong codes were tried. Joins are refused until the owner rekeys.</summary>
	public bool Locked { get; set; }

	/// <summary>All members, live and held.</summary>
	public IReadOnlyCollection<Member> Members => m_members.Values;

	/// <summary>True if the room has room for another member. Held members keep their place.</summary>
	public bool IsFull => m_members.Count >= Protocol.MaxMembers;

	/// <summary>True if the room has no live or held members.</summary>
	public bool IsEmpty => m_members.Count == 0;

	/// <summary>Live members only.</summary>
	public IEnumerable<Member> LiveMembers => m_members.Values.Where(m => m.IsLive);

	/// <summary>Replace the join verifier.</summary>
	public void SetVerifier(string verifier)
	{
		m_verifier = Encoding.ASCII.GetBytes(verifier);
	}

	/// <summary>True if <paramref name="verifier"/> matches, compared in constant time so timing does not reveal how much of it is right.</summary>
	public bool VerifierMatches(string verifier)
	{
		return CryptographicOperations.FixedTimeEquals(m_verifier, Encoding.ASCII.GetBytes(verifier));
	}

	/// <summary>Find a member by id.</summary>
	public Member? Find(int id)
	{
		return m_members.GetValueOrDefault(id);
	}

	/// <summary>Add a new live member for <paramref name="session"/>. The room must not be full.</summary>
	public Member Add(Session session, int colour_pref)
	{
		// Ids rotate rather than taking the lowest free value, so a departed member's id is not immediately given to someone else
		var id = m_next_id;
		while (m_members.ContainsKey(id))
			id = id % 255 + 1;

		m_next_id = id % 255 + 1;

		// Honour the preferred colour if no one has it, otherwise use the lowest free slot
		var used = m_members.Values.Select(m => m.Slot).ToHashSet();
		var slot = Palette.IsValidSlot(colour_pref) && !used.Contains(colour_pref) ? colour_pref : Enumerable.Range(0, Palette.Count).First(s => !used.Contains(s));

		// The first member owns the room
		var member = new Member(id, slot, m_next_order++, RandomNumberGenerator.GetHexString(32, lowercase: true), session);
		m_members.Add(id, member);
		if (Owner == 0)
			Owner = id;

		return member;
	}

	/// <summary>
	/// Remove a member. If it was the owner, ownership passes to the oldest remaining member.
	/// Returns true if the owner changed to another member.
	/// </summary>
	public bool Remove(Member member)
	{
		// Ownership follows join order so it is predictable for everyone in the room
		m_members.Remove(member.Id);
		if (Owner != member.Id)
			return false;

		Owner = m_members.Count != 0 ? m_members.Values.MinBy(m => m.Order)!.Id : 0;
		return Owner != 0;
	}
}
