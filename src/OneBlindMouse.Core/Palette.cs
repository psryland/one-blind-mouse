namespace OneBlindMouse.Core;

/// <summary>The fixed set of pointer colours. Each member of a session gets a distinct slot.</summary>
public static class Palette
{
	/// <summary>Number of colour slots.</summary>
	public const int Count = 8;

	/// <summary>Colours as 0xRRGGBB, chosen to stay distinct from each other on typical desktop content.</summary>
	private static readonly uint[] m_colours =
	[
		0xE6194B, // red
		0x3CB44B, // green
		0x4363D8, // blue
		0xF58231, // orange
		0x911EB4, // purple
		0x42D4F4, // cyan
		0xF032E6, // magenta
		0xBFEF45, // lime
	];

	/// <summary>Display names of the colours, matching <see cref="m_colours"/>.</summary>
	private static readonly string[] m_names = ["Red", "Green", "Blue", "Orange", "Purple", "Cyan", "Magenta", "Lime"];

	/// <summary>True if <paramref name="slot"/> is a valid colour slot.</summary>
	public static bool IsValidSlot(int slot)
	{
		return slot >= 0 && slot < Count;
	}

	/// <summary>The colour of <paramref name="slot"/> as 0xRRGGBB.</summary>
	public static uint Rgb(int slot)
	{
		return m_colours[slot];
	}

	/// <summary>The display name of the colour in <paramref name="slot"/>.</summary>
	public static string Name(int slot)
	{
		return m_names[slot];
	}
}
