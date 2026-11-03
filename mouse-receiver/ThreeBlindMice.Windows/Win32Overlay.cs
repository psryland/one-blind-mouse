using System.Runtime.InteropServices;

namespace ThreeBlindMice.Windows;

/// <summary>
/// Transparent, always-on-top, click-through overlay window using pure Win32 P/Invoke.
/// Covers the entire primary screen. Only drawn cursors are visible — the background
/// is keyed out via LWA_COLORKEY with magenta (#FF00FF).
/// </summary>
internal sealed class Win32Overlay : IDisposable
{
	// Background colour key — anything drawn in this colour is fully transparent
	private const uint ColorKeyRgb = 0x00FF00FF; // magenta in COLORREF (0x00BBGGRR)

	// Window style flags
	private const uint WS_POPUP = 0x80000000;
	private const uint WS_EX_LAYERED = 0x00080000;
	private const uint WS_EX_TRANSPARENT = 0x00000020;
	private const uint WS_EX_TOPMOST = 0x00000008;
	private const uint WS_EX_TOOLWINDOW = 0x00000080;
	private const uint WS_EX_NOACTIVATE = 0x08000000;

	private const int SW_SHOW = 5;
	private const uint LWA_COLORKEY = 0x00000001;
	private const uint LWA_ALPHA = 0x00000002;
	private const uint WM_DESTROY = 0x0002;
	private const uint WM_PAINT = 0x000F;
	private const uint WM_ERASEBKGND = 0x0014;
	private const uint WM_CLOSE = 0x0010;
	private const uint WM_APP_SHUTDOWN = 0x8001; // custom message for cross-thread shutdown

	// GDI constants
	private const int COLOR_WINDOW = 5;

	private IntPtr m_hwnd;
	private IntPtr m_brush;
	private Thread? m_thread;
	private volatile bool m_disposed;
	private readonly int m_monitor_left;
	private readonly int m_monitor_top;
	private readonly int m_monitor_width;
	private readonly int m_monitor_height;

	/// <summary>
	/// Callback invoked during WM_PAINT with (hdc, monitor_width, monitor_height).
	/// </summary>
	public Action<IntPtr, int, int>? On_Paint;

	// Delegate must be stored to prevent GC collection while the window is alive
	private readonly WndProcDelegate m_wnd_proc_delegate;

	/// <param name="left">Monitor left edge in virtual-screen coordinates.</param>
	/// <param name="top">Monitor top edge in virtual-screen coordinates.</param>
	/// <param name="width">Monitor width in pixels.</param>
	/// <param name="height">Monitor height in pixels.</param>
	public Win32Overlay(int left, int top, int width, int height)
	{
		m_monitor_left = left;
		m_monitor_top = top;
		m_monitor_width = width;
		m_monitor_height = height;
		m_wnd_proc_delegate = WndProc;
	}

	/// <summary>
	/// Creates the overlay window on a dedicated STA thread and runs the message pump.
	/// This method returns immediately — the window runs on its own thread.
	/// </summary>
	public void Start()
	{
		var ready = new ManualResetEventSlim(false);
		Exception? init_error = null;

		m_thread = new Thread(() =>
		{
			try
			{
				CreateOverlayWindow();
				ready.Set();
				RunMessagePump();
			}
			catch (Exception ex)
			{
				init_error = ex;
				ready.Set();
			}
		})
		{
			Name = "Win32Overlay",
			IsBackground = true,
		};
		m_thread.SetApartmentState(ApartmentState.STA);
		m_thread.Start();

		ready.Wait();
		if (init_error != null)
			throw new InvalidOperationException("Failed to create overlay window.", init_error);
	}

	/// <summary>
	/// Posts a repaint request to the overlay window. Safe to call from any thread.
	/// </summary>
	public void RequestRepaint()
	{
		if (m_hwnd != IntPtr.Zero)
			InvalidateRect(m_hwnd, IntPtr.Zero, true);
	}

	/// <summary>
	/// Cleanly shuts down the overlay window and waits for the message pump to exit.
	/// </summary>
	public void Shutdown()
	{
		if (m_hwnd != IntPtr.Zero)
		{
			// Post a custom message so the pump thread can destroy the window
			PostMessage(m_hwnd, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
		}
		m_thread?.Join(TimeSpan.FromSeconds(5));
	}

	public void Dispose()
	{
		if (m_disposed)
			return;

		m_disposed = true;
		Shutdown();

		if (m_brush != IntPtr.Zero)
		{
			DeleteObject(m_brush);
			m_brush = IntPtr.Zero;
		}
	}

	private void CreateOverlayWindow()
	{
		// Create the magenta brush used as the transparent background
		m_brush = CreateSolidBrush(ColorKeyRgb);

		var wc = new WNDCLASSEX
		{
			cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
			lpfnWndProc = Marshal.GetFunctionPointerForDelegate(m_wnd_proc_delegate),
			hInstance = GetModuleHandle(null),
			lpszClassName = "ThreeBlindMiceOverlay",
			hbrBackground = m_brush,
		};

		var atom = RegisterClassEx(ref wc);
		if (atom == 0)
			throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

		var ex_style = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

		m_hwnd = CreateWindowEx(
			ex_style,
			wc.lpszClassName,
			"Three Blind Mice Overlay",
			WS_POPUP,
			m_monitor_left, m_monitor_top,
			m_monitor_width, m_monitor_height,
			IntPtr.Zero,
			IntPtr.Zero,
			wc.hInstance,
			IntPtr.Zero);

		if (m_hwnd == IntPtr.Zero)
			throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

		// Key out the magenta background, and apply global alpha for a subtle frosted look
		SetLayeredWindowAttributes(m_hwnd, ColorKeyRgb, 230, LWA_COLORKEY | LWA_ALPHA);

		ShowWindow(m_hwnd, SW_SHOW);
		UpdateWindow(m_hwnd);
	}

	private static void RunMessagePump()
	{
		while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
		{
			TranslateMessage(ref msg);
			DispatchMessage(ref msg);
		}
	}

	private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w_param, IntPtr l_param)
	{
		switch (msg)
		{
			case WM_ERASEBKGND:
				// Handled — the background brush fills with the colour key automatically
				return (IntPtr)1;

			case WM_PAINT:
				var ps = new PAINTSTRUCT();
				var paint_hdc = BeginPaint(hwnd, ref ps);
				On_Paint?.Invoke(paint_hdc, m_monitor_width, m_monitor_height);
				EndPaint(hwnd, ref ps);
				return IntPtr.Zero;

			case WM_APP_SHUTDOWN:
				DestroyWindow(hwnd);
				return IntPtr.Zero;

			case WM_DESTROY:
				PostQuitMessage(0);
				return IntPtr.Zero;

			default:
				return DefWindowProc(hwnd, msg, w_param, l_param);
		}
	}

	#region P/Invoke — User32

	private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WNDCLASSEX
	{
		public uint cbSize;
		public uint style;
		public IntPtr lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public IntPtr hInstance;
		public IntPtr hIcon;
		public IntPtr hCursor;
		public IntPtr hbrBackground;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string lpszMenuName;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string lpszClassName;
		public IntPtr hIconSm;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MSG
	{
		public IntPtr hwnd;
		public uint message;
		public IntPtr wParam;
		public IntPtr lParam;
		public uint time;
		public POINT pt;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int x;
		public int y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int left;
		public int top;
		public int right;
		public int bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PAINTSTRUCT
	{
		public IntPtr hdc;
		public int fErase;
		public RECT rcPaint;
		public int fRestore;
		public int fIncUpdate;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
		public byte[] rgbReserved;
	}

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr CreateWindowEx(
		uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
		int x, int y, int nWidth, int nHeight,
		IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool UpdateWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG lpMsg);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref MSG lpMsg);

	[DllImport("user32.dll")]
	private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern void PostQuitMessage(int nExitCode);

	[DllImport("user32.dll")]
	private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

	[DllImport("user32.dll")]
	private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

	[DllImport("user32.dll")]
	private static extern bool DestroyWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

	[DllImport("user32.dll")]
	private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);

	#endregion

	#region P/Invoke — Gdi32

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateSolidBrush(uint crColor);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr hObject);

	#endregion
}
