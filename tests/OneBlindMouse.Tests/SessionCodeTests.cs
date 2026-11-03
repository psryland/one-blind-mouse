using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Tests for session code parsing, formatting and verifiers.</summary>
public class SessionCodeTests
{
	[Theory]
	[InlineData("K7QM4PZX")]
	[InlineData("K7QM-4PZX")]
	[InlineData("k7qm-4pzx")]
	[InlineData("K7QM 4PZX")]
	[InlineData("  K7Q-M4P-ZX  ")]
	[InlineData("https://example.com/j/K7QM4PZX")]
	[InlineData("https://example.com/j/k7qm-4pzx/")]
	[InlineData("http://localhost:8080/j/K7QM4PZX")]
	[InlineData("obm://join/K7QM4PZX")]
	[InlineData("OBM://JOIN/k7qm-4pzx")]
	public void ParsesAcceptedForms(string text)
	{
		Assert.True(SessionCode.TryParse(text, out var code));
		Assert.Equal("K7Q", code.Room);
		Assert.Equal("M4PZX", code.Secret);
		Assert.Equal("K7QM4PZX", code.Compact);
		Assert.Equal("K7QM-4PZX", code.Display);
		Assert.Equal("K7QM-4PZX", code.ToString());
	}

	[Fact]
	public void MapsCrockfordAliases()
	{
		// O reads as zero; I and L read as one, in either case
		Assert.True(SessionCode.TryParse("OIL0-oil0", out var code));
		Assert.Equal("01100110", code.Compact);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("K7QM4PZ")]
	[InlineData("K7QM4PZXA")]
	[InlineData("K7QM4PZU")]
	[InlineData("K7QM_4PZX")]
	[InlineData("K7QM.4PZX")]
	[InlineData("K7QM4PZ\u00C9")]
	[InlineData("ftp://example.com/j/K7QM4PZX")]
	[InlineData("https://example.com/x/K7QM4PZX")]
	[InlineData("obm://join/K7QM4PZ")]
	[InlineData("obm://join/K7QM4PZX/extra")]
	public void RejectsInvalidInput(string? text)
	{
		Assert.False(SessionCode.TryParse(text, out _));
	}

	[Fact]
	public void RejectsOverlongInput()
	{
		Assert.False(SessionCode.TryParse(new string('-', 300) + "K7QM4PZX", out _));
	}

	[Fact]
	public void VerifierHasExpectedShape()
	{
		// 64 lowercase hex characters, deterministic, and bound to the room id
		var verifier = SessionCode.ComputeVerifier("K7Q", "M4PZX");
		Assert.Matches("^[0-9a-f]{64}$", verifier);
		Assert.True(SessionCode.IsValidVerifier(verifier));
		Assert.Equal(verifier, new SessionCode("K7Q", "M4PZX").Verifier);
		Assert.NotEqual(verifier, SessionCode.ComputeVerifier("K7R", "M4PZX"));
		Assert.NotEqual(verifier, SessionCode.ComputeVerifier("K7Q", "M4PZY"));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("abc")]
	public void RejectsMalformedVerifiers(string? verifier)
	{
		Assert.False(SessionCode.IsValidVerifier(verifier));
	}

	[Fact]
	public void RejectsVerifierCaseAndLengthChanges()
	{
		var verifier = SessionCode.ComputeVerifier("K7Q", "M4PZX");
		Assert.False(SessionCode.IsValidVerifier(verifier[..63]));
		Assert.False(SessionCode.IsValidVerifier(verifier + "0"));
		Assert.False(SessionCode.IsValidVerifier("A" + verifier[1..].Replace('a', 'A')));
	}

	[Fact]
	public void GeneratesValidParts()
	{
		for (var i = 0; i != 100; ++i)
		{
			Assert.True(SessionCode.IsValidRoom(SessionCode.NewRoom()));
			Assert.True(SessionCode.IsValidSecret(SessionCode.NewSecret()));
		}
	}

	[Fact]
	public void ConstructorRejectsInvalidParts()
	{
		Assert.Throws<ArgumentException>(() => new SessionCode("k7q", "M4PZX"));
		Assert.Throws<ArgumentException>(() => new SessionCode("K7Q", "M4PZ"));
	}
}
