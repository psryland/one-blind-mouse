using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneBlindMouse.Core;

/// <summary>A published release that can replace the running app.</summary>
/// <param name="Version">The release version, from a 'vMAJOR.MINOR.PATCH' tag.</param>
/// <param name="DownloadUrl">The HTTPS address of the release's app file.</param>
public sealed record Release(Version Version, Uri DownloadUrl);

/// <summary>Reads release information from the GitHub releases API.</summary>
public static class Releases
{
	/// <summary>
	/// Read a 'releases/latest' API response. Returns the release only if it is newer than 'current' and has an asset called 'asset_name'
	/// whose download address starts with 'download_prefix'. Returns null when the release is not newer. Throws FormatException for a malformed response.
	/// </summary>
	public static Release? ParseLatest(string json, Version current, string asset_name, string download_prefix)
	{
		// Malformed JSON is reported the same way as a response with missing fields
		JsonNode? root;
		try
		{
			root = JsonNode.Parse(json);
		}
		catch (JsonException ex)
		{
			throw new FormatException("The release information is not valid JSON.", ex);
		}

		// Only plain 'vX.Y.Z' tags count as releases; the comparison ignores build and revision parts
		var tag = (root?["tag_name"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : throw new FormatException("The release has no tag.");
		var version = ParseTag(tag) ?? throw new FormatException($"The release tag '{tag}' is not a version.");
		if (version <= Normalise(current))
			return null;

		// The download must be the app itself and must come from the expected place, so a tampered response can't redirect it elsewhere
		foreach (var asset in root!["assets"] as JsonArray ?? [])
		{
			var name = (asset?["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : null;
			if (name != asset_name)
				continue;

			var url = (asset!["browser_download_url"] as JsonValue)?.TryGetValue<string>(out var u) == true ? u : null;
			if (url is null || !url.StartsWith(download_prefix, StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
				throw new FormatException($"The release download address '{url}' is not trusted.");

			return new Release(version, uri);
		}
		throw new FormatException($"The release {tag} has no '{asset_name}' download.");
	}

	/// <summary>Parse a 'vMAJOR.MINOR.PATCH' tag. Returns null for anything else.</summary>
	public static Version? ParseTag(string tag)
	{
		// Exactly three numeric parts after a lowercase 'v'
		if (tag.Length < 2 || tag[0] != 'v' || tag.Count(c => c == '.') != 2)
			return null;

		return Version.TryParse(tag.AsSpan(1), out var version) && !tag.Contains('+') && !tag.Contains('-') ? version : null;
	}

	/// <summary>A version reduced to MAJOR.MINOR.PATCH, so assembly versions (which have a revision) compare equal to tags.</summary>
	public static Version Normalise(Version version)
	{
		return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
	}
}
