using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows.Ui;

/// <summary>
/// A nearly invisible window laid over the pointing frame while pointing. It takes the mouse hover away from the application underneath
/// (so tooltips and hover effects don't trigger) and shows a cross-hair cursor. Mouse buttons are blocked separately by the input hook.
/// </summary>
internal sealed class CaptureWindow : NativeWindow
{
	/// <summary>Create the hidden window.</summary>
	public CaptureWindow()
	{
		// Alpha 1 is invisible to the eye but still hit-testable, unlike a fully transparent window
		const uint ex = User32.WS_EX_LAYERED | User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE;
		CreateWindow("OBM.Capture", ex, User32.WS_POPUP, null, new RECT(0, 0, 1, 1), cursor: User32.IDC_CROSS, background: User32.GetSysColorBrush(User32.COLOR_BTNFACE));
		User32.SetLayeredWindowAttributes(Handle, 0, 1, User32.LWA_ALPHA);
	}

	/// <summary>Cover 'rect' and show the window without activating it.</summary>
	public void Show(RECT rect)
	{
		User32.SetWindowPos(Handle, User32.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
	}

	/// <summary>Hide the window.</summary>
	public void Hide()
	{
		User32.ShowWindow(Handle, User32.SW_HIDE);
	}

	/// <inheritdoc/>
	protected override nint WndProc(uint msg, nint wparam, nint lparam)
	{
		// Clicks must never activate this window and steal focus from the user's application
		if (msg == User32.WM_MOUSEACTIVATE)
			return User32.MA_NOACTIVATE;

		return base.WndProc(msg, wparam, lparam);
	}
}

/// <summary>A coloured rectangle outline drawn with four thin solid windows (cheap at any size, unlike a full-size layered bitmap).</summary>
internal sealed class FrameBorder : IDisposable
{
	private readonly Edge[] m_edges = [new(), new(), new(), new()];

	/// <summary>Outline 'rect' (the border is drawn outside it) in colour 'rgb' with the given thickness.</summary>
	public void Show(RECT rect, uint rgb, int thickness)
	{
		// Top, bottom, left, right; the side edges span the full height including the corners
		var t = thickness;
		m_edges[0].Show(new RECT(rect.Left - t, rect.Top - t, rect.Right + t, rect.Top), rgb);
		m_edges[1].Show(new RECT(rect.Left - t, rect.Bottom, rect.Right + t, rect.Bottom + t), rgb);
		m_edges[2].Show(new RECT(rect.Left - t, rect.Top, rect.Left, rect.Bottom), rgb);
		m_edges[3].Show(new RECT(rect.Right, rect.Top, rect.Right + t, rect.Bottom), rgb);
	}

	/// <summary>Hide the outline.</summary>
	public void Hide()
	{
		foreach (var e in m_edges)
			e.Hide();
	}

	/// <summary>Destroy the windows.</summary>
	public void Dispose()
	{
		foreach (var e in m_edges)
			e.Dispose();
	}

	/// <summary>One edge of the outline: a click-through window filled with a solid colour.</summary>
	private sealed unsafe class Edge : NativeWindow
	{
		private nint m_brush;
		private uint m_rgb = uint.MaxValue;

		public Edge()
		{
			// Click-through so the outline never interferes with the pointing input
			const uint ex = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE;
			CreateWindow("OBM.Edge", ex, User32.WS_POPUP, null, new RECT(0, 0, 1, 1));
			User32.SetLayeredWindowAttributes(Handle, 0, 230, User32.LWA_ALPHA);
		}

		/// <summary>Place the edge and fill it with 'rgb'.</summary>
		public void Show(RECT rect, uint rgb)
		{
			// GDI colours are 0x00BBGGRR
			if (rgb != m_rgb)
			{
				if (m_brush != 0)
					Gdi32.DeleteObject(m_brush);

				m_brush = Gdi32.CreateSolidBrush(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
				m_rgb = rgb;
				User32.InvalidateRect(Handle, null, true);
			}
			User32.SetWindowPos(Handle, User32.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
		}

		/// <summary>Hide the edge.</summary>
		public void Hide()
		{
			User32.ShowWindow(Handle, User32.SW_HIDE);
		}

		/// <inheritdoc/>
		protected override nint WndProc(uint msg, nint wparam, nint lparam)
		{
			// Fill the whole window with the edge colour
			switch (msg)
			{
				case User32.WM_ERASEBKGND:
				{
					if (User32.GetClientRect(Handle, out var rc))
						User32.FillRect(wparam, &rc, m_brush);

					return 1;
				}
				case User32.WM_PAINT:
				{
					var ps = default(PAINTSTRUCT);
					User32.BeginPaint(Handle, &ps);
					User32.EndPaint(Handle, &ps);
					return 0;
				}
				default:
				{
					return base.WndProc(msg, wparam, lparam);
				}
			}
		}

		/// <inheritdoc/>
		protected override void OnDestroyed()
		{
			// The brush outlives repaints, so free it with the window
			if (m_brush != 0)
				Gdi32.DeleteObject(m_brush);

			m_brush = 0;
		}
	}
}
