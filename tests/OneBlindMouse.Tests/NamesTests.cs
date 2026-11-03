using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Tests for display name sanitising.</summary>
public class NamesTests
{
	[Theory]
	[InlineData("Alice", "Alice")]
	[InlineData("A\u0000B\u0007C\u001B", "ABC")]
	[InlineData("abc\u202Edef", "abcdef")]
	[InlineData("\u2066x\u2069\u200Dy\u200B", "xy")]
	[InlineData("  a \t\n  b  ", "a b")]
	[InlineData("a\u2028b\u2029c", "a b c")]
	[InlineData("\uE000priv", "priv")]
	[InlineData("", "")]
	[InlineData(" \t ", "")]
	public void RemovesInvisibleCharactersAndCollapsesSpace(string input, string expected)
	{
		Assert.Equal(expected, Names.Sanitise(input));
	}

	[Fact]
	public void LimitsLength()
	{
		Assert.Equal(new string('x', Protocol.MaxNameLength), Names.Sanitise(new string('x', 40)));
		Assert.Equal("abc", Names.Sanitise("abcdef", 3));
	}

	[Fact]
	public void NeverSplitsSurrogatePairs()
	{
		// Each emoji is two UTF-16 code units, so an odd prefix leaves room for only 11 more within 24 units
		var emoji = "\U0001F600";
		Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 12)), Names.Sanitise(string.Concat(Enumerable.Repeat(emoji, 20))));
		var result = Names.Sanitise("x" + string.Concat(Enumerable.Repeat(emoji, 20)));
		Assert.Equal("x" + string.Concat(Enumerable.Repeat(emoji, 11)), result);
		Assert.Equal(23, result.Length);
	}

	[Fact]
	public void ReplacesLoneSurrogates()
	{
		// Invalid UTF-16 decodes as the visible replacement character rather than passing an unpaired surrogate through
		Assert.Equal("a\uFFFDb", Names.Sanitise("a\uD800b"));
	}

	[Fact]
	public void DoesNotEndWithASpaceAtTheLimit()
	{
		var input = new string('a', 23) + " b";
		Assert.Equal(new string('a', 23), Names.Sanitise(input));
	}

	[Fact]
	public void FallsBackWhenNothingVisibleRemains()
	{
		Assert.Equal("Anon", Names.SanitiseOr("\u202E\u0000 ", "Anon"));
		Assert.Equal("Anon", Names.SanitiseOr(null, "Anon"));
		Assert.Equal("Bob", Names.SanitiseOr(" Bob ", "Anon"));
	}

	[Fact]
	public void SanitisedNamesAreValid()
	{
		Assert.True(Protocol.IsValidName(Names.Sanitise(" x\u202E y ")));
		Assert.False(Protocol.IsValidName("x\u202E"));
	}
}
