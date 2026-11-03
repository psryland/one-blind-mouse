using System.Text.Json;
using System.Text.Json.Serialization;
using OneBlindMouse.Core;

namespace OneBlindMouse.Windows;

/// <summary>User settings, stored as JSON in %APPDATA%\OneBlindMouse. Values are validated on load; invalid values fall back to defaults.</summary>
internal sealed class Settings
{
	/// <summary>Display name shown on this user's pointer.</summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>Preferred palette slot, or -1 for "any free colour".</summary>
	public int ColourPref { get; set; } = -1;

	/// <summary>Virtual key held to point at someone else's screen.</summary>
	public int HoldKey { get; set; } = Native.User32.VK_RCONTROL;

	/// <summary>Device name of the monitor shared as the pointing target, or null for the primary monitor.</summary>
	public string? TargetMonitor { get; set; }

	/// <summary>Size of this user's pointer on other screens, as a percentage.</summary>
	public int PointerSize { get; set; } = PointerFrame.DefaultSize;

	/// <summary>Whether remote pointers appear in screen captures (and therefore in this user's screen share).</summary>
	public bool ShowInCapture { get; set; } = true;

	/// <summary>Load settings from the profile directory, or defaults if missing or unreadable.</summary>
	public static Settings Load()
	{
		// A corrupt file is replaced by defaults rather than stopping the app
		Settings settings;
		try
		{
			var json = File.ReadAllBytes(FilePath);
			settings = JsonSerializer.Deserialize(json, SettingsJson.Default.Settings) ?? new Settings();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			settings = new Settings();
		}

		// Clamp everything into the valid domain
		settings.Name = Names.SanitiseOr(settings.Name, Names.SanitiseOr(Environment.UserName, "Anonymous"));
		if (!Palette.IsValidSlot(settings.ColourPref))
			settings.ColourPref = -1;

		if (settings.HoldKey is < 1 or > 254)
			settings.HoldKey = Native.User32.VK_RCONTROL;

		settings.PointerSize = Math.Clamp(settings.PointerSize, PointerFrame.MinSize, PointerFrame.MaxSize);
		return settings;
	}

	/// <summary>Save settings. Failures are ignored because settings are a convenience.</summary>
	public void Save()
	{
		// Write to a temporary file first so a crash cannot leave a half-written file
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			var tmp = FilePath + ".tmp";
			File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.Settings));
			File.Move(tmp, FilePath, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Path of the settings file.</summary>
	private static string FilePath => Path.Combine(AppConfig.DataDirectory, "settings.json");

	/// <summary>
	/// Keys offered as the hold-to-point key. Keys that do something on release (Alt opens menus, the Menu key opens context menus) are left out,
	/// because the hold key is passed through to the focused application.
	/// </summary>
	public static readonly (int Vk, string Name)[] HoldKeys =
	[
		(Native.User32.VK_RCONTROL, "Right Ctrl"),
		(Native.User32.VK_RSHIFT, "Right Shift"),
		(Native.User32.VK_SCROLL, "Scroll Lock"),
		(Native.User32.VK_PAUSE, "Pause"),
	];

	/// <summary>The display name of the hold key.</summary>
	[JsonIgnore]
	public string HoldKeyName
	{
		get
		{
			// Keys set by editing the file directly are shown by code
			foreach (var (vk, name) in HoldKeys)
			{
				if (vk == HoldKey)
					return name;
			}
			return $"key 0x{HoldKey:X2}";
		}
	}
}

/// <summary>Source-generated JSON for <see cref="Settings"/> (required for trimming and NativeAOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext
{
}
