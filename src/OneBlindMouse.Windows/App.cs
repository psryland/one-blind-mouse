using OneBlindMouse.Core;
using OneBlindMouse.Windows.Agent;
using OneBlindMouse.Windows.Native;
using OneBlindMouse.Windows.Ui;

namespace OneBlindMouse.Windows;

/// <summary>The tray application: wires the relay connection to the overlay, transmitter, tray icon, session popup and local agent API. UI thread only.</summary>
internal sealed class App : IDisposable
{
	/// <summary>Pointer size choices in the tray menu (percent).</summary>
	private static readonly (int Size, string Name)[] PointerSizes = [(60, "Small"), (PointerFrame.DefaultSize, "Normal"), (150, "Large"), (220, "Huge")];

	private readonly Settings m_settings;
	private readonly nint m_gdip;
	private readonly UiDispatcher m_ui;
	private readonly RelayClient m_relay;
	private readonly Target m_target;
	private readonly Overlay m_overlay;
	private readonly Transmitter m_transmitter;
	private readonly TrayIcon m_tray;
	private readonly SessionPopup m_popup;
	private readonly LocalApi m_api;
	private SessionSnapshot m_snap;
	private nuint m_housekeeping;

	/// <summary>Create the app on the UI thread and start connecting. 'join' is a session to join once connected.</summary>
	public App(SessionCode? join)
	{
		// Settings and GDI+ are needed by everything else
		m_settings = Settings.Load();
		var input = new GdiplusStartupInput { GdiplusVersion = 1 };
		if (Gdip.GdiplusStartup(out m_gdip, input, 0) != 0)
			throw new InvalidOperationException("GDI+ failed to start");

		// The relay client raises events on network threads, so they are marshalled to the UI thread
		m_ui = new UiDispatcher();
		m_relay = new RelayClient(RelayClient.WebSocketConnector(AppConfig.RelayUri), m_settings.Name, m_settings.ColourPref);
		m_snap = m_relay.Snapshot;
		m_relay.SessionChanged += snap => m_ui.Post(() => OnSession(snap));
		m_relay.PointerReceived += frame => m_ui.Post(() => m_overlay!.OnFrame(frame));
		m_relay.Notice += (level, text) => m_ui.Post(() => m_tray!.ShowBalloon(AppConfig.ProductName, text, level));

		// Receiving side: remote pointers map onto the shared monitor or window
		m_target = new Target();
		m_target.SetMonitor(m_settings.TargetMonitor);
		m_overlay = new Overlay(m_ui, m_target, hide_from_capture: !m_settings.ShowInCapture);

		// Sending side, tray icon and popup
		m_transmitter = new Transmitter(m_ui, m_relay, m_settings);
		m_tray = new TrayIcon();
		m_tray.Clicked += () => m_popup!.Toggle();
		m_tray.MenuRequested += ShowMenu;
		m_tray.SessionLocked += m_transmitter.Cancel;
		m_tray.DisplayChanged += Housekeeping;
		m_popup = new SessionPopup();
		m_popup.JoinRequested += OnJoinRequested;
		m_popup.RenameRequested += OnRename;
		m_popup.LeaveRequested += m_relay.Leave;
		m_popup.NewCodeRequested += m_relay.NewCode;

		// Local agents talk to the app over a named pipe
		var commands = new AgentCommands(m_overlay, m_relay, Activate, m_relay.Join);
		m_api = new LocalApi(m_ui, commands);

		// Connect, then join the requested session (the relay client queues the join until it is online)
		ProtocolHandler.Refresh();
		OnSession(m_snap);
		m_relay.Start();
		if (join is { } code)
			m_relay.Join(code);

		m_housekeeping = m_ui.StartTimer(1000, Housekeeping);
		Housekeeping();
		m_popup.Show();
	}

	/// <summary>Run the message loop until the user quits.</summary>
	public void Run()
	{
		// The popup gets dialog keyboard handling (Tab, Enter, Esc) while it is visible
		UiDispatcher.Run(() => m_popup.IsVisible ? m_popup.Handle : 0);
	}

	/// <summary>Leave the session and release everything.</summary>
	public void Dispose()
	{
		// Release the mouse and hooks first, so nothing is left captured if a later step hangs
		m_ui.StopTimer(ref m_housekeeping);
		m_transmitter.Dispose();
		m_settings.Save();

		// The relay says goodbye so the others see this user leave at once; a dead network must not stop the app closing
		Task.Run(async () =>
		{
			await m_api.DisposeAsync().ConfigureAwait(false);
			await m_relay.DisposeAsync().ConfigureAwait(false);
		}).Wait(TimeSpan.FromSeconds(3));

		// Windows last
		m_popup.Dispose();
		m_tray.Dispose();
		m_overlay.Dispose();
		m_ui.Dispose();
		Gdip.GdiplusShutdown(m_gdip);
	}

	/// <summary>Join a session (used for links and codes forwarded from another launch).</summary>
	public void Join(SessionCode code)
	{
		m_relay.Join(code);
	}

	/// <summary>Bring up the session popup.</summary>
	public void Activate()
	{
		m_popup.Show();
	}

	/// <summary>Show the new session state everywhere.</summary>
	private void OnSession(SessionSnapshot snap)
	{
		// The overlay drops pointers of members who left
		m_snap = snap;
		m_overlay.OnSession(snap);
		m_popup.Update(snap, m_settings.Name, m_settings.HoldKeyName);

		// A chosen recipient who left falls back to automatic
		if (m_transmitter.Recipient != 0 && snap.Members.All(m => m.Id != m_transmitter.Recipient))
			m_transmitter.Recipient = 0;

		// The tray icon is drawn in this user's colour, and its tip summarises the session
		var online = snap.Connection == EConnection.Online;
		m_tray.SetAppearance(snap.Self is { } self ? Palette.Rgb(self.Slot) : 0x9A9A9A, online);
		var state = snap.Connection switch
		{
			EConnection.Online when snap.IsShared => $"{snap.Members.Count} people in {snap.Code?.Display}",
			EConnection.Online => $"Code {snap.Code?.Display}",
			EConnection.Connecting => "Connecting…",
			EConnection.Offline => "Offline",
			_ => throw new ArgumentOutOfRangeException(nameof(snap.Connection), snap.Connection, null),
		};
		m_tray.SetTip($"{AppConfig.Title(AppConfig.ProductName)}\n{state}");
	}

	/// <summary>Join the session typed or pasted into the popup.</summary>
	private void OnJoinRequested(string text)
	{
		// Bad input is reported but left in the box for correction
		if (!SessionCode.TryParse(text, out var code))
		{
			m_tray.ShowBalloon(AppConfig.ProductName, "That isn't a valid session code. Codes look like ABCD-2345.", ENotice.Warning);
			return;
		}
		m_popup.ClearJoin();
		m_relay.Join(code);
	}

	/// <summary>Change this user's display name.</summary>
	private void OnRename(string text)
	{
		// Empty or invalid names keep the current name
		var name = Names.SanitiseOr(text, m_settings.Name);
		if (name == m_settings.Name)
			return;

		m_settings.Name = name;
		m_settings.Save();
		m_relay.Rename(name);
		m_popup.Update(m_snap, name, m_settings.HoldKeyName);
	}

	/// <summary>Periodic checks: tell others about this user's pointing area, and recover from a closed shared window.</summary>
	private void Housekeeping()
	{
		// A closed window can't be pointed at, so revert to the monitor
		if (m_target.IsLost)
		{
			m_target.SetMonitor(m_settings.TargetMonitor);
			m_tray.ShowBalloon(AppConfig.ProductName, "The shared window was closed. Pointers now use your screen.", ENotice.Info);
		}

		// The relay client ignores unchanged values, so this only sends when the area changes shape or name
		m_relay.SetTarget(m_target.Aspect(), m_target.Label());
	}

	/// <summary>Use a different area as this user's pointing target.</summary>
	private void SetMonitorTarget(string device)
	{
		m_settings.TargetMonitor = device;
		m_settings.Save();
		m_target.SetMonitor(device);
		Housekeeping();
	}

	/// <summary>Build and show the tray menu.</summary>
	private void ShowMenu(POINT pt)
	{
		// Session actions first, then settings, then app actions
		var snap = m_snap;
		var others = snap.Members.Where(m => m.Id != snap.Me).ToList();
		var menu = new Menu();
		menu.Item("Show session…", Activate);
		menu.Separator();

		// Who this user's pointer goes to
		var recipients = new Menu();
		if (others.Count != 0)
		{
			recipients.Item("Automatic (whoever is sharing)", () => m_transmitter.Recipient = 0, m_transmitter.Recipient == 0);
			foreach (var m in others)
			{
				var id = m.Id;
				var sharing = m.Label.Length != 0 ? $" — {m.Label}" : string.Empty;
				recipients.Item($"{m.Name}{sharing}", () => m_transmitter.Recipient = id, m_transmitter.Recipient == id);
			}
		}
		menu.Sub($"Point on ({m_settings.HoldKeyName})", recipients);

		// Where other people's pointers appear on this screen
		var area = new Menu();
		var monitors = Monitors.All();
		for (var i = 0; i != monitors.Count; ++i)
		{
			var device = monitors[i].Device;
			var is_target = m_target.Kind == ETargetKind.Monitor && Monitors.Find(m_target.Device).Device == device;
			area.Item($"Screen {i + 1}: {monitors[i].Description}", () => SetMonitorTarget(device), is_target);
		}
		area.Separator();
		if (m_target.Kind == ETargetKind.Window)
			area.Item($"Window: {m_target.Label()}", () => { }, is_checked: true);

		area.Item("Pick a window…", () => m_transmitter.PickWindow(hwnd =>
		{
			// The chosen window's client area becomes the target until it closes or another area is chosen
			m_target.SetWindow(hwnd);
			Housekeeping();
		}));
		menu.Sub("What I'm sharing", area);

		// Frames make this user's pointer map onto part of a window, e.g. the shared screen inside a meeting app
		var frames = new Menu();
		frames.Item("Draw a frame inside a window…", m_transmitter.DrawFrame);
		frames.Item("Clear frames", m_transmitter.ClearFrames, enabled: m_transmitter.HasFrames);
		menu.Sub("Pointing frames", frames);
		menu.Separator();

		// Settings
		var keys = new Menu();
		foreach (var (vk, name) in Settings.HoldKeys)
			keys.Item(name, () => SetHoldKey(vk), m_settings.HoldKey == vk);

		menu.Sub("Hold key to point", keys);
		var sizes = new Menu();
		foreach (var (size, name) in PointerSizes)
			sizes.Item(name, () => SetPointerSize(size), m_settings.PointerSize == size);

		menu.Sub("Pointer size", sizes);
		menu.Item("Show pointers in my screen share", ToggleShowInCapture, m_settings.ShowInCapture);
		menu.Item("Open join links with this app", ToggleProtocolHandler, ProtocolHandler.IsRegistered);
		menu.Separator();

		// Session code and membership
		var has_code = snap.Code is not null;
		menu.Item("Copy code", () => Clipboard.SetText(snap.Code!.Value.Display), enabled: has_code);
		menu.Item("Copy join link", () => Clipboard.SetText(AppConfig.JoinLink(snap.Code!.Value)), enabled: has_code);
		menu.Item("New code", m_relay.NewCode, enabled: snap.IsOwner);
		menu.Item("Leave session", m_relay.Leave, enabled: snap.IsShared || (has_code && !snap.IsOwner));
		var remove = new Menu();
		if (snap.IsOwner)
		{
			foreach (var m in others)
			{
				var id = m.Id;
				remove.Item(m.Name, () => m_relay.Kick(id));
			}
		}
		menu.Sub("Remove", remove);
		menu.Separator();
		menu.Item("Quit", UiDispatcher.Quit);
		m_tray.ShowMenu(menu, pt);
	}

	/// <summary>Change the hold-to-point key.</summary>
	private void SetHoldKey(int vk)
	{
		m_settings.HoldKey = vk;
		m_settings.Save();
		m_popup.Update(m_snap, m_settings.Name, m_settings.HoldKeyName);
	}

	/// <summary>Change the size of this user's pointer on other screens.</summary>
	private void SetPointerSize(int size)
	{
		m_settings.PointerSize = size;
		m_settings.Save();
	}

	/// <summary>Toggle whether remote pointers appear in this user's screen captures.</summary>
	private void ToggleShowInCapture()
	{
		m_settings.ShowInCapture = !m_settings.ShowInCapture;
		m_settings.Save();
		m_overlay.HideFromCapture = !m_settings.ShowInCapture;
	}

	/// <summary>Register or unregister the obm:// link handler.</summary>
	private void ToggleProtocolHandler()
	{
		// Registry failures are reported rather than fatal
		try
		{
			if (ProtocolHandler.IsRegistered)
				ProtocolHandler.Unregister();
			else
				ProtocolHandler.Register();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
			m_tray.ShowBalloon(AppConfig.ProductName, $"Could not change the link handler: {ex.Message}", ENotice.Error);
		}
	}
}
