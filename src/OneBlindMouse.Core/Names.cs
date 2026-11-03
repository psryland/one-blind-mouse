using System.Globalization;
using System.Text;

namespace OneBlindMouse.Core;

/// <summary>Helpers for user-visible names and labels.</summary>
public static class Names
{
	/// <summary>
	/// Remove control, format and other invisible characters, collapse runs of white space, trim, and limit the length.
	/// Names are drawn on other people's screens, so this stops text that could hide or spoof content (e.g. bidi overrides).
	/// </summary>
	public static string Sanitise(string text, int max_length = Protocol.MaxNameLength)
	{
		// Keep only visible characters, mapping any white space to a single space
		var sb = new StringBuilder(Math.Min(text.Length, max_length));
		var pending_space = false;
		foreach (var rune in text.EnumerateRunes())
		{
			// White space is collapsed and emitted lazily so leading and trailing spaces disappear
			if (Rune.IsWhiteSpace(rune))
			{
				pending_space = sb.Length != 0;
				continue;
			}

			// Drop invisible and structural categories
			switch (Rune.GetUnicodeCategory(rune))
			{
				case UnicodeCategory.Control:
				case UnicodeCategory.Format:
				case UnicodeCategory.Surrogate:
				case UnicodeCategory.PrivateUse:
				case UnicodeCategory.OtherNotAssigned:
				case UnicodeCategory.LineSeparator:
				case UnicodeCategory.ParagraphSeparator:
				{
					continue;
				}
			}

			// Stop before exceeding the limit; never split a surrogate pair
			var needed = rune.Utf16SequenceLength + (pending_space ? 1 : 0);
			if (sb.Length + needed > max_length)
				break;

			if (pending_space)
				sb.Append(' ');

			pending_space = false;
			sb.Append(rune.ToString());
		}
		return sb.ToString();
	}

	/// <summary>A sanitised name, or <paramref name="fallback"/> if nothing visible remains.</summary>
	public static string SanitiseOr(string? text, string fallback)
	{
		var name = Sanitise(text ?? string.Empty);
		return name.Length != 0 ? name : fallback;
	}
}
