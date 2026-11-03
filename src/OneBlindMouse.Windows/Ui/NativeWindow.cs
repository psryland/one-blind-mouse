using System.ComponentModel;
using System.Runtime.InteropServices;
using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows.Ui;

/// <summary>
/// Base class for a Win32 window owned by a managed object. All windows must be created and used on the UI thread.
/// Derived classes override <see cref="WndProc"/> to handle messages.
/// </summary>
internal abstract unsafe class NativeWindow : IDisposable
{
	// Maps window handles to their owners so the shared window procedure can route messages.
	// The window being created is held separately because messages arrive before CreateWindowExW returns the handle.
	private static readonly Dictionary<nint, NativeWindow> s_windows = [];
	private static readonly HashSet<string> s_classes = [];
	[ThreadStatic] private static NativeWindow? t_creating;

	/// <summary>The window handle, or 0 once destroyed.</summary>
	public nint Handle { get; private set; }

	/// <summary>Create the window. The class is registered on first use with the given cursor and background brush.</summary>
	protected void CreateWindow(string class_name, uint ex_style, uint style, string? title, RECT rect, nint parent = 0, int cursor = User32.IDC_ARROW, nint background = 0)
	{
		// Register the class once per process
		RegisterClass(class_name, cursor, background);

		// Route messages sent during creation (e.g. WM_NCCREATE) to this instance
		t_creating = this;
		try
		{
			var hwnd = User32.CreateWindowExW(ex_style, class_name, title, style, rect.Left, rect.Top, rect.Width, rect.Height, parent, 0, Kernel32.GetModuleHandleW(null), 0);
			if (hwnd == 0)
				throw new Win32Exception(Marshal.GetLastPInvokeError());

			Handle = hwnd;
		}
		finally
		{
			t_creating = null;
		}
	}

	/// <summary>Handle a window message. The default passes it to DefWindowProc.</summary>
	protected virtual nint WndProc(uint msg, nint wparam, nint lparam)
	{
		return User32.DefWindowProcW(Handle, msg, wparam, lparam);
	}

	/// <summary>Called after the window has been destroyed.</summary>
	protected virtual void OnDestroyed()
	{
	}

	/// <summary>Destroy the window.</summary>
	public virtual void Dispose()
	{
		// Destroying triggers WM_NCDESTROY, which unregisters the window
		if (Handle != 0)
			User32.DestroyWindow(Handle);
	}

	/// <summary>Register a window class that uses the shared window procedure.</summary>
	private static void RegisterClass(string class_name, int cursor, nint background)
	{
		// The class name is copied by Windows, so it only needs to be pinned during the call
		if (!s_classes.Add(class_name))
			return;

		fixed (char* name = class_name)
		{
			var wc = new WNDCLASSEXW
			{
				cbSize = (uint)sizeof(WNDCLASSEXW),
				lpfnWndProc = &StaticWndProc,
				hInstance = Kernel32.GetModuleHandleW(null),
				hCursor = User32.LoadCursorW(0, cursor),
				hbrBackground = background,
				lpszClassName = name,
			};
			if (User32.RegisterClassExW(&wc) == 0)
				throw new Win32Exception(Marshal.GetLastPInvokeError());
		}
	}

	/// <summary>The window procedure for all windows created by this class.</summary>
	[UnmanagedCallersOnly]
	private static nint StaticWndProc(nint hwnd, uint msg, nint wparam, nint lparam)
	{
		// Exceptions must not cross the native boundary, so they end the app through the crash handler
		try
		{
			// Bind the window being created to its handle on its first message
			if (!s_windows.TryGetValue(hwnd, out var wnd) && t_creating is { } creating)
			{
				wnd = creating;
				wnd.Handle = hwnd;
				s_windows[hwnd] = wnd;
			}
			if (wnd == null)
				return User32.DefWindowProcW(hwnd, msg, wparam, lparam);

			// Unregister after the final message so late messages fall through to DefWindowProc
			var result = wnd.WndProc(msg, wparam, lparam);
			if (msg == User32.WM_NCDESTROY)
			{
				s_windows.Remove(hwnd);
				wnd.Handle = 0;
				wnd.OnDestroyed();
			}
			return result;
		}
		catch (Exception ex)
		{
			Program.Crash(ex);
			return 0;
		}
	}
}
