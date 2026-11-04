using System.Runtime.InteropServices;

namespace OneBlindMouse.Windows.Native;

// Win32 declarations used by the app. Only what is needed is declared. All coordinates are physical pixels (the process is per-monitor DPI aware).

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
	public int X;
	public int Y;

	public POINT(int x, int y)
	{
		X = x;
		Y = y;
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
	public int CX;
	public int CY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
	public int Left;
	public int Top;
	public int Right;
	public int Bottom;

	public RECT(int l, int t, int r, int b)
	{
		Left = l;
		Top = t;
		Right = r;
		Bottom = b;
	}

	public readonly int Width => Right - Left;
	public readonly int Height => Bottom - Top;
	public readonly bool IsEmpty => Width <= 0 || Height <= 0;

	/// <summary>True if (x,y) is inside the rectangle (right and bottom edges excluded).</summary>
	public readonly bool Contains(int x, int y)
	{
		return x >= Left && x < Right && y >= Top && y < Bottom;
	}

	public override readonly string ToString()
	{
		return $"({Left},{Top} {Width}x{Height})";
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
	public nint hwnd;
	public uint message;
	public nint wParam;
	public nint lParam;
	public uint time;
	public POINT pt;
	public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
	public uint cbSize;
	public uint style;
	public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
	public int cbClsExtra;
	public int cbWndExtra;
	public nint hInstance;
	public nint hIcon;
	public nint hCursor;
	public nint hbrBackground;
	public char* lpszMenuName;
	public char* lpszClassName;
	public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
	public uint cbSize;
	public nint hWnd;
	public uint uID;
	public uint uFlags;
	public uint uCallbackMessage;
	public nint hIcon;
	public fixed char szTip[128];
	public uint dwState;
	public uint dwStateMask;
	public fixed char szInfo[256];
	public uint uTimeoutOrVersion;
	public fixed char szInfoTitle[64];
	public uint dwInfoFlags;
	public Guid guidItem;
	public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEXW
{
	public uint cbSize;
	public RECT rcMonitor;
	public RECT rcWork;
	public uint dwFlags;
	public fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
	public uint vkCode;
	public uint scanCode;
	public uint flags;
	public uint time;
	public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
	public POINT pt;
	public uint mouseData;
	public uint flags;
	public uint time;
	public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
	public uint biSize;
	public int biWidth;
	public int biHeight;
	public ushort biPlanes;
	public ushort biBitCount;
	public uint biCompression;
	public uint biSizeImage;
	public int biXPelsPerMeter;
	public int biYPelsPerMeter;
	public uint biClrUsed;
	public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
	public byte BlendOp;
	public byte BlendFlags;
	public byte SourceConstantAlpha;
	public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT
{
	public nint hdc;
	public int fErase;
	public RECT rcPaint;
	public int fRestore;
	public int fIncUpdate;
	public fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
	public int fIcon;
	public int xHotspot;
	public int yHotspot;
	public nint hbmMask;
	public nint hbmColor;
}

internal static unsafe partial class User32
{
	public const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
	public const uint WS_TABSTOP = 0x00010000, WS_CLIPCHILDREN = 0x02000000, WS_BORDER = 0x00800000;
	public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
	public const uint WS_EX_CONTROLPARENT = 0x10000, WS_EX_CLIENTEDGE = 0x200;

	public const uint ES_AUTOHSCROLL = 0x80, ES_READONLY = 0x800, ES_CENTER = 0x1, ES_UPPERCASE = 0x8;
	public const uint BS_PUSHBUTTON = 0x0, BS_DEFPUSHBUTTON = 0x1, SS_LEFT = 0x0, SS_NOPREFIX = 0x80;

	public const uint WM_DESTROY = 0x0002, WM_ACTIVATE = 0x0006, WM_CLOSE = 0x0010, WM_SETCURSOR = 0x0020, WM_SETFONT = 0x0030;
	public const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
	public const uint WM_COMMAND = 0x0111, WM_TIMER = 0x0113, WM_CTLCOLOREDIT = 0x0133, WM_CTLCOLORSTATIC = 0x0138, WM_CTLCOLORBTN = 0x0135, WM_CTLCOLORDLG = 0x0136;
	public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
	public const uint WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
	public const uint WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDBLCLK = 0x0206;
	public const uint WM_DPICHANGED = 0x02E0, WM_WTSSESSION_CHANGE = 0x02B1, WM_DISPLAYCHANGE = 0x007E, WM_CONTEXTMENU = 0x007B, WM_NULL = 0x0000;
	public const uint WM_APP = 0x8000, WM_NCDESTROY = 0x0082, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084;
	public const uint LWA_ALPHA = 0x2;
	public const int IDOK = 1, IDCANCEL = 2;
	public const int COLOR_BTNFACE = 15;
	public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
	public const uint EM_SETCUEBANNER = 0x1501;
	public const uint WM_MOUSEACTIVATE = 0x0021;
	public const int MA_NOACTIVATE = 3;
	public const uint EM_SETSEL = 0x00B1, EM_LIMITTEXT = 0x00C5;
	public const uint BN_CLICKED = 0, EN_CHANGE = 0x0300;

	public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;
	public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200;
	public static readonly nint HWND_TOPMOST = -1, HWND_MESSAGE = -3;

	public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, HC_ACTION = 0;
	public const uint LLKHF_INJECTED = 0x10;

	public const uint ULW_ALPHA = 0x2;
	public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

	public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
	public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

	public const int IDC_ARROW = 32512, IDC_CROSS = 32515;
	public const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
	public const uint GA_ROOT = 2;
	public const uint CF_UNICODETEXT = 13;
	public const uint SPI_GETWORKAREA = 0x30;
	public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
	public const int VK_ESCAPE = 0x1B, VK_RETURN = 0x0D, VK_RCONTROL = 0xA3, VK_RSHIFT = 0xA1, VK_RMENU = 0xA5, VK_APPS = 0x5D, VK_SCROLL = 0x91, VK_PAUSE = 0x13;
	public const uint MB_OK = 0, MB_YESNO = 4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30;
	public const int IDYES = 6;

	[LibraryImport("user32.dll", SetLastError = true)]
	public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

	[LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	public static partial nint CreateWindowExW(uint ex_style, string class_name, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DestroyWindow(nint hwnd);

	[LibraryImport("user32.dll")]
	public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wparam, nint lparam);

	[LibraryImport("user32.dll")]
	public static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool TranslateMessage(in MSG msg);

	[LibraryImport("user32.dll")]
	public static partial nint DispatchMessageW(in MSG msg);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsDialogMessageW(nint hwnd, in MSG msg);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PostMessageW(nint hwnd, uint msg, nint wparam, nint lparam);

	[LibraryImport("user32.dll")]
	public static partial nint SendMessageW(nint hwnd, uint msg, nint wparam, nint lparam);

	[LibraryImport("user32.dll")]
	public static partial void PostQuitMessage(int code);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ShowWindow(nint hwnd, int cmd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool UpdateLayeredWindow(nint hwnd, nint hdc_dst, POINT* pt_dst, SIZE* size, nint hdc_src, POINT* pt_src, uint key, BLENDFUNCTION* blend, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

	[LibraryImport("user32.dll")]
	public static partial nint GetDC(nint hwnd);

	[LibraryImport("user32.dll")]
	public static partial int ReleaseDC(nint hwnd, nint hdc);

	[LibraryImport("user32.dll")]
	public static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool KillTimer(nint hwnd, nuint id);

	[LibraryImport("user32.dll", SetLastError = true)]
	public static partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint thread);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool UnhookWindowsHookEx(nint hook);

	[LibraryImport("user32.dll")]
	public static partial nint CallNextHookEx(nint hook, int code, nint wparam, nint lparam);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetCursorPos(out POINT pt);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ClipCursor(RECT* rect);

	[LibraryImport("user32.dll")]
	public static partial nint WindowFromPoint(POINT pt);

	[LibraryImport("user32.dll")]
	public static partial nint GetAncestor(nint hwnd, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetClientRect(nint hwnd, out RECT rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ClientToScreen(nint hwnd, ref POINT pt);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetWindowRect(nint hwnd, out RECT rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsWindow(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsIconic(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsWindowVisible(nint hwnd);

	[LibraryImport("user32.dll")]
	public static partial int GetWindowTextW(nint hwnd, char* buf, int max);

	[LibraryImport("user32.dll")]
	public static partial int GetWindowTextLengthW(nint hwnd);

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetWindowTextW(nint hwnd, string text);

	[LibraryImport("user32.dll")]
	public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> proc, nint data);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetMonitorInfoW(nint monitor, MONITORINFOEXW* info);

	[LibraryImport("user32.dll")]
	public static partial nint MonitorFromPoint(POINT pt, uint flags);

	[LibraryImport("user32.dll")]
	public static partial nint CreatePopupMenu();

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

	[LibraryImport("user32.dll")]
	public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DestroyMenu(nint menu);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetForegroundWindow(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool AllowSetForegroundWindow(uint process_id);
	public const uint ASFW_ANY = 0xFFFFFFFF;

	[LibraryImport("user32.dll")]
	public static partial nint LoadCursorW(nint instance, nint name);

	[LibraryImport("user32.dll")]
	public static partial nint SetCursor(nint cursor);

	[LibraryImport("user32.dll")]
	public static partial nint SetFocus(nint hwnd);

	[LibraryImport("user32.dll")]
	public static partial nint GetFocus();

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool OpenClipboard(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EmptyClipboard();

	[LibraryImport("user32.dll")]
	public static partial nint SetClipboardData(uint format, nint mem);

	[LibraryImport("user32.dll")]
	public static partial nint GetClipboardData(uint format);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool CloseClipboard();

	[LibraryImport("user32.dll")]
	public static partial uint GetDpiForWindow(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SystemParametersInfoW(uint action, uint param, void* pv, uint win_ini);

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial uint RegisterWindowMessageW(string name);

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);

	[LibraryImport("user32.dll")]
	public static partial nint CreateIconIndirect(ICONINFO* info);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DestroyIcon(nint icon);

	[LibraryImport("user32.dll")]
	public static partial nint GetSysColorBrush(int index);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

	[LibraryImport("user32.dll")]
	public static partial short GetAsyncKeyState(int vk);

	[LibraryImport("user32.dll")]
	public static partial nint GetForegroundWindow();

	[LibraryImport("user32.dll")]
	public static partial int GetSystemMetrics(int index);

	[LibraryImport("user32.dll")]
	public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);

	[LibraryImport("user32.dll")]
	public static partial int FillRect(nint hdc, RECT* rect, nint brush);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool InvalidateRect(nint hwnd, RECT* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

	[LibraryImport("user32.dll")]
	public static partial uint GetDpiForSystem();

	[LibraryImport("user32.dll")]
	public static partial int GetClassNameW(nint hwnd, char* buf, int max);

	[LibraryImport("user32.dll")]
	public static partial nint GetShellWindow();

	/// <summary>Read a window's class name.</summary>
	public static string GetClassName(nint hwnd)
	{
		// Class names are limited to 256 characters
		var buf = stackalloc char[257];
		var len = GetClassNameW(hwnd, buf, 257);
		return new string(buf, 0, Math.Max(len, 0));
	}

	/// <summary>The bounding rectangle of all monitors.</summary>
	public static RECT VirtualScreen()
	{
		// The virtual screen origin can be negative when a monitor is left of or above the primary
		var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
		var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
		return new RECT(x, y, x + GetSystemMetrics(SM_CXVIRTUALSCREEN), y + GetSystemMetrics(SM_CYVIRTUALSCREEN));
	}

	/// <summary>Read a window's title (up to 255 characters).</summary>
	public static string GetWindowText(nint hwnd)
	{
		// Titles are short; a fixed buffer avoids a separate length query
		var buf = stackalloc char[256];
		var len = GetWindowTextW(hwnd, buf, 256);
		return new string(buf, 0, Math.Max(len, 0));
	}

	/// <summary>The client area of a window in screen coordinates.</summary>
	public static RECT GetClientRectScreen(nint hwnd)
	{
		// Client rects are relative to the client origin, so offset by its screen position
		if (!GetClientRect(hwnd, out var rc))
			return default;

		var pt = new POINT(0, 0);
		ClientToScreen(hwnd, ref pt);
		return new RECT(pt.X, pt.Y, pt.X + rc.Width, pt.Y + rc.Height);
	}

	/// <summary>The low word of a message parameter as a signed value (e.g. x in a mouse message).</summary>
	public static int LoWord(nint value)
	{
		return (short)(value & 0xFFFF);
	}

	/// <summary>The high word of a message parameter as a signed value (e.g. y in a mouse message).</summary>
	public static int HiWord(nint value)
	{
		return (short)((value >> 16) & 0xFFFF);
	}
}

internal static unsafe partial class Gdi32
{
	public const uint DIB_RGB_COLORS = 0;
	public const int TRANSPARENT = 1;
	public const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
	public const uint DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5;

	[LibraryImport("gdi32.dll")]
	public static partial nint CreateCompatibleDC(nint hdc);

	[LibraryImport("gdi32.dll")]
	public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* bmi, uint usage, out nint bits, nint section, uint offset);

	[LibraryImport("gdi32.dll")]
	public static partial nint SelectObject(nint hdc, nint obj);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DeleteObject(nint obj);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DeleteDC(nint hdc);

	[LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint out_precision, uint clip_precision, uint quality, uint pitch, string face);

	[LibraryImport("gdi32.dll")]
	public static partial int SetBkMode(nint hdc, int mode);

	[LibraryImport("gdi32.dll")]
	public static partial uint SetTextColor(nint hdc, uint colour);

	[LibraryImport("gdi32.dll")]
	public static partial nint CreateSolidBrush(uint colour);

	[LibraryImport("gdi32.dll")]
	public static partial nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
}

internal static unsafe partial class Shell32
{
	public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
	public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
	public const uint NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_ERROR = 3, NIIF_NOSOUND = 0x10;
	public const uint NOTIFYICON_VERSION_4 = 4;
	public const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

	[LibraryImport("shell32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
}

internal static partial class Kernel32
{
	public const uint GMEM_MOVEABLE = 0x2;

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial nint GetModuleHandleW(string? name);

	[LibraryImport("kernel32.dll")]
	public static partial nint GlobalAlloc(uint flags, nuint bytes);

	[LibraryImport("kernel32.dll")]
	public static partial nint GlobalLock(nint mem);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GlobalUnlock(nint mem);

	[LibraryImport("kernel32.dll")]
	public static partial nint GlobalFree(nint mem);
}

internal static partial class Shcore
{
	public const int MDT_EFFECTIVE_DPI = 0;

	[LibraryImport("shcore.dll")]
	public static partial int GetDpiForMonitor(nint monitor, int type, out uint dpi_x, out uint dpi_y);
}

internal static partial class WtsApi32
{
	public const uint NOTIFY_FOR_THIS_SESSION = 0;
	public const int WTS_SESSION_LOCK = 0x7;

	[LibraryImport("wtsapi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);

	[LibraryImport("wtsapi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool WTSUnRegisterSessionNotification(nint hwnd);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINTRUST_FILE_INFO
{
	public uint cbStruct;
	public nint pcwszFilePath;
	public nint hFile;
	public nint pgKnownSubject;
}

/// <summary>The leading fields of CRYPT_PROVIDER_SGNR; only ever read through a pointer from wintrust.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CRYPT_PROVIDER_SGNR
{
	public uint cbStruct;
	public uint sftVerifyAsOfLow;
	public uint sftVerifyAsOfHigh;
	public uint csCertChain;
	public CRYPT_PROVIDER_CERT* pasCertChain;
}

/// <summary>The leading fields of CRYPT_PROVIDER_CERT; only ever read through a pointer from wintrust.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CRYPT_PROVIDER_CERT
{
	public uint cbStruct;
	public nint pCert;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINTRUST_DATA
{
	public uint cbStruct;
	public nint pPolicyCallbackData;
	public nint pSIPClientData;
	public uint dwUIChoice;
	public uint fdwRevocationChecks;
	public uint dwUnionChoice;
	public nint pFile;
	public uint dwStateAction;
	public nint hWVTStateData;
	public nint pwszURLReference;
	public uint dwProvFlags;
	public uint dwUIContext;
	public nint pSignatureSettings;
}

internal static unsafe partial class WinTrust
{
	public const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;

	/// <summary>The standard Authenticode policy.</summary>
	public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

	[LibraryImport("wintrust.dll")]
	public static partial int WinVerifyTrust(nint hwnd, Guid* action, WINTRUST_DATA* data);

	[LibraryImport("wintrust.dll")]
	public static partial nint WTHelperProvDataFromStateData(nint state);

	[LibraryImport("wintrust.dll")]
	public static partial CRYPT_PROVIDER_SGNR* WTHelperGetProvSignerFromChain(nint prov_data, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counter_signer, uint counter_signer_index);
}

internal static unsafe partial class Crypt32
{
	public const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;

	[LibraryImport("crypt32.dll")]
	public static partial uint CertGetNameStringW(nint cert, uint type, uint flags, nint type_para, char* name, uint name_len);
}
