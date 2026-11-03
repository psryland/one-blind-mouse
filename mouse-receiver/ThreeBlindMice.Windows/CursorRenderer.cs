using System.Runtime.InteropServices;
using ThreeBlindMice.Core;

namespace ThreeBlindMice.Windows;

/// <summary>
/// Renders coloured arrow cursors with name labels on the overlay window using GDI P/Invoke.
/// </summary>
internal sealed class CursorRenderer : IDisposable
{
	private const uint COLOR_KEY = 0x00FF00FF; // magenta COLORREF (0x00BBGGRR)
	private const int TRANSPARENT = 1;

	// Arrow polygon points relative to cursor tip at (0,0), roughly 20×30 pixels
	private static readonly int[] s_arrow_dx = { 0,  0,  6,  9, 13,  9, 15 };
	private static readonly int[] s_arrow_dy = { 0, 21, 17, 26, 24, 16, 16 };
	private const int ARROW_POINT_COUNT = 7;

	private IntPtr m_font;

	public CursorRenderer()
	{
		m_font = CreateFont(
			14, 0, 0, 0,
			400, // FW_NORMAL
			0, 0, 0,
			1, // DEFAULT_CHARSET
			0, 0, 0, 0,
			"Segoe UI");
	}

	/// <summary>
	/// Draws all active cursors onto the given device context.
	/// Non-cursor areas are filled with the colour key so they appear transparent.
	/// When constraint parameters are provided, normalised (0,1) cursor coords
	/// map to the constraint region and are then translated to overlay-local coords.
	/// </summary>
	public void Render(IntPtr hdc, CursorState state, int overlay_width, int overlay_height,
		int constraint_left = 0, int constraint_top = 0,
		int constraint_width = 0, int constraint_height = 0,
		int overlay_left = 0, int overlay_top = 0)
	{
		// If no constraint specified, map (0,1) directly to overlay dimensions
		var cw = constraint_width > 0 ? constraint_width : overlay_width;
		var ch = constraint_height > 0 ? constraint_height : overlay_height;
		var cl = constraint_width > 0 ? constraint_left : overlay_left;
		var ct = constraint_height > 0 ? constraint_top : overlay_top;

		// Fill entire area with colour key for transparency
		var full_rect = new RECT { left = 0, top = 0, right = overlay_width, bottom = overlay_height };
		var bg_brush = CreateSolidBrush(COLOR_KEY);
		FillRect(hdc, ref full_rect, bg_brush);
		DeleteObject(bg_brush);

		var old_font = SelectObject(hdc, m_font);
		SetBkMode(hdc, TRANSPARENT);

		foreach (var kvp in state.Cursors)
		{
			var cursor = kvp.Value;
			var colour_ref = ParseColour(cursor.Colour);

			// Map normalised [0,1] to constraint region, then to overlay-local coords
			var cx = (int)(cl + cursor.X * cw) - overlay_left;
			var cy = (int)(ct + cursor.Y * ch) - overlay_top;

			DrawTrail(hdc, cursor, colour_ref, cl, ct, cw, ch, overlay_left, overlay_top);
			DrawArrow(hdc, cx, cy, colour_ref);
			DrawLabel(hdc, cx, cy, cursor.Display_Name, colour_ref);
		}

		SelectObject(hdc, old_font);
	}

	private static void DrawTrail(IntPtr hdc, CursorInfo cursor, uint colour_ref,
		int cl, int ct, int cw, int ch, int overlay_left, int overlay_top)
	{
		var trail = cursor.Trail_Points;
		if (trail.Count < 2)
			return;

		for (var i = 1; i < trail.Count; i++)
		{
			var age_ratio = (float)i / trail.Count;
			var pen_width = Math.Max(1, (int)(age_ratio * 4));

			var pen = CreatePen(0, pen_width, colour_ref);
			var old_pen = SelectObject(hdc, pen);

			var x0 = (int)(cl + trail[i - 1].X * cw) - overlay_left;
			var y0 = (int)(ct + trail[i - 1].Y * ch) - overlay_top;
			var x1 = (int)(cl + trail[i].X * cw) - overlay_left;
			var y1 = (int)(ct + trail[i].Y * ch) - overlay_top;

			MoveToEx(hdc, x0, y0, IntPtr.Zero);
			LineTo(hdc, x1, y1);

			SelectObject(hdc, old_pen);
			DeleteObject(pen);
		}
	}

	private static void DrawArrow(IntPtr hdc, int cx, int cy, uint colour_ref)
	{
		var pen = CreatePen(0, 1, 0x00000000); // PS_SOLID, black outline
		var brush = CreateSolidBrush(colour_ref);
		var old_pen = SelectObject(hdc, pen);
		var old_brush = SelectObject(hdc, brush);

		var points = new POINT[ARROW_POINT_COUNT];
		for (var i = 0; i < ARROW_POINT_COUNT; i++)
		{
			points[i].x = cx + s_arrow_dx[i];
			points[i].y = cy + s_arrow_dy[i];
		}

		Polygon(hdc, points, ARROW_POINT_COUNT);

		SelectObject(hdc, old_brush);
		SelectObject(hdc, old_pen);
		DeleteObject(brush);
		DeleteObject(pen);
	}

	private static void DrawLabel(IntPtr hdc, int cx, int cy, string name, uint colour_ref)
	{
		if (string.IsNullOrEmpty(name))
			return;

		// Position label to the right of and slightly below the arrow tip
		var label_x = cx + 18;
		var label_y = cy + 4;
		const int padding = 4;
		const int accent_width = 3;

		// Approximate text metrics (width ≈ 7px per char, height ≈ 16px)
		var text_width = name.Length * 7;
		const int text_height = 16;

		// Dark background for readability on any desktop
		var bg_rect = new RECT
		{
			left = label_x - padding,
			top = label_y - padding,
			right = label_x + text_width + padding + accent_width,
			bottom = label_y + text_height + padding,
		};
		var bg_brush = CreateSolidBrush(0x00282828); // dark charcoal (COLORREF 0x00BBGGRR)
		FillRect(hdc, ref bg_rect, bg_brush);
		DeleteObject(bg_brush);

		// Coloured left accent bar
		var accent_rect = new RECT
		{
			left = label_x - padding,
			top = label_y - padding,
			right = label_x - padding + accent_width,
			bottom = label_y + text_height + padding,
		};
		var accent_brush = CreateSolidBrush(colour_ref);
		FillRect(hdc, ref accent_rect, accent_brush);
		DeleteObject(accent_brush);

		// Draw the name in white
		SetTextColor(hdc, 0x00FFFFFF);
		SetBkMode(hdc, TRANSPARENT);
		TextOut(hdc, label_x + accent_width, label_y, name, name.Length);
	}

	/// <summary>
	/// Parses a hex colour string "#RRGGBB" to a Win32 COLORREF value (0x00BBGGRR).
	/// </summary>
	public static uint ParseColour(string hex)
	{
		if (hex.Length != 7 || hex[0] != '#')
			return 0x00FFFFFF; // fallback to white

		var r = Convert.ToByte(hex.Substring(1, 2), 16);
		var g = Convert.ToByte(hex.Substring(3, 2), 16);
		var b = Convert.ToByte(hex.Substring(5, 2), 16);
		return (uint)(b << 16 | g << 8 | r);
	}

	public void Dispose()
	{
		if (m_font != IntPtr.Zero)
		{
			DeleteObject(m_font);
			m_font = IntPtr.Zero;
		}
	}

	#region P/Invoke — GDI32 / User32

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

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreatePen(int iStyle, int cWidth, uint color);

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateSolidBrush(uint crColor);

	[DllImport("gdi32.dll")]
	private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

	[DllImport("gdi32.dll")]
	private static extern bool Polygon(IntPtr hdc, POINT[] apt, int cpt);

	[DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
	private static extern bool TextOut(IntPtr hdc, int x, int y, string lpString, int c);

	[DllImport("gdi32.dll")]
	private static extern uint SetTextColor(IntPtr hdc, uint color);

	[DllImport("gdi32.dll")]
	private static extern int SetBkMode(IntPtr hdc, int mode);

	[DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr CreateFont(
		int cHeight, int cWidth, int cEscapement, int cOrientation,
		int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut,
		uint iCharSet, uint iOutPrecision, uint iClipPrecision,
		uint iQuality, uint iPitchAndFamily, string pszFaceName);

	[DllImport("user32.dll")]
	private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr ho);

	[DllImport("gdi32.dll")]
	private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

	[DllImport("gdi32.dll")]
	private static extern bool LineTo(IntPtr hdc, int x, int y);

	#endregion
}
