using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows;

/// <summary>What the receiver shares as its pointing area.</summary>
internal enum ETargetKind
{
	/// <summary>A whole monitor.</summary>
	Monitor,

	/// <summary>The client area of one application window.</summary>
	Window,
}

/// <summary>
/// The area of this screen that remote pointers map onto. Remote coordinates are fractions of this area,
/// so it should match what the other people see in the screen share (a whole monitor or a single shared window).
/// </summary>
internal sealed class Target
{
	/// <summary>Kind of target.</summary>
	public ETargetKind Kind { get; private set; } = ETargetKind.Monitor;

	/// <summary>Monitor device name when <see cref="Kind"/> is Monitor (null means the primary monitor).</summary>
	public string? Device { get; private set; }

	/// <summary>Window handle when <see cref="Kind"/> is Window.</summary>
	public nint Window { get; private set; }

	/// <summary>Use a monitor as the target.</summary>
	public void SetMonitor(string? device)
	{
		Kind = ETargetKind.Monitor;
		Device = device;
		Window = 0;
	}

	/// <summary>Use a window's client area as the target.</summary>
	public void SetWindow(nint hwnd)
	{
		Kind = ETargetKind.Window;
		Window = hwnd;
	}

	/// <summary>True if a window target has been closed.</summary>
	public bool IsLost => Kind == ETargetKind.Window && !User32.IsWindow(Window);

	/// <summary>The current target area in screen pixels. False if it cannot be shown right now (e.g. the window is minimised).</summary>
	public bool TryGetRect(out RECT rect)
	{
		// Monitors always exist (falling back to the primary); windows may be hidden or minimised
		switch (Kind)
		{
			case ETargetKind.Monitor:
			{
				rect = Monitors.Find(Device).Bounds;
				return true;
			}
			case ETargetKind.Window:
			{
				rect = default;
				if (!User32.IsWindow(Window) || User32.IsIconic(Window) || !User32.IsWindowVisible(Window))
					return false;

				rect = User32.GetClientRectScreen(Window);
				return !rect.IsEmpty;
			}
			default:
			{
				throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
			}
		}
	}

	/// <summary>Width / height of the target, or 0 if unknown.</summary>
	public double Aspect()
	{
		// Clamp to the protocol's valid range; extreme shapes are reported as unknown
		if (!TryGetRect(out var rc))
			return 0;

		var aspect = (double)rc.Width / rc.Height;
		return aspect is >= 0.1 and <= 10 ? Math.Round(aspect, 3) : 0;
	}

	/// <summary>A short description for other members, e.g. "Display 1" or "Visual Studio".</summary>
	public string Label()
	{
		// Window titles can be long and contain document names, so only the last part (usually the app name) is shared
		switch (Kind)
		{
			case ETargetKind.Monitor:
			{
				var all = Monitors.All();
				var index = all.FindIndex(m => m.Device == Monitors.Find(Device).Device);
				return all.Count > 1 ? $"Screen {index + 1} of {all.Count}" : "Screen";
			}
			case ETargetKind.Window:
			{
				var title = User32.GetWindowText(Window);
				var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
				return Core.Names.SanitiseOr(dash >= 0 ? title[(dash + 3)..] : title, "Window").Truncate(Core.Protocol.MaxLabelLength);
			}
			default:
			{
				throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
			}
		}
	}
}

/// <summary>String helpers.</summary>
internal static class StringExt
{
	/// <summary>The string cut to at most 'max' characters, without splitting a surrogate pair.</summary>
	public static string Truncate(this string text, int max)
	{
		// Step back one character if the cut would leave a lone high surrogate
		if (text.Length <= max)
			return text;

		var len = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
		return text[..len];
	}
}
