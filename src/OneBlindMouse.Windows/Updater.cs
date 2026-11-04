using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using OneBlindMouse.Core;
using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows;

/// <summary>An expected failure while checking for or installing an update. The message is suitable for the user.</summary>
internal sealed class UpdateError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finds, downloads and installs new releases from GitHub.
/// Installing replaces the app's exe in place. The running exe is renamed to '*.old', which Windows allows for a running file.
/// The verified download then takes its name, and the caller restarts from the same path.
/// Renamed copies are deleted at the next start. Copies still running from them (e.g. MCP servers) keep the old version until they restart.
/// Check and install block on the network, so call them off the UI thread.
/// </summary>
internal static class Updater
{
	/// <summary>The name of the release asset that holds the app.</summary>
	private const string AssetName = "OneBlindMouse.exe";

	/// <summary>No release is anywhere near this size, so a larger download is rejected rather than filling the disk.</summary>
	private const long MaxDownloadBytes = 64L << 20;

	/// <summary>
	/// The app's path, read once at start-up. After an install renames the running file, Windows may report the renamed path.
	/// Reading it early keeps the original name, which is where the new version is installed and restarted from.
	/// </summary>
	private static readonly string? s_exe = Environment.ProcessPath;

	/// <summary>Return the newer release, or null when this version is the latest.</summary>
	public static async Task<Release?> CheckAsync()
	{
		// Network and format problems are reported the same way, because the user can only retry
		try
		{
			using var http = CreateClient(TimeSpan.FromSeconds(20));
			http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			var json = await http.GetStringAsync(AppConfig.LatestReleaseApi).ConfigureAwait(false);
			return Releases.ParseLatest(json, AppConfig.Version, AssetName, AppConfig.ReleaseDownloadPrefix);
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
		{
			throw new UpdateError($"Could not check for updates. {ex.Message}", ex);
		}
	}

	/// <summary>Download 'release', check that Rylogic signed it, and put it in place of the running exe. The caller must then restart.</summary>
	public static async Task InstallAsync(Release release)
	{
		// The download goes next to the exe, so the final rename stays on one volume and can't half-complete
		var exe = s_exe ?? throw new UpdateError("The app's location is unknown.");
		var download = exe + ".download";
		try
		{
			await DownloadAsync(release.DownloadUrl, download).ConfigureAwait(false);
			Verify(download);
			Swap(exe, download);
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
		{
			throw new UpdateError($"Could not install version {release.Version}. {ex.Message}", ex);
		}
		finally
		{
			// A rejected or partial download is removed; after a successful swap there is nothing left to remove
			TryDelete(download);
		}
	}

	/// <summary>Delete copies left by earlier updates. Copies that are still running are skipped.</summary>
	public static void DeleteOldVersions()
	{
		// Nothing to do if the location is unknown or the folder can't be read
		if (s_exe is null)
			return;

		try
		{
			foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(s_exe)!, Path.GetFileName(s_exe) + ".*.old"))
				TryDelete(old);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Start the (newly installed) app with 'args'. Throws Win32Exception if it can't be started.</summary>
	public static void Relaunch(IEnumerable<string> args)
	{
		// Arguments are passed as a list so they are quoted correctly
		var psi = new ProcessStartInfo(s_exe ?? throw new Win32Exception("The app's location is unknown.")) { UseShellExecute = false };
		foreach (var arg in args)
			psi.ArgumentList.Add(arg);

		Process.Start(psi)?.Dispose();
	}

	/// <summary>Open the latest release's web page, for a manual download.</summary>
	public static void OpenReleasesPage()
	{
		// A missing browser is not worth an error; the user can find the page themselves
		try
		{
			Process.Start(new ProcessStartInfo(AppConfig.ReleasesPage) { UseShellExecute = true })?.Dispose();
		}
		catch (Win32Exception)
		{
		}
	}

	/// <summary>An HTTP client that identifies the app, as the GitHub API requires.</summary>
	private static HttpClient CreateClient(TimeSpan timeout)
	{
		// Redirects are followed, but never from HTTPS to HTTP
		var http = new HttpClient { Timeout = timeout };
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneBlindMouse", AppConfig.Version.ToString()));
		return http;
	}

	/// <summary>Download 'url' to 'path', replacing any earlier partial download.</summary>
	private static async Task DownloadAsync(Uri url, string path)
	{
		// Headers first, so an oversized download is rejected before it starts
		using var http = CreateClient(TimeSpan.FromMinutes(5));
		using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength > MaxDownloadBytes)
			throw new UpdateError("The download is unexpectedly large.");

		// Copy with a running total, because the length header is optional
		await using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		var buffer = new byte[81920];
		var total = 0L;
		for (int n; (n = await src.ReadAsync(buffer).ConfigureAwait(false)) != 0;)
		{
			total += n;
			if (total > MaxDownloadBytes)
				throw new UpdateError("The download is unexpectedly large.");

			await dst.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
		}
	}

	/// <summary>Throw unless 'path' has a valid Authenticode signature from <see cref="AppConfig.Signer"/>.</summary>
	private static unsafe void Verify(string path)
	{
		// Windows checks the signature, the file's hash and the certificate chain (a timestamp keeps short-lived signing certificates valid)
		int result;
		string? signer = null;
		fixed (char* file_path = path)
		{
			var file = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = (nint)file_path };
			var data = new WINTRUST_DATA
			{
				cbStruct = (uint)sizeof(WINTRUST_DATA),
				dwUIChoice = WinTrust.WTD_UI_NONE,
				fdwRevocationChecks = WinTrust.WTD_REVOKE_NONE,
				dwUnionChoice = WinTrust.WTD_CHOICE_FILE,
				pFile = (nint)(&file),
				dwStateAction = WinTrust.WTD_STATEACTION_VERIFY,
			};
			var action = WinTrust.WINTRUST_ACTION_GENERIC_VERIFY_V2;
			result = WinTrust.WinVerifyTrust(0, &action, &data);

			// The signer's name comes from the signature that was just verified, so it can't differ from what Windows checked
			if (result == 0)
				signer = SignerName(data.hWVTStateData);

			// The verify step holds state that must always be released
			data.dwStateAction = WinTrust.WTD_STATEACTION_CLOSE;
			WinTrust.WinVerifyTrust(0, &action, &data);
		}
		if (result != 0)
			throw new UpdateError($"The download is not validly signed (0x{result:X8}), so it was not installed.");

		// A valid signature from anyone else is still rejected
		if (signer != AppConfig.Signer)
			throw new UpdateError($"The download is signed by '{signer}', not '{AppConfig.Signer}', so it was not installed.");
	}

	/// <summary>The display name (common name) of the primary signer's certificate in verified WinVerifyTrust state, or null if unavailable.</summary>
	private static unsafe string? SignerName(nint state)
	{
		// The first certificate in the primary signer's chain is the signing certificate itself
		var sgnr = WinTrust.WTHelperGetProvSignerFromChain(WinTrust.WTHelperProvDataFromStateData(state), 0, false, 0);
		if (sgnr is null || sgnr->csCertChain == 0 || sgnr->pasCertChain is null || sgnr->pasCertChain[0].pCert == 0)
			return null;

		// Names are short; the returned length includes the terminator
		var cert = sgnr->pasCertChain[0].pCert;
		var name = stackalloc char[256];
		var len = Crypt32.CertGetNameStringW(cert, Crypt32.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, 0, name, 256);
		return len > 1 ? new string(name, 0, (int)len - 1) : null;
	}
	/// <summary>Replace 'exe' with 'download'. On failure the original exe is put back.</summary>
	private static void Swap(string exe, string download)
	{
		// The process id keeps the name unique while copies from earlier updates are still running
		var old = $"{exe}.{Environment.ProcessId}.old";
		File.Move(exe, old, overwrite: true);
		try
		{
			File.Move(download, exe);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			File.Move(old, exe);
			throw;
		}
	}

	/// <summary>Delete a file if possible; a file that is in use or protected is left alone.</summary>
	private static void TryDelete(string path)
	{
		// Leftover files are harmless, so failures are ignored
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
