using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneBlindMouse.Core;

namespace OneBlindMouse.Windows.Agent;

/// <summary>
/// A Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0), so AI agents can point on screen.
/// Local mode ("OneBlindMouse.exe mcp") forwards tools to the running tray app through <see cref="LocalApi"/>, starting the app if needed.
/// Remote mode ("OneBlindMouse.exe mcp --join CODE [--name NAME]") joins a session as its own member, so an agent can point on another person's screen.
/// </summary>
internal static class McpServer
{
	private const string ProtocolVersion = "2025-06-18";

	/// <summary>Output goes to a pipe read by a program, not a web page, so only the escaping JSON itself requires is applied. This keeps tool text readable for agents.</summary>
	private static readonly JsonSerializerOptions s_json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>Tool definitions for local mode. Coordinates are physical screen pixels.</summary>
	private const string LocalTools = """
	[
		{ "name": "get_screen_layout", "description": "Get the monitors (physical pixel bounds and display scale), the cursor position and the foreground window's client area. Call this first to convert screenshot positions into screen coordinates.", "inputSchema": { "type": "object", "properties": {} } },
		{ "name": "point_at", "description": "Glide the agent's labelled pointer to (x, y) in physical screen pixels and pulse there, to show the user where something is. There is one agent pointer; each call moves it from where it was (or from the user's cursor if hidden), so the user's eye can follow it.", "inputSchema": { "type": "object", "properties": {
			"x": { "type": "number" }, "y": { "type": "number" },
			"label": { "type": "string", "description": "Short text next to the pointer, e.g. 'Save is here'." },
			"colour": { "type": "string", "description": "#RRGGBB. Defaults to amber." },
			"duration": { "type": "number", "description": "Seconds to keep the pointer after it arrives (0 = until cleared). Default 5." } }, "required": ["x", "y"] } },
		{ "name": "show_click", "description": "Glide the agent's pointer to (x, y) in physical screen pixels and play click ripples there, to prompt the user to click it. Does not click anything.", "inputSchema": { "type": "object", "properties": {
			"x": { "type": "number" }, "y": { "type": "number" },
			"label": { "type": "string", "description": "Short text next to the pointer, e.g. 'Click Save'." },
			"colour": { "type": "string" }, "duration": { "type": "number", "description": "Seconds to keep the pointer after it arrives (0 = until cleared). Default 5." } }, "required": ["x", "y"] } },
		{ "name": "circle", "description": "Glide the agent's pointer to an area and draw a hand-drawn circle around it, to draw the user's attention to it. Give the bounds of the thing to circle in physical screen pixels; the circle passes just outside them.", "inputSchema": { "type": "object", "properties": {
			"x": { "type": "number" }, "y": { "type": "number" }, "width": { "type": "number" }, "height": { "type": "number" },
			"label": { "type": "string", "description": "Short text above the circle." },
			"colour": { "type": "string" }, "duration": { "type": "number", "description": "Seconds to keep the circle after it is drawn (0 = until cleared). Default 5." } }, "required": ["x", "y", "width", "height"] } },
		{ "name": "highlight_rect", "description": "Outline a rectangle on the user's screen (physical pixels), with an optional label.", "inputSchema": { "type": "object", "properties": {
			"x": { "type": "number" }, "y": { "type": "number" }, "width": { "type": "number" }, "height": { "type": "number" },
			"label": { "type": "string" }, "colour": { "type": "string" }, "duration": { "type": "number" } }, "required": ["x", "y", "width", "height"] } },
		{ "name": "trace_path", "description": "Draw an arrow along a path of points (physical pixels), e.g. to show a drag or the order of steps.", "inputSchema": { "type": "object", "properties": {
			"points": { "type": "array", "items": { "type": "array", "items": { "type": "number" }, "minItems": 2, "maxItems": 2 }, "minItems": 2, "maxItems": 256 },
			"colour": { "type": "string" }, "duration": { "type": "number" } }, "required": ["points"] } },
		{ "name": "clear", "description": "Hide the agent's pointer and remove all annotations made by agents.", "inputSchema": { "type": "object", "properties": {} } },
		{ "name": "get_session", "description": "Get this user's One-Blind-Mouse session code and the people in it.", "inputSchema": { "type": "object", "properties": {} } },
		{ "name": "join_session", "description": "Join another One-Blind-Mouse session using a code the user gave you.", "inputSchema": { "type": "object", "properties": {
			"code": { "type": "string" } }, "required": ["code"] } }
	]
	""";

	/// <summary>Tool definitions for remote mode. Coordinates are fractions of the other person's shared area.</summary>
	private const string RemoteTools = """
	[
		{ "name": "get_session", "description": "Get the session members and what each one is sharing (label and aspect ratio of their pointing area).", "inputSchema": { "type": "object", "properties": {} } },
		{ "name": "point_at", "description": "Show this agent's pointer on the other people's screens. x and y are fractions (0..1) of their shared screen or window, from the top-left.", "inputSchema": { "type": "object", "properties": {
			"x": { "type": "number", "minimum": 0, "maximum": 1 }, "y": { "type": "number", "minimum": 0, "maximum": 1 },
			"duration": { "type": "number", "description": "Seconds to show it (0 = until cleared). Default 5." } }, "required": ["x", "y"] } },
		{ "name": "show_click", "description": "Move the pointer to (x, y) (fractions 0..1) and play a click ripple there. Does not click anything.", "inputSchema": { "type": "object", "properties": {
			"x": { "type": "number", "minimum": 0, "maximum": 1 }, "y": { "type": "number", "minimum": 0, "maximum": 1 } }, "required": ["x", "y"] } },
		{ "name": "clear", "description": "Hide this agent's pointer.", "inputSchema": { "type": "object", "properties": {} } }
	]
	""";

	/// <summary>Run the server until stdin closes. 'args' are the arguments after "mcp".</summary>
	public static async Task<int> RunAsync(string[] args)
	{
		// Options for remote mode
		string? join = null;
		var name = "AI agent";
		for (var i = 0; i != args.Length; ++i)
		{
			switch (args[i])
			{
				case "--join" when i + 1 < args.Length:
				{
					join = args[++i];
					break;
				}
				case "--name" when i + 1 < args.Length:
				{
					name = args[++i];
					break;
				}
				case "--profile" when i + 1 < args.Length:
				{
					// Talk to the tray app running with this test profile
					AppConfig.Profile = args[++i];
					break;
				}
				default:
				{
					await Console.Error.WriteLineAsync($"Unknown argument: {args[i]}").ConfigureAwait(false);
					return 2;
				}
			}
		}

		// Remote mode needs a valid code before anything starts
		IToolHost host;
		if (join is not null)
		{
			if (!SessionCode.TryParse(join, out var code))
			{
				await Console.Error.WriteLineAsync("Invalid session code").ConfigureAwait(false);
				return 2;
			}
			host = new RemoteHost(code, Names.SanitiseOr(name, "AI agent"));
		}
		else
		{
			host = new LocalHost();
		}

		// stdout carries only protocol messages
		await using (host as IAsyncDisposable)
		{
			using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
			await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
			while (await stdin.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				if (string.IsNullOrWhiteSpace(line))
					continue;

				var response = await HandleAsync(host, line).ConfigureAwait(false);
				if (response is not null)
					await stdout.WriteLineAsync(response.ToJsonString(s_json)).ConfigureAwait(false);
			}
		}
		return 0;
	}

	/// <summary>Handle one JSON-RPC message. Returns null for notifications.</summary>
	private static async Task<JsonObject?> HandleAsync(IToolHost host, string line)
	{
		// Parse errors get the standard JSON-RPC error with a null id
		JsonObject msg;
		try
		{
			msg = JsonNode.Parse(line) as JsonObject ?? throw new FormatException();
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
		{
			return Error(null, -32700, "Parse error");
		}

		// Messages without an id are notifications and get no response
		var id = msg["id"]?.DeepClone();
		var method = msg["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
		if (id is null)
			return null;

		try
		{
			switch (method)
			{
				case "initialize":
				{
					// Agree on the client's version when given, since the tools work the same in all versions
					var version = msg["params"]?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var pv) ? pv : ProtocolVersion;
					return Result(id, new JsonObject
					{
						["protocolVersion"] = version,
						["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
						["serverInfo"] = new JsonObject { ["name"] = "one-blind-mouse", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
						["instructions"] = host.Instructions,
					});
				}
				case "ping":
				{
					return Result(id, new JsonObject());
				}
				case "tools/list":
				{
					return Result(id, new JsonObject { ["tools"] = JsonNode.Parse(host.Tools) });
				}
				case "tools/call":
				{
					// Tool failures are reported as tool results (isError) so the agent can read and recover from them
					var p = msg["params"] as JsonObject ?? throw new FormatException("Missing params");
					var tool = p["name"]?.GetValue<string>() ?? throw new FormatException("Missing tool name");
					var args = p["arguments"] as JsonObject ?? [];
					try
					{
						var result = await host.CallAsync(tool, args).ConfigureAwait(false);
						return Result(id, ToolResult(result?.ToJsonString(s_json) ?? "ok", is_error: false));
					}
					catch (Exception ex) when (ex is AgentError or IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
					{
						return Result(id, ToolResult(ex.Message, is_error: true));
					}
				}
				default:
				{
					return Error(id, -32601, "Method not found");
				}
			}
		}
		catch (Exception ex) when (ex is FormatException or InvalidOperationException)
		{
			return Error(id, -32602, ex.Message);
		}
	}

	/// <summary>A tool result with one text item.</summary>
	private static JsonObject ToolResult(string text, bool is_error)
	{
		return new JsonObject
		{
			["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
			["isError"] = is_error,
		};
	}

	/// <summary>A JSON-RPC success response.</summary>
	private static JsonObject Result(JsonNode? id, JsonNode result)
	{
		return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
	}

	/// <summary>A JSON-RPC error response.</summary>
	private static JsonObject Error(JsonNode? id, int code, string message)
	{
		return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
	}

	/// <summary>Where tool calls go.</summary>
	private interface IToolHost
	{
		/// <summary>Usage notes for the agent.</summary>
		string Instructions { get; }

		/// <summary>The tool definitions as a JSON array.</summary>
		string Tools { get; }

		/// <summary>Run a tool.</summary>
		Task<JsonNode?> CallAsync(string tool, JsonObject args);
	}

	/// <summary>Forwards tools to the tray app on this machine.</summary>
	private sealed class LocalHost : IToolHost
	{
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

		public string Instructions => "Draws a gliding agent pointer, click prompts, circles and highlights on this user's screen, to direct their attention. It never clicks or types. Coordinates are physical screen pixels; call get_screen_layout first. Call clear when the annotations are no longer needed.";

		public string Tools => LocalTools;

		/// <inheritdoc/>
		public async Task<JsonNode?> CallAsync(string tool, JsonObject args)
		{
			// Tool names map onto pipe commands; arguments pass through and are validated by the app
			var cmd = tool switch
			{
				"join_session" => "join",
				"get_screen_layout" or "point_at" or "show_click" or "highlight_rect" or "circle" or "trace_path" or "clear" or "get_session" => tool,
				_ => throw new AgentError($"Unknown tool '{Names.Sanitise(tool, 32)}'"),
			};
			await EnsureAppAsync().ConfigureAwait(false);
			var request = (JsonObject)args.DeepClone();
			request["cmd"] = cmd;
			return await LocalApi.CallAsync(request, Timeout).ConfigureAwait(false);
		}

		/// <summary>Start the tray app if it isn't running, and wait for it to listen.</summary>
		private static async Task EnsureAppAsync()
		{
			// A quick ping avoids starting a second copy
			if (await LocalApi.PingAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false))
				return;

			var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the app executable");
			var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
			if (AppConfig.Profile.Length != 0)
			{
				psi.ArgumentList.Add("--profile");
				psi.ArgumentList.Add(AppConfig.Profile);
			}
			Process.Start(psi)?.Dispose();

			// Wait for the pipe to come up
			var clock = Stopwatch.StartNew();
			while (clock.Elapsed < Timeout)
			{
				if (await LocalApi.PingAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false))
					return;

				await Task.Delay(200).ConfigureAwait(false);
			}
			throw new TimeoutException("One-Blind-Mouse did not start");
		}
	}

	/// <summary>Joins a session as a headless member and points on the other members' screens.</summary>
	private sealed class RemoteHost : IToolHost, IAsyncDisposable
	{
		private readonly RelayClient m_relay;
		private readonly SessionCode m_code;
		private readonly Lock m_lock = new();
		private readonly CancellationTokenSource m_cts = new();
		private readonly Task m_heartbeat;
		private (double X, double Y) m_pos;
		private bool m_visible;
		private long m_expires;
		private ushort m_seq;

		/// <summary>Connect and join 'code' as 'name'.</summary>
		public RemoteHost(SessionCode code, string name)
		{
			m_code = code;
			m_relay = new RelayClient(RelayClient.WebSocketConnector(AppConfig.RelayUri), name);
			m_relay.Notice += (level, text) => Console.Error.WriteLine($"[{level}] {text}");
			m_relay.Start();
			m_relay.Join(code);
			m_heartbeat = Task.Run(() => HeartbeatAsync(m_cts.Token));
		}

		public string Instructions => "Shows this agent's pointer on the screens of the people in a One-Blind-Mouse session. It never clicks or types. Coordinates are fractions (0..1) of each person's shared screen or window.";

		public string Tools => RemoteTools;

		/// <inheritdoc/>
		public Task<JsonNode?> CallAsync(string tool, JsonObject args)
		{
			// Pointer tools need to be in the session with someone to point at
			switch (tool)
			{
				case "get_session":
				{
					return Task.FromResult<JsonNode?>(Session());
				}
				case "point_at":
				{
					RequireShared();
					var duration = args["duration"] is null ? 5 : Fraction(args, "duration", 600);
					Show(Fraction(args, "x", 1), Fraction(args, "y", 1), duration, EPointerFlags.None);
					return Task.FromResult<JsonNode?>(null);
				}
				case "show_click":
				{
					RequireShared();
					Show(Fraction(args, "x", 1), Fraction(args, "y", 1), 5, EPointerFlags.Click);
					return Task.FromResult<JsonNode?>(null);
				}
				case "clear":
				{
					lock (m_lock)
					{
						m_visible = false;
						Send(EPointerFlags.None);
					}
					return Task.FromResult<JsonNode?>(null);
				}
				default:
				{
					throw new AgentError($"Unknown tool '{Names.Sanitise(tool, 32)}'");
				}
			}
		}

		/// <summary>Leave the session.</summary>
		public async ValueTask DisposeAsync()
		{
			m_cts.Cancel();
			try
			{
				await m_heartbeat.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
			await m_relay.DisposeAsync().ConfigureAwait(false);
			m_cts.Dispose();
		}

		/// <summary>Move the pointer and keep it visible for 'duration' seconds (0 = until cleared).</summary>
		private void Show(double x, double y, double duration, EPointerFlags events)
		{
			lock (m_lock)
			{
				m_pos = (x, y);
				m_visible = true;
				m_expires = duration > 0 ? Environment.TickCount64 + (long)(duration * 1000) : long.MaxValue;
				Send(EPointerFlags.Visible | events);
			}
		}

		/// <summary>Send one sample to everyone. Caller holds the lock.</summary>
		private void Send(EPointerFlags flags)
		{
			m_relay.SendPointer(new PointerFrame(0, 0, ++m_seq, PointerFrame.Quantise(m_pos.X), PointerFrame.Quantise(m_pos.Y), flags, PointerFrame.DefaultSize));
		}

		/// <summary>Resend the pointer twice a second while visible (receivers hide silent pointers), and hide it when it expires.</summary>
		private async Task HeartbeatAsync(CancellationToken ct)
		{
			using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
			while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
			{
				lock (m_lock)
				{
					if (!m_visible)
						continue;

					m_visible = Environment.TickCount64 < m_expires;
					Send(m_visible ? EPointerFlags.Visible : EPointerFlags.None);
				}
			}
		}

		/// <summary>Fail with a helpful message if there is no one to point at.</summary>
		private void RequireShared()
		{
			var snap = m_relay.Snapshot;
			if (snap.Connection != EConnection.Online)
				throw new AgentError("Not connected to the relay yet; try again shortly");

			if (snap.Code?.Room != m_code.Room)
				throw new AgentError("Not in the session yet (the code may be wrong or expired); try get_session");

			if (!snap.IsShared)
				throw new AgentError("No one else is in the session");
		}

		/// <summary>Session state for the agent.</summary>
		private JsonObject Session()
		{
			var snap = m_relay.Snapshot;
			var members = new JsonArray();
			foreach (var m in snap.Members)
			{
				members.Add((JsonNode)new JsonObject
				{
					["name"] = m.Name,
					["is_me"] = m.Id == snap.Me,
					["is_owner"] = m.Id == snap.Owner,
					["sharing"] = m.Label,
					["aspect"] = m.Aspect,
				});
			}
			return new JsonObject
			{
				["connection"] = snap.Connection.ToString().ToLowerInvariant(),
				["joined"] = snap.Code?.Room == m_code.Room,
				["members"] = members,
			};
		}

		/// <summary>A required number in [0, max].</summary>
		private static double Fraction(JsonObject args, string name, double max)
		{
			if (args[name] is not JsonValue v || !v.TryGetValue<double>(out var value) || !double.IsFinite(value) || value < 0 || value > max)
				throw new AgentError($"'{name}' must be a number between 0 and {max}");

			return value;
		}
	}
}
