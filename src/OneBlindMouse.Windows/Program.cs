using System.Text.Json.Nodes;
using OneBlindMouse.Core;
using OneBlindMouse.Windows.Agent;
using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows;

/// <summary>
/// Entry point. Usage:
///   OneBlindMouse.exe [obm://join/CODE | --join CODE] [--profile NAME] [--title-tag TEXT]
///   OneBlindMouse.exe mcp [--join CODE] [--name NAME]
/// Only one tray app runs per user, logon session and profile; a second launch hands its join code to the first and exits.
/// </summary>
internal static class Program
{
	private static int s_crashed;

	[STAThread]
	private static int Main(string[] args)
	{
		// Anything unhandled is reported and the cursor released, rather than leaving the user's mouse confined
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception ?? new Exception("Unknown error"));

		// The MCP server is a console-style stdio process that never shows UI
		if (args.Length != 0 && args[0] == "mcp")
			return McpServer.RunAsync(args[1..]).GetAwaiter().GetResult();

		// Parse the tray app options
		SessionCode? join = null;
		for (var i = 0; i != args.Length; ++i)
		{
			switch (args[i])
			{
				case "--join" when i + 1 < args.Length:
				{
					join = SessionCode.TryParse(args[++i], out var code) ? code : null;
					break;
				}
				case "--profile" when i + 1 < args.Length:
				{
					AppConfig.Profile = args[++i];
					break;
				}
				case "--title-tag" when i + 1 < args.Length:
				{
					AppConfig.TitleTag = Names.Sanitise(args[++i], 40);
					break;
				}
				default:
				{
					// A bare argument is a link from the browser; anything that isn't a valid code is ignored
					if (SessionCode.TryParse(args[i], out var code))
						join = code;

					break;
				}
			}
		}

		// Hand over to the running instance if there is one
		bool restart;
		using (var mutex = new Mutex(true, $@"Local\{AppConfig.InstanceName}", out var created))
		{
			if (!created)
				return Forward(join);

			// Run the tray app, after tidying up any copy replaced by an earlier update
			Updater.DeleteOldVersions();
			using var app = new App(join);
			app.Run();
			restart = app.RestartRequested;
		}

		// An installed update starts a fresh instance once this one has let go of the single-instance lock
		if (restart)
			Relaunch();

		return 0;
	}

	/// <summary>Start the updated app with this instance's profile and title tag. A join code isn't passed on because the update starts a fresh session.</summary>
	private static void Relaunch()
	{
		// Only the options that identify this instance are kept
		var args = new List<string>();
		if (AppConfig.Profile.Length != 0)
			args.AddRange(["--profile", AppConfig.Profile]);

		if (AppConfig.TitleTag.Length != 0)
			args.AddRange(["--title-tag", AppConfig.TitleTag]);

		try
		{
			Updater.Relaunch(args);
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			User32.MessageBoxW(0, $"{AppConfig.ProductName} was updated but could not restart. Please start it again.\n\n{ex.Message}", AppConfig.Title(AppConfig.ProductName), User32.MB_OK | User32.MB_ICONWARNING);
		}
	}

	/// <summary>Send a join code (or just "show yourself") to the running instance.</summary>
	private static int Forward(SessionCode? join)
	{
		// Let the running instance bring its popup to the front
		User32.AllowSetForegroundWindow(User32.ASFW_ANY);
		var request = join is { } code
			? new JsonObject { ["cmd"] = "join", ["code"] = code.Compact }
			: new JsonObject { ["cmd"] = "activate" };
		try
		{
			LocalApi.CallAsync(request, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
			return 0;
		}
		catch (Exception ex) when (ex is AgentError or IOException or TimeoutException or OperationCanceledException)
		{
			User32.MessageBoxW(0, $"{AppConfig.ProductName} is already running but did not respond.\n\n{ex.Message}", AppConfig.Title(AppConfig.ProductName), User32.MB_OK | User32.MB_ICONWARNING);
			return 1;
		}
	}

	/// <summary>Report a fatal error and exit. Safe to call from any thread, including native callbacks.</summary>
	public static void Crash(Exception ex)
	{
		// Only the first crash is reported; the cursor is released first in case pointing had confined it
		if (Interlocked.Exchange(ref s_crashed, 1) != 0)
			return;

		unsafe
		{
			User32.ClipCursor(null);
		}

		// Write a log for bug reports; failure to write it must not hide the original error
		var log = Path.Combine(AppConfig.DataDirectory, "crash.log");
		try
		{
			Directory.CreateDirectory(AppConfig.DataDirectory);
			File.AppendAllText(log, $"[{DateTime.Now:u}] {ex}\n\n");
		}
		catch (Exception log_ex) when (log_ex is IOException or UnauthorizedAccessException)
		{
		}

		User32.MessageBoxW(0, $"{AppConfig.ProductName} has stopped because of an error.\n\n{ex.Message}\n\nDetails were written to {log}", AppConfig.Title(AppConfig.ProductName), User32.MB_OK | User32.MB_ICONERROR);
		Environment.Exit(1);
	}
}
