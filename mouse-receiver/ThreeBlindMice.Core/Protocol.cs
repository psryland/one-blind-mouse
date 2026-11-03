using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreeBlindMice.Core;

// Base message type
public abstract class Message
{
	[JsonPropertyName("type")]
	public abstract string Type { get; }
}

public class CursorMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "cursor";

	[JsonPropertyName("user_id")]
	public string User_Id { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("colour")]
	public string Colour { get; set; } = "";

	[JsonPropertyName("x")]
	public double X { get; set; }

	[JsonPropertyName("y")]
	public double Y { get; set; }

	[JsonPropertyName("button")]
	public int Button { get; set; }
}

public class JoinMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "join";

	[JsonPropertyName("user_id")]
	public string User_Id { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("colour")]
	public string Colour { get; set; } = "";
}

public class LeaveMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "leave";

	[JsonPropertyName("user_id")]
	public string User_Id { get; set; } = "";
}

public class HostConfigMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_config";

	[JsonPropertyName("aspect_ratio")]
	public double Aspect_Ratio { get; set; }

	[JsonPropertyName("monitor_name")]
	public string Monitor_Name { get; set; } = "";
}

public class MonitorData
{
	[JsonPropertyName("index")]
	public int Index { get; set; }

	[JsonPropertyName("device")]
	public string Device { get; set; } = "";

	[JsonPropertyName("left")]
	public int Left { get; set; }

	[JsonPropertyName("top")]
	public int Top { get; set; }

	[JsonPropertyName("width")]
	public int Width { get; set; }

	[JsonPropertyName("height")]
	public int Height { get; set; }

	[JsonPropertyName("is_primary")]
	public bool Is_Primary { get; set; }
}

public class WindowData
{
	[JsonPropertyName("title")]
	public string Title { get; set; } = "";

	[JsonPropertyName("left")]
	public int Left { get; set; }

	[JsonPropertyName("top")]
	public int Top { get; set; }

	[JsonPropertyName("width")]
	public int Width { get; set; }

	[JsonPropertyName("height")]
	public int Height { get; set; }

	[JsonPropertyName("monitor_index")]
	public int Monitor_Index { get; set; }
}

public class HostInfoMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_info";

	[JsonPropertyName("monitors")]
	public List<MonitorData> Monitors { get; set; } = new();

	[JsonPropertyName("windows")]
	public List<WindowData> Windows { get; set; } = new();
}

public class HostThumbnailMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_thumbnail";

	[JsonPropertyName("monitor_index")]
	public int Monitor_Index { get; set; }

	[JsonPropertyName("data_url")]
	public string Data_Url { get; set; } = "";
}

// Web → Host: request the host to enter window-pick mode
public class HostPickWindowMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_pick_window";
}

// Web → Host: tell the host which region to constrain the overlay to
public class HostConstraintMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_constraint";

	[JsonPropertyName("mode")]
	public string Mode { get; set; } = "";

	[JsonPropertyName("monitor_index")]
	public int? Monitor_Index { get; set; }

	[JsonPropertyName("window_title")]
	public string? Window_Title { get; set; }

	[JsonPropertyName("left")]
	public int? Left { get; set; }

	[JsonPropertyName("top")]
	public int? Top { get; set; }

	[JsonPropertyName("width")]
	public int? Width { get; set; }

	[JsonPropertyName("height")]
	public int? Height { get; set; }
}

// Host → Web: result of a window pick
public class HostWindowPickedMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_window_picked";

	[JsonPropertyName("title")]
	public string Title { get; set; } = "";

	[JsonPropertyName("left")]
	public int Left { get; set; }

	[JsonPropertyName("top")]
	public int Top { get; set; }

	[JsonPropertyName("width")]
	public int Width { get; set; }

	[JsonPropertyName("height")]
	public int Height { get; set; }

	[JsonPropertyName("monitor_index")]
	public int Monitor_Index { get; set; }
}

// Web → Host: request the host to enter rectangle-select mode
public class HostPickRectangleMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_pick_rectangle";
}

// Web → Host: periodic heartbeat so the host app knows the session is still alive
public class HostHeartbeatMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_heartbeat";
}

// Web → Host: the host user has ended the session
public class HostEndMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_end";
}

// Host → Web: result of a rectangle selection
public class HostRectanglePickedMessage : Message
{
	[JsonPropertyName("type")]
	public override string Type => "host_rectangle_picked";

	[JsonPropertyName("left")]
	public int Left { get; set; }

	[JsonPropertyName("top")]
	public int Top { get; set; }

	[JsonPropertyName("width")]
	public int Width { get; set; }

	[JsonPropertyName("height")]
	public int Height { get; set; }
}

public static class MessageParser
{
	public static Message? Parse(string json)
	{
		// Parse to get the type field first
		using var doc = JsonDocument.Parse(json);
		if (!doc.RootElement.TryGetProperty("type", out var type_element))
			return null;

		var type = type_element.GetString();
		return type switch
		{
			"cursor" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.CursorMessage),
			"join" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.JoinMessage),
			"leave" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.LeaveMessage),
			"host_config" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.HostConfigMessage),
			"host_info" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.HostInfoMessage),
			"host_thumbnail" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.HostThumbnailMessage),
			"host_pick_window" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.HostPickWindowMessage),
			"host_pick_rectangle" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.HostPickRectangleMessage),
			"host_constraint" => JsonSerializer.Deserialize(json, TbmJsonContext.Default.HostConstraintMessage),
			"host_heartbeat" => new HostHeartbeatMessage(),
			"host_end" => new HostEndMessage(),
			_ => null,
		};
	}

	public static string Serialize(Message message)
	{
		return message switch
		{
			CursorMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.CursorMessage),
			JoinMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.JoinMessage),
			LeaveMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.LeaveMessage),
			HostConfigMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostConfigMessage),
			HostInfoMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostInfoMessage),
			HostThumbnailMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostThumbnailMessage),
			HostWindowPickedMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostWindowPickedMessage),
			HostRectanglePickedMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostRectanglePickedMessage),
			HostConstraintMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostConstraintMessage),
			HostHeartbeatMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostHeartbeatMessage),
			HostEndMessage m => JsonSerializer.Serialize(m, TbmJsonContext.Default.HostEndMessage),
			_ => JsonSerializer.Serialize(message, TbmJsonContext.Default.Message),
		};
	}
}

// Source-generated JSON context for NativeAOT compatibility
[JsonSerializable(typeof(CursorMessage))]
[JsonSerializable(typeof(JoinMessage))]
[JsonSerializable(typeof(LeaveMessage))]
[JsonSerializable(typeof(HostConfigMessage))]
[JsonSerializable(typeof(MonitorData))]
[JsonSerializable(typeof(WindowData))]
[JsonSerializable(typeof(HostInfoMessage))]
[JsonSerializable(typeof(HostThumbnailMessage))]
[JsonSerializable(typeof(HostPickWindowMessage))]
[JsonSerializable(typeof(HostWindowPickedMessage))]
[JsonSerializable(typeof(HostPickRectangleMessage))]
[JsonSerializable(typeof(HostRectanglePickedMessage))]
[JsonSerializable(typeof(HostConstraintMessage))]
[JsonSerializable(typeof(HostHeartbeatMessage))]
[JsonSerializable(typeof(HostEndMessage))]
[JsonSerializable(typeof(Message))]
[JsonSourceGenerationOptions(
	PropertyNameCaseInsensitive = true,
	NumberHandling = JsonNumberHandling.Strict,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
internal partial class TbmJsonContext : JsonSerializerContext
{
}
