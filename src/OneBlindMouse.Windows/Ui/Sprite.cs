using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows.Ui;

/// <summary>
/// A small topmost, click-through, per-pixel alpha window used to draw overlay graphics (pointers, ripples, highlights).
/// Draw into <see cref="Begin"/>'s canvas, then call <see cref="Present"/>. Moving without redrawing uses <see cref="Move"/>.
/// </summary>
internal sealed unsafe class Sprite : NativeWindow
{
	private Canvas? m_canvas;
	private bool m_visible;

	/// <summary>Create a hidden sprite. 'hide_from_capture' excludes it from screen capture and screen sharing.</summary>
	public Sprite(bool hide_from_capture)
	{
		// Layered + transparent makes the window invisible to the mouse; no-activate keeps focus where it is
		const uint ex = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE;
		CreateWindow("OBM.Sprite", ex, User32.WS_POPUP, null, new RECT(0, 0, 1, 1));
		if (hide_from_capture)
			User32.SetWindowDisplayAffinity(Handle, User32.WDA_EXCLUDEFROMCAPTURE);
	}

	/// <summary>Screen position of the sprite's top-left corner.</summary>
	public POINT Position { get; private set; }

	/// <summary>Get a cleared canvas of at least the given size to draw the next image into.</summary>
	public Canvas Begin(int width, int height)
	{
		// Reuse the canvas when the size is unchanged, which is the common case
		if (m_canvas == null || m_canvas.Width != width || m_canvas.Height != height)
		{
			m_canvas?.Dispose();
			m_canvas = new Canvas(width, height);
		}
		m_canvas.Clear();
		return m_canvas;
	}

	/// <summary>Show the canvas contents with the top-left corner at (x, y). 'opacity' scales the whole image.</summary>
	public void Present(int x, int y, byte opacity = 255)
	{
		// Nothing to show until something has been drawn
		if (m_canvas == null)
			return;

		var dst = new POINT(x, y);
		var src = new POINT(0, 0);
		var size = new SIZE { CX = m_canvas.Width, CY = m_canvas.Height };
		var blend = new BLENDFUNCTION { BlendOp = User32.AC_SRC_OVER, SourceConstantAlpha = opacity, AlphaFormat = User32.AC_SRC_ALPHA };
		User32.UpdateLayeredWindow(Handle, 0, &dst, &size, m_canvas.Hdc, &src, 0, &blend, User32.ULW_ALPHA);
		Position = dst;
		Show();
	}

	/// <summary>Move the sprite without redrawing.</summary>
	public void Move(int x, int y)
	{
		// Skip redundant moves, which are frequent while a remote pointer is still
		if (Position.X == x && Position.Y == y && m_visible)
			return;

		Position = new POINT(x, y);
		User32.SetWindowPos(Handle, User32.HWND_TOPMOST, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
		m_visible = true;
	}

	/// <summary>Hide the sprite.</summary>
	public void Hide()
	{
		if (!m_visible)
			return;

		User32.ShowWindow(Handle, User32.SW_HIDE);
		m_visible = false;
	}

	/// <summary>Show the sprite (above other topmost windows) without activating it.</summary>
	private void Show()
	{
		// Re-assert topmost order each time because other topmost windows may have been raised above it
		User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
		m_visible = true;
	}

	/// <inheritdoc/>
	public override void Dispose()
	{
		// Free the canvas along with the window
		base.Dispose();
		m_canvas?.Dispose();
		m_canvas = null;
	}
}
