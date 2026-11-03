using OneBlindMouse.Core;
using OneBlindMouse.Windows.Native;
using OneBlindMouse.Windows.Ui;

namespace OneBlindMouse.Windows;

/// <summary>
/// The small window shown from the tray icon: this user's code, a box to join someone else's code, the member list and the user's name.
/// It hides itself when it loses focus, like a tray flyout. UI thread only.
/// </summary>
internal sealed unsafe class SessionPopup : NativeWindow
{
	// Control ids
	private const int IdCode = 100, IdCopy = 101, IdCopyLink = 102, IdJoinText = 103, IdJoin = 104, IdMembers = 105;
	private const int IdName = 106, IdRename = 107, IdLeave = 108, IdNewCode = 109, IdHint = 110, IdTitle = 111, IdCodeLabel = 112, IdJoinLabel = 113, IdNameLabel = 114, IdClose = 115;

	// Layout at 96 DPI
	private const int Width96 = 340, Pad96 = 12, Row96 = 26, Gap96 = 8, Button96 = 80;

	private readonly Dictionary<int, nint> m_controls = [];
	private nint m_font;
	private nint m_bold;
	private nint m_big;
	private uint m_dpi;
	private long m_hidden_at;
	private SessionSnapshot? m_snap;

	/// <summary>Create the (hidden) popup.</summary>
	public SessionPopup()
	{
		// A tool window stays out of the taskbar and Alt+Tab; CONTROLPARENT lets Tab move between the controls
		const uint ex = User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST | User32.WS_EX_CONTROLPARENT;
		CreateWindow("OBM.Popup", ex, User32.WS_POPUP | User32.WS_BORDER | User32.WS_CLIPCHILDREN, AppConfig.Title(AppConfig.ProductName), new RECT(0, 0, 1, 1), background: User32.GetSysColorBrush(User32.COLOR_BTNFACE));

		// Create the controls; their positions are set by Layout. The heading omits the test tag, which only labels the window title
		Add("STATIC", AppConfig.ProductName, User32.SS_LEFT | User32.SS_NOPREFIX, IdTitle);
		Add("STATIC", "Your code — share it with the people you want to point with you:", User32.SS_LEFT, IdCodeLabel);
		Add("EDIT", "", User32.ES_READONLY | User32.ES_CENTER | User32.WS_TABSTOP, IdCode, User32.WS_EX_CLIENTEDGE);
		Add("BUTTON", "Copy", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdCopy);
		Add("BUTTON", "Copy link", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdCopyLink);
		Add("STATIC", "Join someone else's session:", User32.SS_LEFT, IdJoinLabel);
		Add("EDIT", "", User32.ES_AUTOHSCROLL | User32.WS_TABSTOP, IdJoinText, User32.WS_EX_CLIENTEDGE);
		Add("BUTTON", "Join", User32.BS_DEFPUSHBUTTON | User32.WS_TABSTOP, IdJoin);
		Add("STATIC", "", User32.SS_LEFT | User32.SS_NOPREFIX, IdMembers);
		Add("STATIC", "Your name:", User32.SS_LEFT, IdNameLabel);
		Add("EDIT", "", User32.ES_AUTOHSCROLL | User32.WS_TABSTOP, IdName, User32.WS_EX_CLIENTEDGE);
		Add("BUTTON", "Set", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdRename);
		Add("BUTTON", "Leave session", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdLeave);
		Add("BUTTON", "New code", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdNewCode);
		Add("STATIC", "", User32.SS_LEFT | User32.SS_NOPREFIX, IdHint);
		Add("BUTTON", "Close", User32.BS_PUSHBUTTON | User32.WS_TABSTOP, IdClose);
		User32.SendMessageW(Ctl(IdName), User32.EM_LIMITTEXT, Protocol.MaxNameLength, 0);
		User32.SendMessageW(Ctl(IdJoinText), User32.EM_LIMITTEXT, 256, 0);
		fixed (char* cue = "Paste a code or link")
			User32.SendMessageW(Ctl(IdJoinText), User32.EM_SETCUEBANNER, 1, (nint)cue);
	}

	/// <summary>Raised when the user asks to join the text in the join box.</summary>
	public event Action<string>? JoinRequested;

	/// <summary>Raised when the user sets a new name.</summary>
	public event Action<string>? RenameRequested;

	/// <summary>Raised when the user clicks Leave.</summary>
	public event Action? LeaveRequested;

	/// <summary>Raised when the user clicks New code.</summary>
	public event Action? NewCodeRequested;

	/// <summary>True while the popup is on screen.</summary>
	public bool IsVisible => User32.IsWindowVisible(Handle);

	/// <summary>Show the popup, unless it was just hidden by the click that is now asking to show it.</summary>
	public void Toggle()
	{
		// Clicking the tray icon deactivates the popup (hiding it) just before the click arrives, so a very recent hide means "close"
		if (Environment.TickCount64 - m_hidden_at < 300)
			return;

		Show();
	}

	/// <summary>Show the popup in the centre of the primary monitor.</summary>
	public void Show()
	{
		// Offer a code from the clipboard so joining is just one click
		var join = Ctl(IdJoinText);
		if (User32.GetWindowTextLengthW(join) == 0 && Clipboard.GetText() is { } clip && SessionCode.TryParse(clip, out var code) && code != m_snap?.Code)
			User32.SetWindowTextW(join, code.Display);

		// Lay out for the primary monitor's DPI and centre it in that monitor's work area, where it is easy to find
		var mon = Monitors.Primary();
		var size = Layout(mon.Dpi);
		var work = mon.WorkArea;
		var x = work.Left + (work.Width - size.CX) / 2;
		var y = work.Top + (work.Height - size.CY) / 2;
		User32.SetWindowPos(Handle, User32.HWND_TOPMOST, x, y, size.CX, size.CY, User32.SWP_SHOWWINDOW);
		User32.SetForegroundWindow(Handle);
		User32.SetFocus(Ctl(User32.GetWindowTextLengthW(join) != 0 ? IdJoin : IdJoinText));
	}

	/// <summary>Hide the popup.</summary>
	public void Hide()
	{
		User32.ShowWindow(Handle, User32.SW_HIDE);
	}

	/// <summary>Show the current session state.</summary>
	public void Update(SessionSnapshot snap, string name, string hold_key)
	{
		// The code box shows the room's code, or the connection state when there isn't one
		m_snap = snap;
		var code_text = snap.Connection switch
		{
			EConnection.Online => snap.Code?.Display ?? "…",
			EConnection.Connecting => "Connecting…",
			EConnection.Offline => "Offline — retrying",
			_ => throw new ArgumentOutOfRangeException(nameof(snap.Connection), snap.Connection, null),
		};
		SetText(IdCode, code_text);
		User32.EnableWindow(Ctl(IdCopy), snap.Code is not null);
		User32.EnableWindow(Ctl(IdCopyLink), snap.Code is not null);
		User32.EnableWindow(Ctl(IdLeave), snap.IsShared || (snap.Code is not null && !snap.IsOwner));
		User32.EnableWindow(Ctl(IdNewCode), snap.IsOwner);

		// Members, with this user and the owner marked
		var lines = snap.Members.Count == 0
			? "Not connected"
			: snap.IsShared
				? "In this session:\n" + string.Join("\n", snap.Members.Select(m => $"  ● {m.Name} ({Palette.Name(m.Slot)}){(m.Id == snap.Me ? " — you" : string.Empty)}{(m.Id == snap.Owner ? " — owner" : string.Empty)}"))
				: "No one has joined yet.\n ";
		SetText(IdMembers, lines);

		// Only replace the name if the user isn't editing it
		if (User32.GetFocus() != Ctl(IdName))
			SetText(IdName, name);

		SetText(IdHint, $"Hold {hold_key} to point on their screen. Left-click: ripple and laser. Right-click: pulse. Wheel: size. Esc: cancel.");
	}

	/// <summary>The join box text.</summary>
	public void ClearJoin()
	{
		SetText(IdJoinText, string.Empty);
	}

	/// <inheritdoc/>
	public override void Dispose()
	{
		// Fonts are not owned by the controls
		base.Dispose();
		DeleteFonts();
	}

	/// <inheritdoc/>
	protected override nint WndProc(uint msg, nint wparam, nint lparam)
	{
		// Button clicks, Enter/Esc (IDOK/IDCANCEL from IsDialogMessage), and focus loss
		switch (msg)
		{
			case User32.WM_COMMAND:
			{
				OnCommand(User32.LoWord(wparam), User32.HiWord(wparam));
				return 0;
			}
			case User32.WM_ACTIVATE:
			{
				// Hide like a flyout when the user clicks elsewhere
				if (User32.LoWord(wparam) == 0 && IsVisible)
				{
					m_hidden_at = Environment.TickCount64;
					Hide();
				}
				return 0;
			}
			case User32.WM_CLOSE:
			{
				Hide();
				return 0;
			}
			default:
			{
				return base.WndProc(msg, wparam, lparam);
			}
		}
	}

	/// <summary>Handle a control command.</summary>
	private void OnCommand(int id, int notification)
	{
		// Only button clicks (and Enter/Esc, which arrive as IDOK/IDCANCEL) matter
		if (notification != User32.BN_CLICKED)
			return;

		switch (id)
		{
			case User32.IDOK:
			{
				// Enter applies whichever box has focus
				if (User32.GetFocus() == Ctl(IdName))
					RenameRequested?.Invoke(User32.GetWindowText(Ctl(IdName)));
				else
					JoinRequested?.Invoke(User32.GetWindowText(Ctl(IdJoinText)));

				break;
			}
			case User32.IDCANCEL:
			case IdClose:
			{
				Hide();
				break;
			}
			case IdJoin:
			{
				JoinRequested?.Invoke(User32.GetWindowText(Ctl(IdJoinText)));
				break;
			}
			case IdCopy:
			{
				if (m_snap?.Code is { } code)
					Clipboard.SetText(code.Display);

				break;
			}
			case IdCopyLink:
			{
				if (m_snap?.Code is { } code)
					Clipboard.SetText(AppConfig.JoinLink(code));

				break;
			}
			case IdRename:
			{
				RenameRequested?.Invoke(User32.GetWindowText(Ctl(IdName)));
				break;
			}
			case IdLeave:
			{
				LeaveRequested?.Invoke();
				break;
			}
			case IdNewCode:
			{
				NewCodeRequested?.Invoke();
				break;
			}
		}
	}

	/// <summary>Position the controls for 'dpi' and return the window size.</summary>
	private SIZE Layout(uint dpi)
	{
		// Fonts depend on DPI, so they are recreated when it changes
		if (dpi != m_dpi)
		{
			m_dpi = dpi;
			DeleteFonts();
			m_font = MakeFont(9, Gdi32.FW_NORMAL);
			m_bold = MakeFont(10, Gdi32.FW_SEMIBOLD);
			m_big = MakeFont(16, Gdi32.FW_SEMIBOLD);
			foreach (var (id, hwnd) in m_controls)
				User32.SendMessageW(hwnd, User32.WM_SETFONT, id switch { IdTitle => m_bold, IdCode => m_big, _ => m_font }, 1);
		}

		// One column of rows; buttons sit at the right end of their row
		var pad = Scale(Pad96);
		var gap = Scale(Gap96);
		var row = Scale(Row96);
		var line = Scale(16);
		var width = Scale(Width96);
		var inner = width - 2 * pad;
		var button = Scale(Button96);
		var y = pad;

		Place(IdTitle, pad, y, inner, line + Scale(2));
		y += line + gap;
		Place(IdCodeLabel, pad, y, inner, line * 2);
		y += line * 2 + Scale(2);
		Place(IdCode, pad, y, inner - 2 * (button + gap), Scale(34));
		Place(IdCopy, width - pad - 2 * button - gap, y + Scale(4), button, row);
		Place(IdCopyLink, width - pad - button, y + Scale(4), button, row);
		y += Scale(34) + gap * 2;
		Place(IdJoinLabel, pad, y, inner, line);
		y += line + Scale(2);
		Place(IdJoinText, pad, y, inner - button - gap, row);
		Place(IdJoin, width - pad - button, y, button, row);
		y += row + gap * 2;
		var members = line * Math.Max(2, (m_snap?.Members.Count ?? 0) + 1);
		Place(IdMembers, pad, y, inner, members);
		y += members + gap;
		Place(IdNameLabel, pad, y + Scale(5), Scale(70), line);
		Place(IdName, pad + Scale(72), y, inner - Scale(72) - button - gap, row);
		Place(IdRename, width - pad - button, y, button, row);
		y += row + gap;
		Place(IdLeave, pad, y, (inner - gap) / 2, row);
		Place(IdNewCode, pad + (inner + gap) / 2, y, (inner - gap) / 2, row);
		y += row + gap;
		Place(IdHint, pad, y, inner, line * 3);
		y += line * 3 + gap;
		Place(IdClose, width - pad - button, y, button, row);
		y += row + pad;

		// Add the border thickness so the client area is the laid-out size
		User32.GetWindowRect(Handle, out var wr);
		User32.GetClientRect(Handle, out var cr);
		return new SIZE { CX = width + wr.Width - cr.Width, CY = y + wr.Height - cr.Height };
	}

	/// <summary>Create a child control.</summary>
	private void Add(string cls, string text, uint style, int id, uint ex = 0)
	{
		var hwnd = User32.CreateWindowExW(ex, cls, text, User32.WS_CHILD | User32.WS_VISIBLE | style, 0, 0, 0, 0, Handle, id, Kernel32.GetModuleHandleW(null), 0);
		m_controls[id] = hwnd;
	}

	/// <summary>A control's handle.</summary>
	private nint Ctl(int id)
	{
		return m_controls[id];
	}

	/// <summary>Set a control's text if it changed (avoids flicker and caret resets).</summary>
	private void SetText(int id, string text)
	{
		var hwnd = Ctl(id);
		if (User32.GetWindowText(hwnd) != text)
			User32.SetWindowTextW(hwnd, text);
	}

	/// <summary>Move a control.</summary>
	private void Place(int id, int x, int y, int w, int h)
	{
		User32.SetWindowPos(Ctl(id), 0, x, y, w, h, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
	}

	/// <summary>Scale a 96-DPI length to the current DPI.</summary>
	private int Scale(int value)
	{
		return (int)Math.Round(value * (m_dpi == 0 ? 96 : m_dpi) / 96.0);
	}

	/// <summary>Create a Segoe UI font of the given point size for the current DPI.</summary>
	private nint MakeFont(int points, int weight)
	{
		var height = -(int)Math.Round(points * m_dpi / 72.0);
		return Gdi32.CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, Gdi32.DEFAULT_CHARSET, 0, 0, Gdi32.CLEARTYPE_QUALITY, 0, "Segoe UI");
	}

	/// <summary>Free the fonts.</summary>
	private void DeleteFonts()
	{
		foreach (var font in new[] { m_font, m_bold, m_big })
			if (font != 0)
				Gdi32.DeleteObject(font);

		m_font = m_bold = m_big = 0;
	}
}

/// <summary>Plain-text clipboard access.</summary>
internal static unsafe class Clipboard
{
	/// <summary>The clipboard text, or null if there is none (or the clipboard is busy).</summary>
	public static string? GetText()
	{
		// The clipboard is shared, so it may be briefly unavailable
		if (!User32.OpenClipboard(0))
			return null;

		try
		{
			var mem = User32.GetClipboardData(User32.CF_UNICODETEXT);
			if (mem == 0)
				return null;

			var ptr = Kernel32.GlobalLock(mem);
			if (ptr == 0)
				return null;

			try
			{
				// Bound the read so a huge clipboard doesn't cost anything; codes and links are short
				var text = new string((char*)ptr);
				return text.Length > 512 ? null : text;
			}
			finally
			{
				Kernel32.GlobalUnlock(mem);
			}
		}
		finally
		{
			User32.CloseClipboard();
		}
	}

	/// <summary>Replace the clipboard contents with 'text'.</summary>
	public static void SetText(string text)
	{
		// Retry briefly because another application may be holding the clipboard
		var opened = User32.OpenClipboard(0);
		for (var attempt = 0; attempt != 5 && !opened; ++attempt)
		{
			Thread.Sleep(20);
			opened = User32.OpenClipboard(0);
		}
		if (!opened)
			return;

		try
		{
			// The clipboard takes ownership of the memory on success
			User32.EmptyClipboard();
			var bytes = (nuint)((text.Length + 1) * sizeof(char));
			var mem = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, bytes);
			var ptr = (char*)Kernel32.GlobalLock(mem);
			text.AsSpan().CopyTo(new Span<char>(ptr, text.Length));
			ptr[text.Length] = '\0';
			Kernel32.GlobalUnlock(mem);
			if (User32.SetClipboardData(User32.CF_UNICODETEXT, mem) == 0)
				Kernel32.GlobalFree(mem);
		}
		finally
		{
			User32.CloseClipboard();
		}
	}
}
