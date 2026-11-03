using System.Text;
using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Tests for strict parsing and validation of control messages.</summary>
public class ProtocolTests
{
	private static readonly string Verifier = SessionCode.ComputeVerifier("K7Q", "M4PZX");
	private static readonly string Token = new('a', 32);

	/// <summary>One valid instance of every message type.</summary>
	public static TheoryData<ControlMessage> ValidMessages => new()
	{
		new HelloMsg(1, "Alice", -1),
		new HelloMsg(1, "Bob", 7),
		new CreateMsg("K7Q", Verifier),
		new JoinMsg("K7Q", Verifier),
		new ResumeMsg("K7Q", 3, Token),
		new LeaveMsg(),
		new KickMsg(255),
		new RekeyMsg(Verifier, "M4PZX"),
		new RenameMsg("Carol"),
		new TargetMsg(1.7777, "Monitor 1"),
		new TargetMsg(0, ""),
		new WelcomeMsg(1),
		new JoinedMsg("K7Q", 1, Token, 1, [new MemberInfo(1, "Alice", 0, 0, "")], false),
		new MemberJoinedMsg(new MemberInfo(2, "Bob", 1, 1.5, "Screen")),
		new MemberUpdatedMsg(new MemberInfo(2, "Bob", 1, 1.5, "Screen")),
		new MemberLeftMsg(2),
		new OwnerChangedMsg(2),
		new RekeyedMsg("M4PZX"),
		new LockedMsg(),
		new KickedMsg(),
		new ErrorMsg(ErrorCodes.BadCode, "Wrong code"),
	};

	[Theory]
	[MemberData(nameof(ValidMessages))]
	public void RoundTripsEveryMessageType(ControlMessage msg)
	{
		// Compare serialised forms because records holding arrays use reference equality
		var bytes = Protocol.Serialise(msg);
		Assert.True(Protocol.TryParse(bytes, out var parsed));
		Assert.Equal(msg.GetType(), parsed!.GetType());
		Assert.Equal(bytes, Protocol.Serialise(parsed));
	}

	[Fact]
	public void UsesSnakeCaseAndTypeField()
	{
		var json = Encoding.UTF8.GetString(Protocol.Serialise(new HelloMsg(1, "Alice", -1)));
		Assert.Equal("""{"t":"hello","version":1,"name":"Alice","colour_pref":-1}""", json);
	}

	[Theory]
	[InlineData("""{"t":"hello","version":1,"name":"Alice","colour_pref":-1}""", true)]
	[InlineData("""{"t":"bogus"}""", false)]
	[InlineData("""{"name":"Alice","version":1,"colour_pref":-1}""", false)]
	[InlineData("""{"t":"leave","x":1}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"Alice","colour_pref":-1,"extra":true}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"Alice"}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":null,"colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":"1","name":"Alice","colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":0,"name":"Alice","colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"Alice","colour_pref":8}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"","colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":" Alice","colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"A\u202Eb","colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"A\u0007","colour_pref":-1}""", false)]
	[InlineData("""{"t":"hello","version":1,"name":"Alice","colour_pref":-1.5}""", false)]
	[InlineData("""{"t":"kick","member":0}""", false)]
	[InlineData("""{"t":"kick","member":256}""", false)]
	[InlineData("""{"t":"target","aspect":11,"label":""}""", false)]
	[InlineData("""{"t":"target","aspect":0.05,"label":""}""", false)]
	[InlineData("""{"t":"create","room":"K7U","verifier":"00"}""", false)]
	[InlineData("""{"t":"create","room":"k7q","verifier":"0000000000000000000000000000000000000000000000000000000000000000"}""", false)]
	[InlineData("""{"t":"create","room":"K7Q","verifier":"000000000000000000000000000000000000000000000000000000000000000A"}""", false)]
	[InlineData("""{"t":"resume","room":"K7Q","member":1,"token":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""", false)]
	[InlineData("""{"t":"rekeyed","secret":"M4PZ"}""", false)]
	[InlineData("""{"t":"leave"}""", true)]
	[InlineData("""[]""", false)]
	[InlineData("""null""", false)]
	[InlineData("""{"t":"leave""", false)]
	[InlineData("""not json""", false)]
	public void ParsesStrictly(string json, bool expected)
	{
		Assert.Equal(expected, Protocol.TryParse(Encoding.UTF8.GetBytes(json), out var msg));
		Assert.Equal(expected, msg is not null);
	}

	[Fact]
	public void RejectsEmptyAndOversizeInput()
	{
		// Pad a valid message with white space past the size limit
		Assert.False(Protocol.TryParse([], out _));
		var json = """{"t":"leave"}""";
		var padded = json + new string(' ', Protocol.MaxTextBytes - json.Length + 1);
		Assert.False(Protocol.TryParse(Encoding.UTF8.GetBytes(padded), out _));
		Assert.True(Protocol.TryParse(Encoding.UTF8.GetBytes(padded[..Protocol.MaxTextBytes]), out _));
	}

	[Fact]
	public void RejectsTooManyMembers()
	{
		var members = Enumerable.Range(1, Protocol.MaxMembers + 1).Select(i => new MemberInfo(i, $"M{i}", i % Palette.Count, 0, "")).ToArray();
		var bytes = Protocol.Serialise(new JoinedMsg("K7Q", 1, Token, 1, members, false));
		Assert.False(Protocol.TryParse(bytes, out _));
	}
}
