using OneBlindMouse.Core;
using OneBlindMouse.Windows.Native;
using OneBlindMouse.Windows.Ui;

namespace OneBlindMouse.Windows;

/// <summary>
/// The notification-area icon. It also owns a hidden window that receives shell, display and session-lock notifications.
/// UI thread only.
/// </summary>
internal sealed unsafe class TrayIcon : NativeWindow
{
	private const uint WM_TRAY = User32.WM_APP + 2;
	private const int SM_CXSMICON = 49;

	// Explorer broadcasts this when the taskbar is recreated (e.g. after a crash), and all icons must be added again
	private readonly uint m_taskbar_created = User32.RegisterWindowMessageW("TaskbarCreated");
	private nint m_icon;
	private string m_tip = AppConfig.ProductName;
	private bool m_added;

	/// <summary>Create the icon.</summary>
	public TrayIcon()
	{
		// A hidden top-level window is needed (not message-only) because TaskbarCreated is only broadcast to top-level windows
		CreateWindow("OBM.Tray", 0, User32.WS_POPUP, AppConfig.Title(AppConfig.ProductName), new RECT(0, 0, 0, 0));
		WtsApi32.WTSRegisterSessionNotification(Handle, WtsApi32.NOTIFY_FOR_THIS_SESSION);
		SetAppearance(0x808080, online: false);
		Add();
	}

	/// <summary>Raised when the icon is clicked.</summary>
	public event Action? Clicked;

	/// <summary>Raised when the context menu is requested, with the screen position to show it at.</summary>
	public event Action<POINT>? MenuRequested;

	/// <summary>Raised when the workstation is locked.</summary>
	public event Action? SessionLocked;

	/// <summary>Raised when the display layout changes.</summary>
	public event Action? DisplayChanged;

	/// <summary>Redraw the icon in this user's colour, greyed when offline.</summary>
	public void SetAppearance(uint rgb, bool online)
	{
		// Replace the icon handle, freeing the old one after the shell has the new one
		var old = m_icon;
		m_icon = DrawIcon(online ? rgb : 0x9A9A9A, online);
		Modify(Shell32.NIF_ICON);
		if (old != 0)
			User32.DestroyIcon(old);
	}

	/// <summary>Set the hover text.</summary>
	public void SetTip(string tip)
	{
		m_tip = tip.Truncate(127);
		Modify(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
	}

	/// <summary>Show a notification balloon (a toast on Windows 10+).</summary>
	public void ShowBalloon(string title, string text, ENotice level)
	{
		// The shell limits the text lengths
		var nid = Data(Shell32.NIF_INFO);
		Copy(title.Truncate(63), nid.szInfoTitle, 64);
		Copy(text.Truncate(255), nid.szInfo, 256);
		nid.dwInfoFlags = level switch
		{
			ENotice.Info => Shell32.NIIF_INFO | Shell32.NIIF_NOSOUND,
			ENotice.Warning => Shell32.NIIF_WARNING,
			ENotice.Error => Shell32.NIIF_ERROR,
			_ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
		};
		Shell32.Shell_NotifyIconW(Shell32.NIM_MODIFY, &nid);
	}

	/// <summary>Show 'menu' at 'pt' and run the chosen item's action.</summary>
	public void ShowMenu(Menu menu, POINT pt)
	{
		// The owner must be foreground or the menu won't close when the user clicks elsewhere
		var actions = new List<Action>();
		var hmenu = menu.Build(actions);
		try
		{
			User32.SetForegroundWindow(Handle);
			var id = User32.TrackPopupMenu(hmenu, User32.TPM_RIGHTBUTTON | User32.TPM_RETURNCMD | User32.TPM_NONOTIFY, pt.X, pt.Y, 0, Handle, 0);
			User32.PostMessageW(Handle, User32.WM_NULL, 0, 0);
			if (id > 0 && id <= actions.Count)
				actions[id - 1]();
		}
		finally
		{
			User32.DestroyMenu(hmenu);
		}
	}

	/// <inheritdoc/>
	public override void Dispose()
	{
		// Remove the icon before the window so it doesn't linger in the notification area
		if (m_added)
		{
			var nid = Data(0);
			Shell32.Shell_NotifyIconW(Shell32.NIM_DELETE, &nid);
			m_added = false;
		}
		if (Handle != 0)
			WtsApi32.WTSUnRegisterSessionNotification(Handle);

		base.Dispose();
		if (m_icon != 0)
			User32.DestroyIcon(m_icon);

		m_icon = 0;
	}

	/// <inheritdoc/>
	protected override nint WndProc(uint msg, nint wparam, nint lparam)
	{
		// The explorer restart message has a run-time id, so it can't be a switch case
		if (msg == m_taskbar_created && m_taskbar_created != 0)
		{
			m_added = false;
			Add();
			return 0;
		}

		switch (msg)
		{
			case WM_TRAY:
			{
				// With version 4 the event is in the low word of lParam and the anchor point is in wParam
				var evt = (uint)User32.LoWord(lparam);
				switch (evt)
				{
					case Shell32.NIN_SELECT:
					case Shell32.NIN_KEYSELECT:
					{
						Clicked?.Invoke();
						break;
					}
					case User32.WM_CONTEXTMENU:
					{
						MenuRequested?.Invoke(new POINT(User32.LoWord(wparam), User32.HiWord(wparam)));
						break;
					}
				}
				return 0;
			}
			case User32.WM_WTSSESSION_CHANGE:
			{
				if ((int)wparam == WtsApi32.WTS_SESSION_LOCK)
					SessionLocked?.Invoke();

				return 0;
			}
			case User32.WM_DISPLAYCHANGE:
			case User32.WM_DPICHANGED:
			{
				DisplayChanged?.Invoke();
				return base.WndProc(msg, wparam, lparam);
			}
			default:
			{
				return base.WndProc(msg, wparam, lparam);
			}
		}
	}

	/// <summary>Add the icon to the notification area and opt in to version 4 behaviour.</summary>
	private void Add()
	{
		var nid = Data(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
		m_added = Shell32.Shell_NotifyIconW(Shell32.NIM_ADD, &nid);
		nid.uTimeoutOrVersion = Shell32.NOTIFYICON_VERSION_4;
		Shell32.Shell_NotifyIconW(Shell32.NIM_SETVERSION, &nid);
	}

	/// <summary>Update parts of the icon. Before the icon exists the change is just remembered.</summary>
	private void Modify(uint flags)
	{
		if (!m_added)
			return;

		var nid = Data(flags);
		Shell32.Shell_NotifyIconW(Shell32.NIM_MODIFY, &nid);
	}

	/// <summary>Icon data with the current icon and tip.</summary>
	private NOTIFYICONDATAW Data(uint flags)
	{
		var nid = new NOTIFYICONDATAW
		{
			cbSize = (uint)sizeof(NOTIFYICONDATAW),
			hWnd = Handle,
			uID = 1,
			uFlags = flags,
			uCallbackMessage = WM_TRAY,
			hIcon = m_icon,
		};
		Copy(m_tip, nid.szTip, 128);
		return nid;
	}

	/// <summary>Copy a string into a fixed, null-terminated buffer.</summary>
	private static void Copy(string text, char* dst, int capacity)
	{
		var len = Math.Min(text.Length, capacity - 1);
		text.AsSpan(0, len).CopyTo(new Span<char>(dst, capacity));
		dst[len] = '\0';
	}

	/// <summary>Draw the tray icon: an arrow pointer in the given colour.</summary>
	private static nint DrawIcon(uint rgb, bool online)
	{
		// Draw at the system's small icon size so it stays sharp at any DPI
		var size = Math.Max(16, User32.GetSystemMetrics(SM_CXSMICON));
		var s = size / 16f;
		using var canvas = new Canvas(size, size);
		ReadOnlySpan<PointF> arrow =
		[
			new(2.5f * s, 1.5f * s),
			new(2.5f * s, 13.5f * s),
			new(5.6f * s, 10.6f * s),
			new(7.8f * s, 15f * s),
			new(9.8f * s, 14f * s),
			new(7.7f * s, 9.8f * s),
			new(12f * s, 9.8f * s),
		];
		canvas.FillPolygon(arrow, 0xFF000000 | rgb);
		canvas.DrawPolygon(arrow, online ? 0xFFFFFFFF : 0xFF404040, Math.Max(1f, s));
		Gdip.GdipCreateHICONFromBitmap(canvas.Bitmap, out var icon);
		return icon;
	}
}

/// <summary>A popup menu description. Items run their action when chosen.</summary>
internal sealed class Menu
{
	private readonly List<(string? Text, Action? Action, Menu? Sub, bool Checked, bool Enabled)> m_items = [];

	/// <summary>Add an item.</summary>
	public Menu Item(string text, Action action, bool is_checked = false, bool enabled = true)
	{
		m_items.Add((text, action, null, is_checked, enabled));
		return this;
	}

	/// <summary>Add a disabled line of information.</summary>
	public Menu Label(string text)
	{
		m_items.Add((text, null, null, false, false));
		return this;
	}

	/// <summary>Add a submenu. Empty submenus are shown disabled.</summary>
	public Menu Sub(string text, Menu sub)
	{
		m_items.Add((text, null, sub, false, sub.m_items.Count != 0));
		return this;
	}

	/// <summary>Add a separator.</summary>
	public Menu Separator()
	{
		m_items.Add((null, null, null, false, true));
		return this;
	}

	/// <summary>Create the native menu. Command ids are 1-based indexes into 'actions'.</summary>
	public nint Build(List<Action> actions)
	{
		// Submenus are owned by their parent and destroyed with it
		var hmenu = User32.CreatePopupMenu();
		foreach (var (text, action, sub, is_checked, enabled) in m_items)
		{
			var flags = (is_checked ? User32.MF_CHECKED : 0) | (enabled ? 0 : User32.MF_GRAYED);
			if (text is null)
			{
				User32.AppendMenuW(hmenu, User32.MF_SEPARATOR, 0, null);
			}
			else if (sub is not null)
			{
				User32.AppendMenuW(hmenu, User32.MF_POPUP | flags, (nuint)sub.Build(actions), Escape(text));
			}
			else
			{
				// Labels get a command id too, but being disabled they can never be chosen
				actions.Add(action ?? (() => { }));
				User32.AppendMenuW(hmenu, User32.MF_STRING | flags, (nuint)actions.Count, Escape(text));
			}
		}
		return hmenu;
	}

	/// <summary>Stop '&amp;' in names from becoming mnemonics.</summary>
	private static string Escape(string text)
	{
		return text.Replace("&", "&&");
	}
}
