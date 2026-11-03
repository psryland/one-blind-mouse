using ThreeBlindMice.Core;

namespace ThreeBlindMice.Windows;

class Program
{
	// The constraint region defines where normalised (0,1) cursor coords map to in screen space.
	// Volatile because it's read by the overlay paint thread and written by PubSub/tracker threads.
	private static volatile int[] s_constraint = Array.Empty<int>(); // [left, top, width, height]
	private static WindowTracker? s_window_tracker;
	private static readonly object s_tracker_lock = new();

	static async Task Main(string[] args)
	{
		Console.WriteLine("Three Blind Mice — Windows Mouse Receiver");

		// Parse session code first (may rename the exe if session code is in the filename)
		var session_code = ParseSessionCode(args);

		// Register the tbm:// protocol handler and .tbm file association
		// (done after ParseSessionCode so the registered path uses the clean exe name)
		if (!ProtocolRegistrar.Is_Registered())
		{
			ProtocolRegistrar.Register();
			Console.WriteLine("Registered tbm:// protocol handler and .tbm file association.");
		}

		if (session_code == null)
		{
			// No session code — prompt the user
			Console.Write("Enter session code: ");
			var input = Console.ReadLine()?.Trim();
			if (input != null && TbmUriParser.IsValidSessionCode(input))
				session_code = input;
		}

		if (session_code == null)
		{
			Console.Error.WriteLine("No valid session code provided.");
			Console.Error.WriteLine("Usage:");
			Console.Error.WriteLine("  ThreeBlindMice.Windows tbm://session/<code>");
			Console.Error.WriteLine("  ThreeBlindMice.Windows --session <code>");
			Console.Error.WriteLine("  ThreeBlindMice.Windows.XXXXXX.exe  (session code in filename)");
			Environment.Exit(1);
			return;
		}

		Console.WriteLine($"Session code: {session_code}");

		// Enumerate monitors
		var monitors = MonitorEnumerator.Enumerate();
		Console.WriteLine($"Detected {monitors.Count} monitor(s):");
		foreach (var mon in monitors)
		{
			var primary_tag = mon.Is_Primary ? " (primary)" : "";
			Console.WriteLine($"  [{mon.Index}] {mon.Device}  {mon.Width}x{mon.Height} at ({mon.Left},{mon.Top}){primary_tag}");
		}

		// Overlay covers the entire virtual screen so cursors can appear on any monitor
		var vx = monitors.Min(m => m.Left);
		var vy = monitors.Min(m => m.Top);
		var vr = monitors.Max(m => m.Left + m.Width);
		var vb = monitors.Max(m => m.Top + m.Height);
		var vw = vr - vx;
		var vh = vb - vy;

		// Default constraint: primary monitor (or first)
		var primary = monitors.Where(m => m.Is_Primary).DefaultIfEmpty(monitors[0]).First();
		s_constraint = new[] { primary.Left, primary.Top, primary.Width, primary.Height };
		Console.WriteLine($"Default constraint: {primary.Device} ({primary.Width}x{primary.Height})");

		var exit = new ManualResetEventSlim(false);

		using var overlay = new Win32Overlay(vx, vy, vw, vh);
		overlay.Start();
		Console.WriteLine($"Overlay window created ({vw}x{vh} virtual screen).");

		var cursor_state = new CursorState();
		using var renderer = new CursorRenderer();

		// Paint callback: maps normalised coords through the constraint region
		overlay.On_Paint = (hdc, w, h) =>
		{
			cursor_state.Remove_Inactive();
			var cr = s_constraint;
			if (cr.Length == 4)
				renderer.Render(hdc, cursor_state, w, h, cr[0], cr[1], cr[2], cr[3], vx, vy);
			else
				renderer.Render(hdc, cursor_state, w, h);
		};

		// Enumerate windows (with HWNDs for tracking)
		var windows = WindowEnumerator.Enumerate(monitors);
		Console.WriteLine($"Enumerated {windows.Count} window(s).");

		using var pubsub = new PubSubClient();
		long last_heartbeat = Environment.TickCount64;
		var heartbeat_received = false;

		pubsub.On_Message += msg =>
		{
			switch (msg)
			{
				case CursorMessage cursor_msg:
					cursor_state.Update_Cursor(cursor_msg);
					overlay.RequestRepaint();
					break;
				case JoinMessage join_msg:
					cursor_state.Add_User(join_msg);
					overlay.RequestRepaint();
					break;
				case LeaveMessage leave_msg:
					cursor_state.Remove_User(leave_msg.User_Id);
					overlay.RequestRepaint();
					break;

				case HostConstraintMessage constraint_msg:
					Apply_Constraint(constraint_msg, monitors, windows, overlay);
					break;

				case HostHeartbeatMessage:
					Interlocked.Exchange(ref last_heartbeat, Environment.TickCount64);
					heartbeat_received = true;
					break;

				case HostEndMessage:
					Console.WriteLine("Session ended by host.");
					overlay.Shutdown();
					exit.Set();
					break;

				case HostPickWindowMessage:
					_ = Task.Run(async () =>
					{
						var picked = await ScreenPicker.Pick_Window_Async(monitors);
						if (picked != null)
						{
							await pubsub.Send_Message(new HostWindowPickedMessage
							{
								Title = picked.Title,
								Left = picked.Left,
								Top = picked.Top,
								Width = picked.Width,
								Height = picked.Height,
								Monitor_Index = picked.Monitor_Index,
							});

							// Immediately constrain to the picked window and start tracking
							Start_Window_Tracking(picked.Hwnd, overlay);
						}
					});
					break;

				case HostPickRectangleMessage:
					_ = Task.Run(async () =>
					{
						var picked = await ScreenPicker.Pick_Rectangle_Async();
						if (picked != null)
						{
							await pubsub.Send_Message(new HostRectanglePickedMessage
							{
								Left = picked.Left,
								Top = picked.Top,
								Width = picked.Width,
								Height = picked.Height,
							});

							// Immediately constrain to the picked rectangle
							Stop_Window_Tracking();
							s_constraint = new[] { picked.Left, picked.Top, picked.Width, picked.Height };
							Console.WriteLine($"Constraint set to rectangle ({picked.Left},{picked.Top}) {picked.Width}x{picked.Height}");
							overlay.RequestRepaint();
						}
					});
					break;
			}
		};

		pubsub.On_Connected += () => Console.WriteLine("Connected to Web PubSub.");
		pubsub.On_Disconnected += () => Console.WriteLine("Disconnected from Web PubSub.");
		pubsub.On_Error += err => Console.Error.WriteLine($"PubSub error: {err}");

		const string negotiate_url = "https://mango-beach-0f96e6000.1.azurestaticapps.net/api/negotiate";
		var host_id = $"host-{Guid.NewGuid():N}";
		await pubsub.Connect(negotiate_url, session_code, host_id);

		// Send host info
		var host_info = new HostInfoMessage
		{
			Monitors = monitors.Select(m => new MonitorData
			{
				Index = m.Index,
				Device = m.Device,
				Left = m.Left,
				Top = m.Top,
				Width = m.Width,
				Height = m.Height,
				Is_Primary = m.Is_Primary,
			}).ToList(),
			Windows = windows.Select(w => new WindowData
			{
				Title = w.Title,
				Left = w.Left,
				Top = w.Top,
				Width = w.Width,
				Height = w.Height,
				Monitor_Index = w.Monitor_Index,
			}).ToList(),
		};
		await pubsub.Send_Message(host_info);
		Console.WriteLine("Sent host info.");

		foreach (var mon in monitors)
		{
			var data_url = ScreenCapture.Capture_Monitor(mon.Left, mon.Top, mon.Width, mon.Height);
			if (data_url != null)
			{
				await pubsub.Send_Message(new HostThumbnailMessage { Monitor_Index = mon.Index, Data_Url = data_url });
				Console.WriteLine($"Sent thumbnail for monitor [{mon.Index}].");
			}
			else
			{
				Console.Error.WriteLine($"Failed to capture thumbnail for monitor [{mon.Index}].");
			}
		}

		using var tray = new TrayIcon(() =>
		{
			overlay.Shutdown();
			exit.Set();
		});
		tray.Show(session_code);

		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			overlay.Shutdown();
			exit.Set();
		};

		Console.WriteLine("Running. Right-click tray icon or press Ctrl+C to exit.");

		// Watchdog: shut down if no heartbeat from the web host for 30 seconds
		_ = Task.Run(async () =>
		{
			while (!exit.IsSet)
			{
				await Task.Delay(5000);
				if (heartbeat_received && Environment.TickCount64 - Interlocked.Read(ref last_heartbeat) > 30_000)
				{
					Console.WriteLine("No heartbeat for 30 seconds, shutting down.");
					overlay.Shutdown();
					exit.Set();
					break;
				}
			}
		});

		exit.Wait();

		Stop_Window_Tracking();
		await pubsub.Disconnect();
		Console.WriteLine("Exiting.");
	}

	/// <summary>
	/// Applies a constraint message from the web UI (monitor/window/rectangle selection).
	/// </summary>
	private static void Apply_Constraint(
		HostConstraintMessage msg,
		List<MonitorEnumerator.MonitorInfo> monitors,
		List<WindowEnumerator.WindowInfo> windows,
		Win32Overlay overlay)
	{
		switch (msg.Mode)
		{
			case "monitor":
			{
				Stop_Window_Tracking();
				var idx = msg.Monitor_Index ?? 0;
				var mon = monitors.Where(m => m.Index == idx).DefaultIfEmpty(monitors[0]).First();
				s_constraint = new[] { mon.Left, mon.Top, mon.Width, mon.Height };
				Console.WriteLine($"Constraint set to monitor [{mon.Index}] {mon.Device}");
				overlay.RequestRepaint();
				break;
			}
			case "window":
			{
				// Find the window by title and start tracking its position
				var target = windows.FirstOrDefault(w =>
					w.Title.Equals(msg.Window_Title, StringComparison.OrdinalIgnoreCase));
				if (target != null)
				{
					Start_Window_Tracking(target.Hwnd, overlay);
				}
				else
				{
					Console.WriteLine($"Constraint: window '{msg.Window_Title}' not found.");
				}
				break;
			}
			case "rectangle":
			{
				Stop_Window_Tracking();
				var l = msg.Left ?? 0;
				var t = msg.Top ?? 0;
				var w = msg.Width ?? 1920;
				var h = msg.Height ?? 1080;
				s_constraint = new[] { l, t, w, h };
				Console.WriteLine($"Constraint set to rectangle ({l},{t}) {w}x{h}");
				overlay.RequestRepaint();
				break;
			}
		}
	}

	/// <summary>
	/// Begins tracking a window by HWND. Updates the constraint region as the window moves.
	/// </summary>
	private static void Start_Window_Tracking(IntPtr hwnd, Win32Overlay overlay)
	{
		lock (s_tracker_lock)
		{
			s_window_tracker?.Stop();
			s_window_tracker?.Dispose();

			var tracker = new WindowTracker(hwnd);
			tracker.On_Bounds_Changed += bounds =>
			{
				s_constraint = new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height };
				overlay.RequestRepaint();
			};
			tracker.On_Window_Lost += () =>
			{
				Console.WriteLine("Tracked window was closed.");
			};
			tracker.Start();
			s_window_tracker = tracker;
			Console.WriteLine("Window tracking started.");
		}
	}

	private static void Stop_Window_Tracking()
	{
		lock (s_tracker_lock)
		{
			if (s_window_tracker != null)
			{
				s_window_tracker.Stop();
				s_window_tracker.Dispose();
				s_window_tracker = null;
				Console.WriteLine("Window tracking stopped.");
			}
		}
	}

	private static string? ParseSessionCode(string[] args)
	{
		// Check exe filename for embedded session code: ThreeBlindMice.Windows.XXXXXX.exe
		var exe_path = Environment.ProcessPath;
		if (exe_path != null)
		{
			var filename = Path.GetFileNameWithoutExtension(exe_path);
			var parts = filename.Split('.');
			if (parts.Length >= 3 && TbmUriParser.IsValidSessionCode(parts[^1]))
			{
				var code = parts[^1];

				// Rename to remove session code so the clean exe is used for future protocol launches
				var dir = Path.GetDirectoryName(exe_path)!;
				var clean_name = string.Join('.', parts[..^1]) + Path.GetExtension(exe_path);
				var clean_path = Path.Combine(dir, clean_name);
				try
				{
					if (clean_path != exe_path)
					{
						if (File.Exists(clean_path))
							File.Delete(clean_path);
						File.Move(exe_path, clean_path);
						Console.WriteLine($"Renamed to {clean_name}");
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Could not rename: {ex.Message}");
				}

				return code;
			}
		}

		if (args.Length == 0)
			return null;

		// tbm:// protocol handler
		if (args[0].StartsWith("tbm://", StringComparison.OrdinalIgnoreCase))
			return TbmUriParser.TryParseSessionCode(args[0]);

		// --session flag
		for (var i = 0; i < args.Length - 1; i++)
		{
			if (args[i] == "--session")
			{
				var code = args[i + 1];
				return TbmUriParser.IsValidSessionCode(code) ? code : null;
			}
		}

		return null;
	}
}
