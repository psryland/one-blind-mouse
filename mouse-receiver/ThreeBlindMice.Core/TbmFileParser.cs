using System.Text.Json;

namespace ThreeBlindMice.Core;

/// <summary>
/// Reads session codes from .tbm config files.
/// The file format is a simple JSON object: { "session_code": "abc123" }
/// </summary>
public static class TbmFileParser
{
	public static string? ReadSessionCode(string file_path)
	{
		try
		{
			var json = File.ReadAllText(file_path);
			using var doc = JsonDocument.Parse(json);

			if (doc.RootElement.TryGetProperty("session_code", out var code_element))
			{
				var code = code_element.GetString();
				if (code != null && TbmUriParser.IsValidSessionCode(code))
					return code;
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Failed to read .tbm file: {ex.Message}");
		}

		return null;
	}
}
