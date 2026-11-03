using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using OneBlindMouse.Core;
using OneBlindMouse.Windows.Native;
using OneBlindMouse.Windows.Ui;

namespace OneBlindMouse.Windows.Agent;

/// <summary>
/// The local automation endpoint: a named pipe, restricted to the current user, that accepts one JSON request per line and answers one JSON response per line.
/// Request: {"id": any, "cmd": "point_at", ...arguments}. Response: {"id": same, "ok": true, "result": ...} or {"id": same, "ok": false, "error": "text"}.
/// Used by AI agents (directly or through the MCP bridge) and by a second launch of the app to hand over a join code.
/// </summary>
internal sealed class LocalApi : IAsyncDisposable
{
	/// <summary>Longest accepted request line, in characters.</summary>
	public const int MaxRequestLength = 64 * 1024;

	private readonly UiDispatcher m_ui;
	private readonly AgentCommands m_commands;
	private readonly CancellationTokenSource m_cts = new();
	private readonly Task m_listen;

	/// <summary>Start listening. Commands run on the UI thread.</summary>
	public LocalApi(UiDispatcher ui, AgentCommands commands)
	{
		m_ui = ui;
		m_commands = commands;
		m_listen = Task.Run(() => ListenAsync(m_cts.Token));
	}

	/// <summary>The pipe name for this user's instance.</summary>
	public static string PipeName => AppConfig.InstanceName;

	/// <summary>Stop listening and drop connected clients.</summary>
	public async ValueTask DisposeAsync()
	{
		m_cts.Cancel();
		try
		{
			await m_listen.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		m_cts.Dispose();
	}

	/// <summary>Accept clients until cancelled. Each client is served concurrently.</summary>
	private async Task ListenAsync(CancellationToken ct)
	{
		// A new server instance is created for each client so several agents can be connected at once
		while (!ct.IsCancellationRequested)
		{
			var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			try
			{
				await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
			}
			catch
			{
				await pipe.DisposeAsync().ConfigureAwait(false);
				throw;
			}
			_ = Task.Run(() => ServeAsync(pipe, ct), ct);
		}
	}

	/// <summary>Answer requests from one client until it disconnects.</summary>
	private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
	{
		// Client errors end that client only
		await using (pipe)
		{
			try
			{
				using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
				await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
				while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
				{
					// Blank lines are allowed as keep-alives
					if (line.Length == 0)
						continue;

					var response = await HandleAsync(line).ConfigureAwait(false);
					await writer.WriteLineAsync(response.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
				}
			}
			catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
			{
			}
		}
	}

	/// <summary>Parse and run one request, producing its response.</summary>
	private async Task<JsonObject> HandleAsync(string line)
	{
		// Malformed requests get an error response rather than closing the connection
		JsonNode? id = null;
		try
		{
			if (line.Length > MaxRequestLength)
				throw new AgentError("Request too long");

			if (JsonNode.Parse(line) is not JsonObject request)
				throw new AgentError("Request must be a JSON object");

			id = request["id"]?.DeepClone();
			var cmd = request["cmd"]?.GetValue<string>() ?? throw new AgentError("Missing 'cmd'");
			var result = await m_ui.InvokeAsync(() => m_commands.Execute(cmd, request)).ConfigureAwait(false);
			return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result };
		}
		catch (Exception ex) when (ex is AgentError or System.Text.Json.JsonException or InvalidOperationException or FormatException)
		{
			return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex.Message };
		}
	}

	/// <summary>Send one request to the running instance and return its result. Throws if the instance can't be reached or reports an error.</summary>
	public static async Task<JsonNode?> CallAsync(JsonObject request, TimeSpan timeout, CancellationToken ct = default)
	{
		// One connection per call keeps the client simple; calls are infrequent
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeout);
		await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
		using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
		await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
		await writer.WriteLineAsync(request.ToJsonString().AsMemory(), cts.Token).ConfigureAwait(false);
		var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) ?? throw new IOException("No response");

		// Unwrap the response envelope
		if (JsonNode.Parse(line) is not JsonObject response)
			throw new IOException("Malformed response");

		if (response["ok"]?.GetValue<bool>() != true)
			throw new AgentError(response["error"]?.GetValue<string>() ?? "Unknown error");

		return response["result"]?.DeepClone();
	}

	/// <summary>True if an instance is listening.</summary>
	public static async Task<bool> PingAsync(TimeSpan timeout)
	{
		try
		{
			await CallAsync(new JsonObject { ["cmd"] = "ping" }, timeout).ConfigureAwait(false);
			return true;
		}
		catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or AgentError)
		{
			return false;
		}
	}
}

/// <summary>An error caused by a bad agent request. The message is returned to the caller.</summary>
internal sealed class AgentError(string message) : Exception(message);

/// <summary>
/// Executes agent commands against the overlay and session. Coordinates are physical screen pixels (the same space as the Win32 cursor position),
/// which may be negative on multi-monitor setups. All inputs are validated here, because the pipe is an input boundary.
/// UI thread only.
/// </summary>
internal sealed class AgentCommands
{
	/// <summary>Default time an annotation stays visible, in seconds.</summary>
	private const double DefaultDuration = 5;

	/// <summary>Longest annotation time, in seconds.</summary>
	private const double MaxDuration = 600;

	/// <summary>Most points accepted by trace_path.</summary>
	private const int MaxPathPoints = 256;

	/// <summary>How far outside the virtual screen coordinates may be, in pixels (lets highlights hug the screen edge).</summary>
	private const int Margin = 64;

	private readonly Overlay m_overlay;
	private readonly RelayClient m_relay;
	private readonly Action m_activate;
	private readonly Action<SessionCode> m_join;

	/// <summary>Create the command set. 'activate' shows the app window; 'join' joins a session.</summary>
	public AgentCommands(Overlay overlay, RelayClient relay, Action activate, Action<SessionCode> join)
	{
		m_overlay = overlay;
		m_relay = relay;
		m_activate = activate;
		m_join = join;
	}

	/// <summary>Run 'cmd' with the arguments in 'args'. Returns the result, or throws <see cref="AgentError"/>.</summary>
	public JsonNode? Execute(string cmd, JsonObject args)
	{
		// Each command validates its own arguments
		switch (cmd)
		{
			case "ping":
			{
				return "pong";
			}
			case "point_at":
			{
				var (x, y) = Point(args, "x", "y");
				m_overlay.AgentPointAt(x, y, Label(args), Colour(args), Duration(args));
				return null;
			}
			case "show_click":
			{
				var (x, y) = Point(args, "x", "y");
				m_overlay.AgentClick(x, y, Colour(args));
				return null;
			}
			case "highlight_rect":
			{
				var (x, y) = Point(args, "x", "y");
				var w = Int(args, "width", 1, 32768);
				var h = Int(args, "height", 1, 32768);
				m_overlay.AgentHighlight(new RECT(x, y, x + w, y + h), Label(args), Colour(args), Duration(args));
				return null;
			}
			case "trace_path":
			{
				m_overlay.AgentPath(Points(args), Colour(args), Duration(args));
				return null;
			}
			case "clear":
			{
				m_overlay.ClearAgent();
				return null;
			}
			case "get_screen_layout":
			{
				return ScreenLayout();
			}
			case "get_session":
			{
				return Session();
			}
			case "activate":
			{
				m_activate();
				return null;
			}
			case "join":
			{
				var text = args["code"]?.GetValue<string>() ?? throw new AgentError("Missing 'code'");
				if (!SessionCode.TryParse(text, out var code))
					throw new AgentError("Invalid session code");

				m_join(code);
				return null;
			}
			default:
			{
				throw new AgentError($"Unknown command '{Names.Sanitise(cmd, 32)}'");
			}
		}
	}

	/// <summary>Monitors, cursor and foreground window, so an agent can relate screenshots to screen coordinates.</summary>
	private static JsonObject ScreenLayout()
	{
		// Monitor rectangles are in physical pixels; 'scale' is the Windows display scale for converting from logical sizes
		var vs = User32.VirtualScreen();
		var monitors = new JsonArray();
		foreach (var m in Monitors.All())
		{
			monitors.Add((JsonNode)new JsonObject
			{
				["device"] = m.Device,
				["primary"] = m.Primary,
				["bounds"] = Rect(m.Bounds),
				["work_area"] = Rect(m.WorkArea),
				["scale"] = m.Dpi / 96.0,
			});
		}
		User32.GetCursorPos(out var cursor);

		// The foreground window's client area is where an agent most often wants to point
		var fg = User32.GetForegroundWindow();
		JsonNode? foreground = null;
		if (fg != 0)
		{
			foreground = new JsonObject
			{
				["title"] = User32.GetWindowText(fg).Truncate(256),
				["client"] = Rect(User32.GetClientRectScreen(fg)),
			};
		}
		return new JsonObject
		{
			["virtual_screen"] = Rect(vs),
			["monitors"] = monitors,
			["cursor"] = new JsonObject { ["x"] = cursor.X, ["y"] = cursor.Y },
			["foreground_window"] = foreground,
		};
	}

	/// <summary>The session code and members.</summary>
	private JsonObject Session()
	{
		var snap = m_relay.Snapshot;
		var members = new JsonArray();
		foreach (var m in snap.Members)
		{
			members.Add((JsonNode)new JsonObject
			{
				["id"] = m.Id,
				["name"] = m.Name,
				["colour"] = Palette.Name(m.Slot),
				["is_me"] = m.Id == snap.Me,
				["is_owner"] = m.Id == snap.Owner,
				["sharing"] = m.Label,
			});
		}
		return new JsonObject
		{
			["connection"] = snap.Connection.ToString().ToLowerInvariant(),
			["code"] = snap.Code?.Display,
			["members"] = members,
		};
	}

	/// <summary>A rectangle as JSON.</summary>
	private static JsonObject Rect(RECT rc)
	{
		return new JsonObject { ["x"] = rc.Left, ["y"] = rc.Top, ["width"] = rc.Width, ["height"] = rc.Height };
	}

	/// <summary>A required screen position, which must be on (or just off) the virtual screen.</summary>
	private static (int X, int Y) Point(JsonObject args, string xname, string yname)
	{
		var vs = User32.VirtualScreen();
		var x = Int(args, xname, vs.Left - Margin, vs.Right + Margin);
		var y = Int(args, yname, vs.Top - Margin, vs.Bottom + Margin);
		return (x, y);
	}

	/// <summary>The 'points' array: [[x, y], ...] or [{"x":..,"y":..}, ...].</summary>
	private static List<POINT> Points(JsonObject args)
	{
		// Bounded in count and range so a request can't make the overlay allocate a huge window
		if (args["points"] is not JsonArray arr || arr.Count < 2 || arr.Count > MaxPathPoints)
			throw new AgentError($"'points' must be an array of 2 to {MaxPathPoints} points");

		var vs = User32.VirtualScreen();
		var result = new List<POINT>(arr.Count);
		foreach (var item in arr)
		{
			var (x, y) = item switch
			{
				JsonArray { Count: 2 } pair => (Number(pair[0], "points"), Number(pair[1], "points")),
				JsonObject obj => (Number(obj["x"], "points"), Number(obj["y"], "points")),
				_ => throw new AgentError("Each point must be [x, y] or {\"x\": .., \"y\": ..}"),
			};
			if (x < vs.Left - Margin || x > vs.Right + Margin || y < vs.Top - Margin || y > vs.Bottom + Margin)
				throw new AgentError("Point is off screen");

			result.Add(new POINT((int)Math.Round(x), (int)Math.Round(y)));
		}
		return result;
	}

	/// <summary>A required integer in [min, max].</summary>
	private static int Int(JsonObject args, string name, int min, int max)
	{
		var value = Number(args[name], name);
		if (value < min || value > max)
			throw new AgentError($"'{name}' must be between {min} and {max}");

		return (int)Math.Round(value);
	}

	/// <summary>A required finite number.</summary>
	private static double Number(JsonNode? node, string name)
	{
		// Only JSON numbers are accepted; strings are not coerced
		if (node is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number))
			throw new AgentError($"'{name}' must be a number");

		return number;
	}

	/// <summary>The optional 'label', cleaned for display.</summary>
	private static string Label(JsonObject args)
	{
		var text = args["label"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
		return Names.Sanitise(text, Protocol.MaxLabelLength);
	}

	/// <summary>The optional 'duration' in seconds (0 = until cleared).</summary>
	private static double Duration(JsonObject args)
	{
		if (args["duration"] is null)
			return DefaultDuration;

		var value = Number(args["duration"], "duration");
		if (value < 0 || value > MaxDuration)
			throw new AgentError($"'duration' must be between 0 and {MaxDuration}");

		return value;
	}

	/// <summary>The optional 'colour' as "#RRGGBB" (defaults to the agent colour).</summary>
	private static uint Colour(JsonObject args)
	{
		// Strict format: exactly '#' and six hex digits
		if (args["colour"] is null)
			return Overlay.AgentColour;

		var text = args["colour"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
		if (text is not { Length: 7 } || text[0] != '#' || !uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var rgb))
			throw new AgentError("'colour' must be \"#RRGGBB\"");

		return rgb;
	}
}
