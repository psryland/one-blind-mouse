using OneBlindMouse.Core;
using Xunit;

namespace OneBlindMouse.Tests;

/// <summary>Tests for reading the latest release from the GitHub API.</summary>
public class ReleasesTests
{
	private const string Prefix = "https://github.com/psryland/one-blind-mouse/releases/download/";

	/// <summary>A minimal 'releases/latest' response.</summary>
	private static string Json(string tag, string asset = "OneBlindMouse.exe", string? url = null)
	{
		url ??= $"{Prefix}{tag}/{asset}";
		return $$"""{ "tag_name": "{{tag}}", "assets": [ { "name": "notes.txt", "browser_download_url": "{{Prefix}}{{tag}}/notes.txt" }, { "name": "{{asset}}", "browser_download_url": "{{url}}" } ] }""";
	}

	[Fact]
	public void NewerReleaseIsReturned()
	{
		var release = Releases.ParseLatest(Json("v1.3.0"), new Version(1, 2, 1, 0), "OneBlindMouse.exe", Prefix);
		Assert.NotNull(release);
		Assert.Equal(new Version(1, 3, 0), release.Version);
		Assert.Equal($"{Prefix}v1.3.0/OneBlindMouse.exe", release.DownloadUrl.ToString());
	}

	[Theory]
	[InlineData("v1.2.1")]
	[InlineData("v1.2.0")]
	[InlineData("v0.9.9")]
	public void SameOrOlderReleaseIsIgnored(string tag)
	{
		Assert.Null(Releases.ParseLatest(Json(tag), new Version(1, 2, 1, 0), "OneBlindMouse.exe", Prefix));
	}

	[Theory]
	[InlineData("1.3.0")]
	[InlineData("v1.3")]
	[InlineData("v1.3.0.1")]
	[InlineData("v1.3.0-beta")]
	[InlineData("V1.3.0")]
	[InlineData("vx.y.z")]
	public void NonVersionTagsAreRejected(string tag)
	{
		Assert.Null(Releases.ParseTag(tag));
		Assert.Throws<FormatException>(() => Releases.ParseLatest(Json(tag), new Version(1, 2, 1), "OneBlindMouse.exe", Prefix));
	}

	[Theory]
	[InlineData("http://github.com/psryland/one-blind-mouse/releases/download/v1.3.0/OneBlindMouse.exe")]
	[InlineData("https://example.com/psryland/one-blind-mouse/releases/download/v1.3.0/OneBlindMouse.exe")]
	[InlineData("https://github.com/someone-else/one-blind-mouse/releases/download/v1.3.0/OneBlindMouse.exe")]
	public void UntrustedDownloadAddressIsRejected(string url)
	{
		Assert.Throws<FormatException>(() => Releases.ParseLatest(Json("v1.3.0", url: url), new Version(1, 2, 1), "OneBlindMouse.exe", Prefix));
	}

	[Fact]
	public void MissingAssetOrBadJsonIsRejected()
	{
		Assert.Throws<FormatException>(() => Releases.ParseLatest(Json("v1.3.0", asset: "Other.exe"), new Version(1, 2, 1), "OneBlindMouse.exe", Prefix));
		Assert.Throws<FormatException>(() => Releases.ParseLatest("not json", new Version(1, 2, 1), "OneBlindMouse.exe", Prefix));
		Assert.Throws<FormatException>(() => Releases.ParseLatest("{}", new Version(1, 2, 1), "OneBlindMouse.exe", Prefix));
	}
}
