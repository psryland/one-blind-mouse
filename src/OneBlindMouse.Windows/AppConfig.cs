using System.Diagnostics;
using System.Security.Principal;

namespace OneBlindMouse.Windows;

/// <summary>
/// Fixed configuration. The relay and web addresses are compiled in and never taken from external input in release builds,
/// so a link or command line cannot point the app at another server.
/// </summary>
internal static class AppConfig
{
	/// <summary>Product name shown to users.</summary>
	public const string ProductName = "One-Blind-Mouse";

	/// <summary>The relay WebSocket endpoint.</summary>
	private const string DefaultRelayUrl = "wss://obm-relay.orangeglacier-22ba4601.australiaeast.azurecontainerapps.io/v1";

	/// <summary>The landing page that hosts download and join links.</summary>
	private const string DefaultWebUrl = "https://one-blind-mouse.rylogic.co.nz";

	/// <summary>The relay endpoint. Debug builds may override it with OBM_RELAY_URL for local testing.</summary>
	public static Uri RelayUri { get; } = new(DebugOverride("OBM_RELAY_URL") ?? DefaultRelayUrl);

	/// <summary>The landing page base URL. Debug builds may override it with OBM_WEB_URL.</summary>
	public static string WebUrl { get; } = (DebugOverride("OBM_WEB_URL") ?? DefaultWebUrl).TrimEnd('/');

	/// <summary>
	/// Optional profile name that isolates settings and the single-instance lock, so several instances can run side by side for testing.
	/// Set with '--profile NAME'. Empty for normal use. Only letters, digits, '-' and '_' are kept because it becomes part of a directory, mutex and pipe name.
	/// </summary>
	public static string Profile
	{
		get;
		set => field = new string([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32)]);
	} = string.Empty;

	/// <summary>Optional text shown in brackets before window titles (set with '--title-tag TEXT'), to tell test instances apart.</summary>
	public static string TitleTag { get; set; } = string.Empty;

	/// <summary>Directory for settings and logs.</summary>
	public static string DataDirectory
	{
		get
		{
			// Profiles get their own sub-directory
			var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneBlindMouse");
			return Profile.Length != 0 ? Path.Combine(root, "profiles", Profile) : root;
		}
	}

	/// <summary>A name unique to this user, logon session and profile. Used for the single-instance mutex and the local API pipe.</summary>
	public static string InstanceName
	{
		get
		{
			// Pipe names are machine-wide, so include the user SID and session id to keep users and sessions apart
			var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
			var session = Process.GetCurrentProcess().SessionId;
			return $"OneBlindMouse-{sid}-{session}{(Profile.Length != 0 ? "-" + Profile : string.Empty)}";
		}
	}

	/// <summary>A window title with the optional test tag.</summary>
	public static string Title(string title)
	{
		return TitleTag.Length != 0 ? $"[{TitleTag}] {title}" : title;
	}

	/// <summary>A join link for a session code.</summary>
	public static string JoinLink(Core.SessionCode code)
	{
		return $"{WebUrl}/j/{code.Compact}";
	}

	/// <summary>An environment variable value in debug builds; always null in release builds.</summary>
	private static string? DebugOverride(string name)
	{
#if DEBUG
		var value = Environment.GetEnvironmentVariable(name);
		return string.IsNullOrWhiteSpace(value) ? null : value;
#else
		_ = name;
		return null;
#endif
	}
}
