using System.Runtime.InteropServices;
using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows;

/// <summary>A display monitor. Rectangles are in physical virtual-screen pixels.</summary>
internal sealed record Monitor(nint Handle, string Device, RECT Bounds, RECT WorkArea, bool Primary, uint Dpi)
{
	/// <summary>A short user-facing description, e.g. "Display 2 (2560×1440)".</summary>
	public string Description => $"{Device.TrimStart('\\', '.')} ({Bounds.Width}×{Bounds.Height}){(Primary ? " — primary" : string.Empty)}";
}

/// <summary>Queries the current monitor layout.</summary>
internal static unsafe class Monitors
{
	[ThreadStatic] private static List<Monitor>? t_found;

	/// <summary>All monitors, primary first.</summary>
	public static List<Monitor> All()
	{
		// EnumDisplayMonitors calls back synchronously on this thread, so a thread-static list collects the results
		t_found = [];
		try
		{
			User32.EnumDisplayMonitors(0, null, &OnMonitor, 0);
			return [.. t_found.OrderByDescending(m => m.Primary)];
		}
		finally
		{
			t_found = null;
		}
	}

	/// <summary>The monitor with the given device name, or the primary monitor if it no longer exists.</summary>
	public static Monitor Find(string? device)
	{
		var all = All();
		return all.FirstOrDefault(m => m.Device == device) ?? all[0];
	}

	/// <summary>The monitor nearest to a screen point.</summary>
	public static Monitor At(int x, int y)
	{
		return Describe(User32.MonitorFromPoint(new POINT(x, y), User32.MONITOR_DEFAULTTONEAREST));
	}

	/// <summary>The effective DPI of the monitor nearest to a screen point.</summary>
	public static uint DpiAt(int x, int y)
	{
		var mon = User32.MonitorFromPoint(new POINT(x, y), User32.MONITOR_DEFAULTTONEAREST);
		return Shcore.GetDpiForMonitor(mon, Shcore.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi : 96;
	}

	/// <summary>Build a <see cref="Monitor"/> from a monitor handle.</summary>
	private static Monitor Describe(nint handle)
	{
		// Query bounds, device name and DPI
		var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
		User32.GetMonitorInfoW(handle, &info);
		var device = new string(info.szDevice);
		var dpi = Shcore.GetDpiForMonitor(handle, Shcore.MDT_EFFECTIVE_DPI, out var x, out _) == 0 ? x : 96u;
		return new Monitor(handle, device, info.rcMonitor, info.rcWork, (info.dwFlags & User32.MONITORINFOF_PRIMARY) != 0, dpi);
	}

	/// <summary>EnumDisplayMonitors callback.</summary>
	[UnmanagedCallersOnly]
	private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
	{
		// Never let an exception cross the native boundary
		try
		{
			t_found?.Add(Describe(monitor));
		}
		catch (Exception ex)
		{
			Program.Crash(ex);
		}
		return 1;
	}
}
