using Microsoft.Win32;

namespace OneBlindMouse.Windows;

/// <summary>
/// Optional per-user registration of the obm:// scheme, so join links from the web page open the app directly.
/// Registration is under HKCU (no admin rights) and must be enabled by the user, because the app is portable and shouldn't write to the registry unasked.
/// </summary>
internal static class ProtocolHandler
{
	private const string Scheme = "obm";
	private const string KeyPath = @"Software\Classes\" + Scheme;

	/// <summary>The command line Windows runs for an obm:// link.</summary>
	private static string Command => $"\"{Environment.ProcessPath}\" \"%1\"";

	/// <summary>True if obm:// is registered (to any copy of the app).</summary>
	public static bool IsRegistered
	{
		get
		{
			using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
			return key?.GetValue("URL Protocol") is not null;
		}
	}

	/// <summary>Register obm:// to run this exe.</summary>
	public static void Register()
	{
		// The scheme key marks itself as a URL protocol; the shell command runs this exe with the link as its only argument
		using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
		key.SetValue(string.Empty, $"URL:{AppConfig.ProductName}");
		key.SetValue("URL Protocol", string.Empty);
		using var icon = key.CreateSubKey("DefaultIcon");
		icon.SetValue(string.Empty, $"\"{Environment.ProcessPath}\",0");
		using var command = key.CreateSubKey(@"shell\open\command");
		command.SetValue(string.Empty, Command);
	}

	/// <summary>Remove the obm:// registration.</summary>
	public static void Unregister()
	{
		Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
	}

	/// <summary>Point an existing registration at this exe, since a portable app may have been moved since it was registered.</summary>
	public static void Refresh()
	{
		// Only touch the registry when the user has opted in and the path is stale
		try
		{
			if (!IsRegistered)
				return;

			using var command = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command");
			if (command?.GetValue(string.Empty) as string != Command)
				Register();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
		}
	}
}
