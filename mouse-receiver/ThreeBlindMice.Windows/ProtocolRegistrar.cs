using Microsoft.Win32;
using ThreeBlindMice.Core;

namespace ThreeBlindMice.Windows;

/// <summary>
/// Registers the tbm:// custom URI protocol and .tbm file association
/// in the current user's registry (no elevation required).
/// </summary>
internal static class ProtocolRegistrar
{
	private const string ProtocolKey = @"Software\Classes\tbm";
	private const string FileExtKey = @"Software\Classes\.tbm";
	private const string FileTypeKey = @"Software\Classes\ThreeBlindMice.Session";

	/// <summary>
	/// Returns true if both the tbm:// protocol and .tbm file association are registered.
	/// </summary>
	public static bool Is_Registered()
	{
		using var protocol_key = Registry.CurrentUser.OpenSubKey(ProtocolKey);
		using var file_key = Registry.CurrentUser.OpenSubKey(FileExtKey);
		return protocol_key?.GetValue("URL Protocol") != null && file_key != null;
	}

	/// <summary>
	/// Registers the tbm:// protocol handler and .tbm file association
	/// pointing to the current executable. Writes to HKCU so no elevation is required.
	/// Uses the "clean" exe name (without any session code in the filename).
	/// </summary>
	public static void Register()
	{
		var exe_path = Environment.ProcessPath
			?? throw new InvalidOperationException("Cannot determine executable path.");

		// Derive the clean path: strip any session code from the filename
		// e.g., ThreeBlindMice.Windows.abc123.exe → ThreeBlindMice.Windows.exe
		var dir = Path.GetDirectoryName(exe_path)!;
		var filename = Path.GetFileNameWithoutExtension(exe_path);
		var parts = filename.Split('.');
		if (parts.Length >= 3 && TbmUriParser.IsValidSessionCode(parts[^1]))
		{
			var clean_name = string.Join('.', parts[..^1]) + Path.GetExtension(exe_path);
			exe_path = Path.Combine(dir, clean_name);
		}

		// tbm:// protocol handler
		using (var key = Registry.CurrentUser.CreateSubKey(ProtocolKey))
		{
			key.SetValue("", "URL:TBM Protocol");
			key.SetValue("URL Protocol", "");

			using var command_key = key.CreateSubKey(@"shell\open\command");
			command_key.SetValue("", $"\"{exe_path}\" \"%1\"");
		}

		// .tbm file type registration
		using (var key = Registry.CurrentUser.CreateSubKey(FileTypeKey))
		{
			key.SetValue("", "Three Blind Mice Session File");

			using var command_key = key.CreateSubKey(@"shell\open\command");
			command_key.SetValue("", $"\"{exe_path}\" \"%1\"");
		}

		// .tbm file extension → file type mapping
		using (var key = Registry.CurrentUser.CreateSubKey(FileExtKey))
		{
			key.SetValue("", "ThreeBlindMice.Session");
		}
	}
}
