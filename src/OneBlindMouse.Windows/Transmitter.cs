using System.Diagnostics;
using System.Runtime.InteropServices;
using OneBlindMouse.Core;
using OneBlindMouse.Windows.Native;
using OneBlindMouse.Windows.Ui;

namespace OneBlindMouse.Windows;

/// <summary>What the local mouse is currently being used for.</summary>
internal enum ETransmitMode
{
	/// <summary>Normal use. Only the hold key is watched.</summary>
	Idle,

	/// <summary>The hold key is down: the local mouse drives this user's pointer on another member's screen.</summary>
	Pointing,

	/// <summary>The next click picks a window (used to choose the shared window).</summary>
	PickWindow,

	/// <summary>The next drag defines the pointing frame inside a window.</summary>
	DrawFrame,
}

/// <summary>
/// Turns local mouse input into pointer samples for another member's screen.
/// While the hold key is down, the cursor is confined to a "frame" (the window under the cursor, or a frame drawn by the user) that represents
/// the shared screen as seen in the screen share. Clicks and the wheel are blocked from reaching local applications and become pointer gestures instead.
/// UI thread only. Low-level hooks are called on the UI thread by the message loop, so they must return quickly.
/// </summary>
internal sealed unsafe class Transmitter : IDisposable
{
	/// <summary>Seconds between samples sent while the pointer is not moving, so the receiver knows it is still active.</summary>
	private const double HeartbeatPeriod = 0.5;

	/// <summary>Sample rate limit while moving, in milliseconds.</summary>
	private const uint TickMs = 33;

	/// <summary>Pointer size change per wheel notch, in percent.</summary>
	private const int SizeStep = 10;

	// Low-level hooks have no context parameter, so they route through the single instance
	private static Transmitter? s_instance;

	private readonly UiDispatcher m_ui;
	private readonly RelayClient m_relay;
	private readonly Settings m_settings;
	private readonly CaptureWindow m_capture = new();
	private readonly FrameBorder m_border = new();
	private readonly Stopwatch m_clock = Stopwatch.StartNew();

	// Frames drawn by the user, stored per top-level window as fractions of its client area (left, top, right, bottom)
	private readonly Dictionary<nint, (double L, double T, double R, double B)> m_frames = [];

	private nint m_kb_hook;
	private nint m_mouse_hook;
	private nuint m_timer;
	private ETransmitMode m_mode;

	// Pointing state
	private RECT m_frame;
	private int m_to;
	private POINT m_pos;
	private bool m_laser;
	private bool m_dirty;
	private EPointerFlags m_events;
	private ushort m_seq;
	private double m_last_send;

	// True after the hold key was used in a shortcut or cancelled with Esc; pointing stays off until the key is released
	private bool m_suppressed;

	// Pick and draw state
	private Action<nint>? m_on_pick;
	private nint m_draw_window;
	private POINT m_draw_start;
	private bool m_drawing;

	/// <summary>Install the keyboard hook. Samples are sent through 'relay'.</summary>
	public Transmitter(UiDispatcher ui, RelayClient relay, Settings settings)
	{
		// Only one instance can own the hooks
		if (s_instance is not null)
			throw new InvalidOperationException("Only one transmitter may exist");

		m_ui = ui;
		m_relay = relay;
		m_settings = settings;
		s_instance = this;
		m_kb_hook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &KeyboardHook, Kernel32.GetModuleHandleW(null), 0);
	}

	/// <summary>The current mode.</summary>
	public ETransmitMode Mode => m_mode;

	/// <summary>The member chosen to receive this user's pointer, or 0 to choose automatically.</summary>
	public int Recipient { get; set; }

	/// <summary>Raised when <see cref="Mode"/> changes.</summary>
	public event Action? ModeChanged;

	/// <summary>Seconds since creation.</summary>
	private double Now => m_clock.Elapsed.TotalSeconds;

	/// <summary>Let the user pick a window with the next click. 'picked' receives the top-level window, and is not called if cancelled.</summary>
	public void PickWindow(Action<nint> picked)
	{
		// Any active mode is cancelled first so only one mode owns the mouse
		Cancel();
		m_on_pick = picked;
		SetMode(ETransmitMode.PickWindow);
		InstallMouseHook();
	}

	/// <summary>Let the user drag a rectangle inside a window to use as that window's pointing frame.</summary>
	public void DrawFrame()
	{
		// Any active mode is cancelled first so only one mode owns the mouse
		Cancel();
		m_drawing = false;
		SetMode(ETransmitMode.DrawFrame);
		InstallMouseHook();
	}

	/// <summary>Forget all frames drawn by the user.</summary>
	public void ClearFrames()
	{
		m_frames.Clear();
	}

	/// <summary>True if any frames have been drawn.</summary>
	public bool HasFrames => m_frames.Count != 0;

	/// <summary>Stop whatever mode is active (e.g. when the session is locked).</summary>
	public void Cancel()
	{
		// Each mode has its own clean-up
		switch (m_mode)
		{
			case ETransmitMode.Idle:
			{
				return;
			}
			case ETransmitMode.Pointing:
			{
				StopPointing();
				return;
			}
			case ETransmitMode.PickWindow:
			case ETransmitMode.DrawFrame:
			{
				m_border.Hide();
				m_on_pick = null;
				m_drawing = false;
				RemoveMouseHook();
				SetMode(ETransmitMode.Idle);
				return;
			}
			default:
			{
				throw new ArgumentOutOfRangeException(nameof(m_mode), m_mode, null);
			}
		}
	}

	/// <summary>Remove the hooks and windows.</summary>
	public void Dispose()
	{
		// Release the cursor and hooks before the windows go
		Cancel();
		if (m_kb_hook != 0)
			User32.UnhookWindowsHookEx(m_kb_hook);

		m_kb_hook = 0;
		m_capture.Dispose();
		m_border.Dispose();
		s_instance = null;
	}

	/// <summary>Begin pointing from the current cursor position.</summary>
	private void StartPointing()
	{
		// Pointing only makes sense with someone to point at
		var snap = m_relay.Snapshot;
		if (!snap.IsShared || snap.Self is not { } self)
			return;

		// Choose who sees the pointer and shape the frame like their shared area, so positions map 1:1 onto what they share
		User32.GetCursorPos(out var pt);
		m_to = ResolveRecipient(snap);
		m_frame = Letterbox(FrameAt(pt), RecipientAspect(snap, m_to));
		if (m_frame.IsEmpty)
			return;

		// Keep the cursor inside the frame, take the hover away from the application, and outline the frame in this user's colour
		m_pos = new POINT(Math.Clamp(pt.X, m_frame.Left, m_frame.Right - 1), Math.Clamp(pt.Y, m_frame.Top, m_frame.Bottom - 1));
		m_laser = false;
		m_events = EPointerFlags.None;
		m_dirty = true;
		ClipToFrame();
		m_capture.Show(m_frame);
		m_border.Show(m_frame, Palette.Rgb(self.Slot), Math.Max(2, (int)Monitors.DpiAt(pt.X, pt.Y) / 32));
		InstallMouseHook();
		m_timer = m_ui.StartTimer(TickMs, Tick);
		SetMode(ETransmitMode.Pointing);
		Send();
	}

	/// <summary>End pointing and hide the pointer on the receiver.</summary>
	private void StopPointing()
	{
		// Tell the receiver first, then restore normal mouse behaviour
		m_ui.StopTimer(ref m_timer);
		SendFrame(EPointerFlags.None);
		RemoveMouseHook();
		User32.ClipCursor(null);
		m_capture.Hide();
		m_border.Hide();
		m_laser = false;
		SetMode(ETransmitMode.Idle);
	}

	/// <summary>Periodic work while pointing: send pending movement, heartbeat, and detect a missed key release.</summary>
	private void Tick()
	{
		// A key release can be missed (e.g. if another hook swallowed it), so the key state is checked directly
		if (User32.GetAsyncKeyState(m_settings.HoldKey) >= 0 || !m_relay.Snapshot.IsShared)
		{
			StopPointing();
			return;
		}

		// Other software (or a display change) may release the cursor clip, so it is reapplied
		ClipToFrame();
		if (m_dirty || Now - m_last_send >= HeartbeatPeriod)
			Send();
	}

	/// <summary>Send the current pointer state, including any gesture events since the last sample.</summary>
	private void Send()
	{
		// Visible plus the held/one-shot gesture flags
		var flags = EPointerFlags.Visible | m_events | (m_laser ? EPointerFlags.Laser : EPointerFlags.None);
		SendFrame(flags);
		m_events = EPointerFlags.None;
		m_dirty = false;
	}

	/// <summary>Send one sample with the given flags at the current position.</summary>
	private void SendFrame(EPointerFlags flags)
	{
		// Positions are fractions of the frame, which matches the receiver's target area
		var fx = (m_pos.X - m_frame.Left) / (double)Math.Max(1, m_frame.Width - 1);
		var fy = (m_pos.Y - m_frame.Top) / (double)Math.Max(1, m_frame.Height - 1);
		var size = (byte)Math.Clamp(m_settings.PointerSize, PointerFrame.MinSize, PointerFrame.MaxSize);
		m_relay.SendPointer(new PointerFrame(0, (byte)m_to, ++m_seq, PointerFrame.Quantise(fx), PointerFrame.Quantise(fy), flags, size));
		m_last_send = Now;
	}

	/// <summary>Confine the cursor to the frame.</summary>
	private void ClipToFrame()
	{
		var rc = m_frame;
		User32.ClipCursor(&rc);
	}

	/// <summary>The member who should receive the pointer.</summary>
	private int ResolveRecipient(SessionSnapshot snap)
	{
		// An explicit choice wins while that member is still present
		if (Recipient != 0 && Recipient != snap.Me && snap.Members.Any(m => m.Id == Recipient))
			return Recipient;

		// With one other person there is no ambiguity
		var others = snap.Members.Where(m => m.Id != snap.Me).ToList();
		if (others.Count == 1)
			return others[0].Id;

		// In a group, the owner usually shares the screen; otherwise everyone gets it
		return snap.Owner != snap.Me ? snap.Owner : 0;
	}

	/// <summary>The aspect ratio of the recipient's shared area, or 0 if unknown.</summary>
	private static double RecipientAspect(SessionSnapshot snap, int to)
	{
		// When sending to everyone, the first other member with a known shape is the best guess
		var member = to != 0
			? snap.Members.FirstOrDefault(m => m.Id == to)
			: snap.Members.FirstOrDefault(m => m.Id != snap.Me && m.Aspect > 0);

		return member?.Aspect ?? 0;
	}

	/// <summary>The pointing frame for the window under 'pt': its user-drawn frame, its client area, or the monitor for the desktop.</summary>
	private RECT FrameAt(POINT pt)
	{
		// The desktop, the taskbar, and this app's own windows all mean "the whole monitor"
		var root = RootWindowAt(pt);
		if (root == 0)
			return Monitors.At(pt.X, pt.Y).Bounds;

		// A drawn frame is stored relative to the client area so it follows the window when it moves or resizes
		var client = User32.GetClientRectScreen(root);
		if (client.IsEmpty)
			return Monitors.At(pt.X, pt.Y).Bounds;

		if (!m_frames.TryGetValue(root, out var f))
			return client;

		return new RECT(
			client.Left + (int)Math.Round(f.L * client.Width),
			client.Top + (int)Math.Round(f.T * client.Height),
			client.Left + (int)Math.Round(f.R * client.Width),
			client.Top + (int)Math.Round(f.B * client.Height));
	}

	/// <summary>The top-level application window at 'pt', or 0 for the desktop, the taskbar, or this app's own windows.</summary>
	private static nint RootWindowAt(POINT pt)
	{
		// Shell windows have no meaningful "application area"
		var root = User32.GetAncestor(User32.WindowFromPoint(pt), User32.GA_ROOT);
		if (root == 0 || root == User32.GetShellWindow() || IsOwnWindow(root))
			return 0;

		return User32.GetClassName(root) switch
		{
			"Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => 0,
			_ => root,
		};
	}

	/// <summary>True if 'hwnd' belongs to this process.</summary>
	private static bool IsOwnWindow(nint hwnd)
	{
		User32.GetWindowThreadProcessId(hwnd, out var pid);
		return pid == Environment.ProcessId;
	}

	/// <summary>The largest centred rectangle inside 'rc' with the given aspect ratio (or 'rc' itself if the aspect is unknown).</summary>
	private static RECT Letterbox(RECT rc, double aspect)
	{
		// Shrink whichever dimension is too long for the aspect
		if (aspect <= 0 || rc.IsEmpty)
			return rc;

		if ((double)rc.Width / rc.Height > aspect)
		{
			var w = (int)Math.Round(rc.Height * aspect);
			var x = rc.Left + (rc.Width - w) / 2;
			return new RECT(x, rc.Top, x + w, rc.Bottom);
		}
		else
		{
			var h = (int)Math.Round(rc.Width / aspect);
			var y = rc.Top + (rc.Height - h) / 2;
			return new RECT(rc.Left, y, rc.Right, y + h);
		}
	}

	/// <summary>Change mode and notify listeners.</summary>
	private void SetMode(ETransmitMode mode)
	{
		if (m_mode == mode)
			return;

		m_mode = mode;
		ModeChanged?.Invoke();
	}

	/// <summary>Start receiving low-level mouse events.</summary>
	private void InstallMouseHook()
	{
		if (m_mouse_hook == 0)
			m_mouse_hook = User32.SetWindowsHookExW(User32.WH_MOUSE_LL, &MouseHook, Kernel32.GetModuleHandleW(null), 0);
	}

	/// <summary>Stop receiving low-level mouse events.</summary>
	private void RemoveMouseHook()
	{
		if (m_mouse_hook != 0)
			User32.UnhookWindowsHookEx(m_mouse_hook);

		m_mouse_hook = 0;
	}

	/// <summary>Handle a key event. Returns true to block the key from other applications.</summary>
	private bool OnKey(uint msg, in KBDLLHOOKSTRUCT key)
	{
		// Keys typed by software (e.g. remote desktop tools) are ignored
		if ((key.flags & User32.LLKHF_INJECTED) != 0)
			return false;

		var down = msg is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
		var is_hold = key.vkCode == (uint)m_settings.HoldKey;

		// Releasing the hold key ends pointing and clears any suppression. The key itself is never blocked, so shortcuts still work.
		if (is_hold && !down)
		{
			m_suppressed = false;
			if (m_mode == ETransmitMode.Pointing)
				m_ui.Post(StopPointing);

			return false;
		}

		// Pressing the hold key starts pointing (auto-repeat is ignored because the mode has already changed).
		// The work is posted because creating windows inside a hook risks the hook timeout.
		if (is_hold)
		{
			if (m_mode == ETransmitMode.Idle && !m_suppressed)
				m_ui.Post(() =>
				{
					// The key may already be up, or another mode may have started, by the time this runs
					if (m_mode == ETransmitMode.Idle && User32.GetAsyncKeyState(m_settings.HoldKey) < 0)
						StartPointing();
				});

			return false;
		}

		// Esc cancels any mode and is consumed so the application underneath doesn't also react
		if (down && key.vkCode == User32.VK_ESCAPE && m_mode != ETransmitMode.Idle)
		{
			m_suppressed = m_mode == ETransmitMode.Pointing;
			m_ui.Post(Cancel);
			return true;
		}

		// Another key while pointing means the hold key is part of a shortcut (e.g. Ctrl+C), so pointing gives way to it
		if (down && m_mode == ETransmitMode.Pointing)
		{
			m_suppressed = true;
			m_ui.Post(Cancel);
		}
		return false;
	}

	/// <summary>Handle a mouse event. Returns true to block it from other applications.</summary>
	private bool OnMouse(uint msg, in MSLLHOOKSTRUCT info)
	{
		// Each mode interprets the mouse differently
		switch (m_mode)
		{
			case ETransmitMode.Idle:
			{
				return false;
			}
			case ETransmitMode.Pointing:
			{
				return OnPointingMouse(msg, info);
			}
			case ETransmitMode.PickWindow:
			{
				return OnPickMouse(msg, info);
			}
			case ETransmitMode.DrawFrame:
			{
				return OnDrawMouse(msg, info);
			}
			default:
			{
				throw new ArgumentOutOfRangeException(nameof(m_mode), m_mode, null);
			}
		}
	}

	/// <summary>Mouse handling while pointing. Movement passes through (it is confined to the frame); buttons and the wheel become gestures.</summary>
	private bool OnPointingMouse(uint msg, in MSLLHOOKSTRUCT info)
	{
		// The hook sees the position before clipping is applied, so it is clamped here too
		m_pos = new POINT(Math.Clamp(info.pt.X, m_frame.Left, m_frame.Right - 1), Math.Clamp(info.pt.Y, m_frame.Top, m_frame.Bottom - 1));
		switch (msg)
		{
			case User32.WM_MOUSEMOVE:
			{
				// Movement is sent on the next tick to limit the rate
				m_dirty = true;
				return false;
			}
			case User32.WM_LBUTTONDOWN:
			{
				// A left press is a click ripple and starts the laser trail; it is sent at once so the effect lines up with the movement
				m_laser = true;
				m_events |= EPointerFlags.Click;
				Send();
				return true;
			}
			case User32.WM_LBUTTONUP:
			{
				m_laser = false;
				Send();
				return true;
			}
			case User32.WM_RBUTTONDOWN:
			{
				// A right press is an attention pulse
				m_events |= EPointerFlags.Pulse;
				Send();
				return true;
			}
			case User32.WM_MOUSEWHEEL:
			{
				// The wheel resizes the pointer; the high word of mouseData is the signed wheel delta
				var delta = (short)(info.mouseData >> 16);
				m_settings.PointerSize = Math.Clamp(m_settings.PointerSize + Math.Sign(delta) * SizeStep, PointerFrame.MinSize, PointerFrame.MaxSize);
				Send();
				return true;
			}
			default:
			{
				// All other buttons are blocked so nothing is clicked by accident
				return true;
			}
		}
	}

	/// <summary>Mouse handling while picking a window: the next left click picks, a right click cancels.</summary>
	private bool OnPickMouse(uint msg, in MSLLHOOKSTRUCT info)
	{
		// Movement passes through so the user can aim normally
		switch (msg)
		{
			case User32.WM_MOUSEMOVE:
			{
				return false;
			}
			case User32.WM_LBUTTONUP:
			{
				// The pick happens on release so the matching button-up is also consumed
				var root = RootWindowAt(info.pt);
				var picked = m_on_pick;
				m_ui.Post(() =>
				{
					// Clicking the desktop or this app cancels rather than picking nothing
					Cancel();
					if (root != 0)
						picked?.Invoke(root);
				});
				return true;
			}
			case User32.WM_RBUTTONUP:
			{
				m_ui.Post(Cancel);
				return true;
			}
			default:
			{
				return true;
			}
		}
	}

	/// <summary>Mouse handling while drawing a frame: drag with the left button inside a window.</summary>
	private bool OnDrawMouse(uint msg, in MSLLHOOKSTRUCT info)
	{
		// Only the left-button drag matters; everything else except movement is blocked
		switch (msg)
		{
			case User32.WM_MOUSEMOVE:
			{
				// Show the rectangle being drawn, clamped to the window it started in
				if (m_drawing)
				{
					var rc = DrawRect(info.pt);
					m_ui.Post(() => ShowDrawRect(rc));
				}
				return false;
			}
			case User32.WM_LBUTTONDOWN:
			{
				// The drag defines a frame for the window it starts in
				m_draw_window = RootWindowAt(info.pt);
				m_draw_start = info.pt;
				m_drawing = m_draw_window != 0;
				return true;
			}
			case User32.WM_LBUTTONUP:
			{
				// Store the frame as fractions of the client area; a tiny drag clears the window's frame instead
				if (m_drawing)
				{
					var rc = DrawRect(info.pt);
					var hwnd = m_draw_window;
					m_ui.Post(() => FinishFrame(hwnd, rc));
				}
				m_drawing = false;
				return true;
			}
			case User32.WM_RBUTTONUP:
			{
				m_ui.Post(Cancel);
				return true;
			}
			default:
			{
				return true;
			}
		}
	}

	/// <summary>The rectangle from the drag start to 'pt', limited to the drag window's client area.</summary>
	private RECT DrawRect(POINT pt)
	{
		var client = User32.GetClientRectScreen(m_draw_window);
		var x0 = Math.Clamp(Math.Min(m_draw_start.X, pt.X), client.Left, client.Right);
		var y0 = Math.Clamp(Math.Min(m_draw_start.Y, pt.Y), client.Top, client.Bottom);
		var x1 = Math.Clamp(Math.Max(m_draw_start.X, pt.X), client.Left, client.Right);
		var y1 = Math.Clamp(Math.Max(m_draw_start.Y, pt.Y), client.Top, client.Bottom);
		return new RECT(x0, y0, x1, y1);
	}

	/// <summary>Outline the frame being drawn.</summary>
	private void ShowDrawRect(RECT rc)
	{
		// The mode may have been cancelled before this posted update ran
		if (m_mode != ETransmitMode.DrawFrame || rc.IsEmpty)
			return;

		var colour = m_relay.Snapshot.Self is { } self ? Palette.Rgb(self.Slot) : Overlay.AgentColour;
		m_border.Show(rc, colour, 2);
	}

	/// <summary>Store a drawn frame for 'hwnd' and leave draw mode.</summary>
	private void FinishFrame(nint hwnd, RECT rc)
	{
		// Frames are relative to the client area; small accidental drags remove the frame
		var client = User32.GetClientRectScreen(hwnd);
		if (client.IsEmpty)
		{
			Cancel();
			return;
		}

		if (rc.Width < 16 || rc.Height < 16)
			m_frames.Remove(hwnd);
		else
			m_frames[hwnd] = (
				(rc.Left - client.Left) / (double)client.Width,
				(rc.Top - client.Top) / (double)client.Height,
				(rc.Right - client.Left) / (double)client.Width,
				(rc.Bottom - client.Top) / (double)client.Height);

		Cancel();
	}

	/// <summary>Low-level keyboard hook.</summary>
	[UnmanagedCallersOnly]
	private static nint KeyboardHook(int code, nint wparam, nint lparam)
	{
		// Exceptions must not cross the native boundary
		try
		{
			if (code == User32.HC_ACTION && s_instance is { } self && self.OnKey((uint)wparam, *(KBDLLHOOKSTRUCT*)lparam))
				return 1;
		}
		catch (Exception ex)
		{
			Program.Crash(ex);
		}
		return User32.CallNextHookEx(0, code, wparam, lparam);
	}

	/// <summary>Low-level mouse hook.</summary>
	[UnmanagedCallersOnly]
	private static nint MouseHook(int code, nint wparam, nint lparam)
	{
		// Exceptions must not cross the native boundary
		try
		{
			if (code == User32.HC_ACTION && s_instance is { } self && self.OnMouse((uint)wparam, *(MSLLHOOKSTRUCT*)lparam))
				return 1;
		}
		catch (Exception ex)
		{
			Program.Crash(ex);
		}
		return User32.CallNextHookEx(0, code, wparam, lparam);
	}
}
