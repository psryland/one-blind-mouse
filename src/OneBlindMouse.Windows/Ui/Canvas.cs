using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows.Ui;

/// <summary>
/// A 32-bit premultiplied-alpha drawing surface backed by a DIB section, drawn with GDI+.
/// The same memory is used by GDI+ and by UpdateLayeredWindow, so presenting needs no copy.
/// Colours are 0xAARRGGBB (not premultiplied).
/// </summary>
internal sealed unsafe class Canvas : IDisposable
{
	private readonly nint m_hdc;
	private readonly nint m_dib;
	private readonly nint m_old;
	private readonly nint m_bitmap;
	private readonly nint m_gfx;

	/// <summary>Create a canvas of the given size in pixels. It starts fully transparent.</summary>
	public Canvas(int width, int height)
	{
		// A top-down DIB (negative height) matches GDI+ row order
		Width = Math.Max(width, 1);
		Height = Math.Max(height, 1);
		var bmi = new BITMAPINFOHEADER
		{
			biSize = (uint)sizeof(BITMAPINFOHEADER),
			biWidth = Width,
			biHeight = -Height,
			biPlanes = 1,
			biBitCount = 32,
		};
		m_hdc = Gdi32.CreateCompatibleDC(0);
		m_dib = Gdi32.CreateDIBSection(m_hdc, &bmi, Gdi32.DIB_RGB_COLORS, out var bits, 0, 0);
		if (m_dib == 0)
			throw new OutOfMemoryException("CreateDIBSection failed");

		m_old = Gdi32.SelectObject(m_hdc, m_dib);

		// Wrap the DIB memory in a GDI+ bitmap with high quality settings
		Check(Gdip.GdipCreateBitmapFromScan0(Width, Height, Width * 4, Gdip.PixelFormat32bppPARGB, bits, out m_bitmap));
		Check(Gdip.GdipGetImageGraphicsContext(m_bitmap, out m_gfx));
		Gdip.GdipSetSmoothingMode(m_gfx, Gdip.SmoothingModeAntiAlias);
		Gdip.GdipSetPixelOffsetMode(m_gfx, Gdip.PixelOffsetModeHalf);
		Gdip.GdipSetTextRenderingHint(m_gfx, Gdip.TextRenderingHintAntiAlias);
	}

	/// <summary>Size in pixels.</summary>
	public int Width { get; }
	public int Height { get; }

	/// <summary>The memory DC holding the DIB, for UpdateLayeredWindow.</summary>
	public nint Hdc => m_hdc;

	/// <summary>The GDI+ bitmap, e.g. for creating an icon.</summary>
	public nint Bitmap => m_bitmap;

	/// <summary>Make every pixel fully transparent.</summary>
	public void Clear()
	{
		Gdip.GdipGraphicsClear(m_gfx, 0);
	}

	/// <summary>Fill a closed polygon.</summary>
	public void FillPolygon(ReadOnlySpan<PointF> points, uint argb)
	{
		// Alternate fill mode (0) is fine for the simple shapes drawn here
		var brush = Brush(argb);
		fixed (PointF* p = points)
			Gdip.GdipFillPolygon(m_gfx, brush, p, points.Length, 0);

		Gdip.GdipDeleteBrush(brush);
	}

	/// <summary>Outline a closed polygon.</summary>
	public void DrawPolygon(ReadOnlySpan<PointF> points, uint argb, float width)
	{
		var pen = Pen(argb, width);
		fixed (PointF* p = points)
			Gdip.GdipDrawPolygon(m_gfx, pen, p, points.Length);

		Gdip.GdipDeletePen(pen);
	}

	/// <summary>Draw an open polyline with round joins and caps.</summary>
	public void DrawLines(ReadOnlySpan<PointF> points, uint argb, float width)
	{
		// A polyline needs at least two points
		if (points.Length < 2)
			return;

		var pen = Pen(argb, width);
		fixed (PointF* p = points)
			Gdip.GdipDrawLines(m_gfx, pen, p, points.Length);

		Gdip.GdipDeletePen(pen);
	}

	/// <summary>Fill a circle.</summary>
	public void FillCircle(float cx, float cy, float radius, uint argb)
	{
		var brush = Brush(argb);
		Gdip.GdipFillEllipse(m_gfx, brush, cx - radius, cy - radius, radius * 2, radius * 2);
		Gdip.GdipDeleteBrush(brush);
	}

	/// <summary>Outline a circle.</summary>
	public void DrawCircle(float cx, float cy, float radius, uint argb, float width)
	{
		var pen = Pen(argb, width);
		Gdip.GdipDrawEllipse(m_gfx, pen, cx - radius, cy - radius, radius * 2, radius * 2);
		Gdip.GdipDeletePen(pen);
	}

	/// <summary>Fill and/or outline a rounded rectangle. Pass 0 for 'fill' or 'line' to skip that part.</summary>
	public void RoundRect(RectF r, float radius, uint fill, uint line, float line_width)
	{
		// Build the outline from four corner arcs; GDI+ joins them with straight edges
		Gdip.GdipCreatePath(0, out var path);
		var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
		Gdip.GdipAddPathArc(path, r.X, r.Y, d, d, 180, 90);
		Gdip.GdipAddPathArc(path, r.X + r.Width - d, r.Y, d, d, 270, 90);
		Gdip.GdipAddPathArc(path, r.X + r.Width - d, r.Y + r.Height - d, d, d, 0, 90);
		Gdip.GdipAddPathArc(path, r.X, r.Y + r.Height - d, d, d, 90, 90);
		Gdip.GdipClosePathFigure(path);

		// Draw the requested parts
		if (fill != 0)
		{
			var brush = Brush(fill);
			Gdip.GdipFillPath(m_gfx, brush, path);
			Gdip.GdipDeleteBrush(brush);
		}
		if (line != 0)
		{
			var pen = Pen(line, line_width);
			Gdip.GdipDrawPath(m_gfx, pen, path);
			Gdip.GdipDeletePen(pen);
		}
		Gdip.GdipDeletePath(path);
	}

	/// <summary>Measure single-line text drawn with 'font'.</summary>
	public SizeF MeasureText(string text, Font font)
	{
		// A generous layout box keeps the text on one line
		var layout = new RectF(0, 0, 10000, 1000);
		var bounds = default(RectF);
		Gdip.GdipMeasureString(m_gfx, text, text.Length, font.Handle, &layout, Font.Format, &bounds, out _, out _);
		return new SizeF(bounds.Width, bounds.Height);
	}

	/// <summary>Draw single-line text with its top-left corner at (x, y).</summary>
	public void DrawText(string text, Font font, float x, float y, uint argb)
	{
		var layout = new RectF(x, y, 10000, 1000);
		var brush = Brush(argb);
		Gdip.GdipDrawString(m_gfx, text, text.Length, font.Handle, &layout, Font.Format, brush);
		Gdip.GdipDeleteBrush(brush);
	}

	/// <summary>Release the GDI and GDI+ resources.</summary>
	public void Dispose()
	{
		// Release in reverse order of creation
		Gdip.GdipDeleteGraphics(m_gfx);
		Gdip.GdipDisposeImage(m_bitmap);
		Gdi32.SelectObject(m_hdc, m_old);
		Gdi32.DeleteObject(m_dib);
		Gdi32.DeleteDC(m_hdc);
	}

	/// <summary>Create a solid brush. The caller deletes it.</summary>
	private static nint Brush(uint argb)
	{
		Gdip.GdipCreateSolidFill(argb, out var brush);
		return brush;
	}

	/// <summary>Create a pen with round joins and caps. The caller deletes it.</summary>
	private static nint Pen(uint argb, float width)
	{
		Gdip.GdipCreatePen1(argb, width, Gdip.UnitPixel, out var pen);
		Gdip.GdipSetPenLineJoin(pen, Gdip.LineJoinRound);
		Gdip.GdipSetPenStartCap(pen, Gdip.LineCapRound);
		Gdip.GdipSetPenEndCap(pen, Gdip.LineCapRound);
		return pen;
	}

	/// <summary>Throw if a GDI+ call failed.</summary>
	private static void Check(int status)
	{
		if (status != 0)
			throw new InvalidOperationException($"GDI+ error {status}");
	}
}

/// <summary>A width and height in pixels.</summary>
internal readonly record struct SizeF(float Width, float Height);

/// <summary>A GDI+ font with a pixel size. Fonts are cached because the overlay redraws often.</summary>
internal sealed class Font
{
	private static readonly Dictionary<(string, int, bool), Font> s_cache = [];
	private static nint s_format;

	private Font(nint handle)
	{
		Handle = handle;
	}

	/// <summary>The GDI+ font handle.</summary>
	public nint Handle { get; }

	/// <summary>A typographic string format, which measures text tightly without extra padding.</summary>
	public static nint Format
	{
		get
		{
			// Created lazily because GDI+ must be started first
			if (s_format == 0)
				Gdip.GdipStringFormatGetGenericTypographic(out s_format);

			return s_format;
		}
	}

	/// <summary>Get a font of the given family and pixel size.</summary>
	public static Font Get(string family, float pixels, bool bold = false)
	{
		// Round the size so nearby sizes share a cache entry
		var key = (family, (int)MathF.Round(pixels), bold);
		if (s_cache.TryGetValue(key, out var font))
			return font;

		Gdip.GdipCreateFontFamilyFromName(family, 0, out var fam);
		Gdip.GdipCreateFont(fam, key.Item2, bold ? Gdip.FontStyleBold : Gdip.FontStyleRegular, Gdip.UnitPixel, out var handle);
		Gdip.GdipDeleteFontFamily(fam);
		font = new Font(handle);
		s_cache[key] = font;
		return font;
	}
}
