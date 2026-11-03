using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows.Ui;

/// <summary>
/// Draws the overlay graphics. All sizes are given at 96 DPI and 100% pointer size, then multiplied by 'scale'.
/// Colours are 0xRRGGBB (opaque); alpha is added here.
/// </summary>
internal static class Art
{
	/// <summary>Font family used for labels.</summary>
	public const string FontFamily = "Segoe UI";

	/// <summary>Arrow outline with the hotspot at (0,0). The arrow is larger than the system cursor so it stands out in a screen share.</summary>
	private static readonly PointF[] ArrowShape = [new(0, 0), new(0, 22), new(5.5f, 17), new(9.5f, 26), new(13.5f, 24.5f), new(9.5f, 16), new(16.5f, 16)];

	/// <summary>Padding around drawings so anti-aliased edges and shadows are not clipped.</summary>
	private const float Pad = 4;

	// A small canvas used only for measuring text
	private static Canvas? s_measure;

	/// <summary>Layout of a pointer sprite: canvas size and the hotspot position within it.</summary>
	public readonly record struct PointerLayout(int Width, int Height, int HotX, int HotY);

	/// <summary>Compute the canvas size needed for a pointer with the given label.</summary>
	public static PointerLayout LayoutPointer(string label, float scale)
	{
		// The label pill sits below and to the right of the arrow tip
		var (pill, _) = LabelPill(label, scale);
		var w = Math.Max(17 * scale, pill.X + pill.Width) + 2 * Pad * scale;
		var h = Math.Max(27 * scale, pill.Y + pill.Height) + 2 * Pad * scale;
		var hot = (int)MathF.Ceiling(Pad * scale);
		return new PointerLayout((int)MathF.Ceiling(w + hot), (int)MathF.Ceiling(h + hot), hot, hot);
	}

	/// <summary>Draw a pointer arrow and name tag with the hotspot at (layout.HotX, layout.HotY). 'laser' adds a glowing dot at the tip.</summary>
	public static void DrawPointer(Canvas canvas, PointerLayout layout, uint rgb, string label, float scale, bool laser)
	{
		// The arrow has a dark shadow, a white outline and a coloured fill, so it is visible on any background
		var ox = (float)layout.HotX;
		var oy = (float)layout.HotY;
		Span<PointF> shadow = stackalloc PointF[ArrowShape.Length];
		Span<PointF> arrow = stackalloc PointF[ArrowShape.Length];
		for (var i = 0; i != ArrowShape.Length; ++i)
		{
			arrow[i] = new PointF(ox + ArrowShape[i].X * scale, oy + ArrowShape[i].Y * scale);
			shadow[i] = new PointF(arrow[i].X + 1.5f * scale, arrow[i].Y + 1.5f * scale);
		}
		canvas.FillPolygon(shadow, 0x60000000);
		canvas.FillPolygon(arrow, 0xFF000000 | rgb);
		canvas.DrawPolygon(arrow, 0xFFFFFFFF, 1.5f * scale);

		// The laser dot is drawn over the tip so the exact point is obvious while the button is held
		if (laser)
		{
			canvas.FillCircle(ox, oy, 9 * scale, 0x50000000 | rgb);
			canvas.FillCircle(ox, oy, 4.5f * scale, 0xFF000000 | rgb);
			canvas.DrawCircle(ox, oy, 4.5f * scale, 0xFFFFFFFF, 1.2f * scale);
		}

		// Name tag
		var (pill, font) = LabelPill(label, scale);
		DrawPill(canvas, label, font, new RectF(ox + pill.X, oy + pill.Y, pill.Width, pill.Height), rgb, scale);
	}

	/// <summary>Draw an expanding ring for a click. 't' runs from 0 to 1 over the animation. Returns the canvas size used (square).</summary>
	public static int RippleSize(float scale)
	{
		return (int)MathF.Ceiling((44 + 2 * Pad) * scale) * 2;
	}

	/// <summary>Draw a click ripple centred in a canvas of <see cref="RippleSize"/>.</summary>
	public static void DrawRipple(Canvas canvas, uint rgb, float scale, float t)
	{
		// The ring grows and fades out
		var c = canvas.Width / 2f;
		var alpha = (uint)(255 * (1 - t)) << 24;
		canvas.DrawCircle(c, c, (6 + 38 * t) * scale, alpha | rgb, 4 * scale * (1 - 0.5f * t));
		canvas.DrawCircle(c, c, (6 + 38 * t) * scale, (alpha >> 1) & 0xFF000000 | 0xFFFFFF, 1.2f * scale);
	}

	/// <summary>Draw an "attention" pulse (two filled rings) centred in a canvas of <see cref="RippleSize"/>.</summary>
	public static void DrawPulse(Canvas canvas, uint rgb, float scale, float t)
	{
		// Two staggered discs grow and fade, which reads as "look here" rather than "click"
		var c = canvas.Width / 2f;
		for (var i = 0; i != 2; ++i)
		{
			var u = Math.Clamp(t * 1.5f - i * 0.5f, 0, 1);
			if (u <= 0 || u >= 1)
				continue;

			var alpha = (uint)(160 * (1 - u)) << 24;
			canvas.FillCircle(c, c, (4 + 40 * u) * scale, alpha | rgb);
		}
	}

	/// <summary>Draw a fading trail. 'points' are (x, y, age 0..1) in canvas coordinates, oldest first.</summary>
	public static void DrawTrail(Canvas canvas, ReadOnlySpan<(float X, float Y, float Age)> points, uint rgb, float scale)
	{
		// Each segment fades with its age so the trail tapers away behind the pointer
		Span<PointF> seg = stackalloc PointF[2];
		for (var i = 1; i < points.Length; ++i)
		{
			var a = 1 - points[i].Age;
			seg[0] = new PointF(points[i - 1].X, points[i - 1].Y);
			seg[1] = new PointF(points[i].X, points[i].Y);
			canvas.DrawLines(seg, ((uint)(220 * a) << 24) | rgb, (2 + 4 * a) * scale);
		}
	}

	/// <summary>Draw a highlight rectangle of size (w, h) at (Pad, Pad + label height) with an optional label above it. Returns the canvas size and the rectangle offset.</summary>
	public static (int Width, int Height, int OffX, int OffY) LayoutHighlight(int w, int h, string label, float scale)
	{
		// Leave room above the rectangle for the label
		var pad = (int)MathF.Ceiling(Pad * scale + 3 * scale);
		var top = label.Length != 0 ? (int)MathF.Ceiling(LabelPill(label, scale).Pill.Height + 4 * scale) : 0;
		var label_w = label.Length != 0 ? (int)MathF.Ceiling(LabelPill(label, scale).Pill.Width) : 0;
		return (Math.Max(w, label_w) + 2 * pad, h + 2 * pad + top, pad, pad + top);
	}

	/// <summary>Draw a highlight rectangle laid out by <see cref="LayoutHighlight"/>.</summary>
	public static void DrawHighlight(Canvas canvas, int off_x, int off_y, int w, int h, string label, uint rgb, float scale)
	{
		// A soft tint plus a strong border marks the area without hiding it
		canvas.RoundRect(new RectF(off_x, off_y, w, h), 6 * scale, 0x28000000 | rgb, 0xFF000000 | rgb, 4 * scale);
		if (label.Length == 0)
			return;

		var (pill, font) = LabelPill(label, scale);
		DrawPill(canvas, label, font, new RectF(off_x, off_y - pill.Height - 4 * scale, pill.Width, pill.Height), rgb, scale);
	}

	/// <summary>Draw a path with a start dot and an arrowhead at the end. 'points' are in canvas coordinates.</summary>
	public static void DrawPath(Canvas canvas, ReadOnlySpan<PointF> points, uint rgb, float scale)
	{
		// The line, then a dot marking where the path starts
		DrawStroke(canvas, points, rgb, scale);
		canvas.FillCircle(points[0].X, points[0].Y, 5 * scale, 0xFF000000 | rgb);

		// Arrowhead pointing along the last segment
		var end = points[^1];
		var prev = points[^2];
		var dx = end.X - prev.X;
		var dy = end.Y - prev.Y;
		var len = MathF.Sqrt(dx * dx + dy * dy);
		if (len < 0.001f)
			return;

		dx /= len;
		dy /= len;
		var size = 14 * scale;
		Span<PointF> head = [end, new(end.X - dx * size - dy * size * 0.6f, end.Y - dy * size + dx * size * 0.6f), new(end.X - dx * size + dy * size * 0.6f, end.Y - dy * size - dx * size * 0.6f)];
		canvas.FillPolygon(head, 0xFF000000 | rgb);
	}

	/// <summary>Draw a thick line through 'points' (canvas coordinates, at least two).</summary>
	public static void DrawStroke(Canvas canvas, ReadOnlySpan<PointF> points, uint rgb, float scale)
	{
		// A dark underlay keeps the line visible on light and dark backgrounds
		canvas.DrawLines(points, 0x60000000, 7 * scale);
		canvas.DrawLines(points, 0xFF000000 | rgb, 4 * scale);
	}

	/// <summary>
	/// Points on a hand-drawn looking ellipse around (cx, cy) with radii (rx, ry), in the same coordinates as the centre.
	/// The loop starts at the upper left, goes clockwise and runs a little past its start, ending slightly wider, like a circle drawn by hand.
	/// </summary>
	public static PointF[] CirclePath(float cx, float cy, float rx, float ry)
	{
		// Enough points that the ellipse looks smooth at any size the screen can hold
		const int count = 96;
		const float start = -2.2f;
		const float turns = 1.12f;
		var points = new PointF[count];
		for (var i = 0; i != count; ++i)
		{
			var f = i / (float)(count - 1);
			var angle = start + f * turns * 2 * MathF.PI;
			var grow = 1 + 0.07f * f;
			points[i] = new PointF(cx + rx * grow * MathF.Cos(angle), cy + ry * grow * MathF.Sin(angle));
		}
		return points;
	}

	/// <summary>The size of a label pill drawn by <see cref="DrawLabel"/>. Empty labels have no size.</summary>
	public static SizeF LabelSize(string label, float scale)
	{
		if (label.Length == 0)
			return new SizeF(0, 0);

		var (pill, _) = LabelPill(label, scale);
		return new SizeF(pill.Width, pill.Height);
	}

	/// <summary>Draw a label pill with its top-left corner at (x, y). Empty labels draw nothing.</summary>
	public static void DrawLabel(Canvas canvas, string label, float x, float y, uint rgb, float scale)
	{
		var (pill, font) = LabelPill(label, scale);
		DrawPill(canvas, label, font, new RectF(x, y, pill.Width, pill.Height), rgb, scale);
	}

	/// <summary>Padding to leave around a path's bounding box.</summary>
	public static int PathMargin(float scale)
	{
		return (int)MathF.Ceiling(20 * scale);
	}

	/// <summary>Black or white, whichever reads better on 'rgb'.</summary>
	public static uint ContrastText(uint rgb)
	{
		// Perceived brightness using the usual luma weights
		var r = (rgb >> 16) & 0xFF;
		var g = (rgb >> 8) & 0xFF;
		var b = rgb & 0xFF;
		return (r * 299 + g * 587 + b * 114) / 1000 > 150 ? 0xFF000000 : 0xFFFFFFFF;
	}

	/// <summary>The label pill rectangle relative to the pointer hotspot, and the font to draw it with.</summary>
	private static (RectF Pill, Font Font) LabelPill(string label, float scale)
	{
		// Measure on a shared scratch canvas; the pill is sized to the text plus padding
		s_measure ??= new Canvas(1, 1);
		var font = Font.Get(FontFamily, 12.5f * scale, bold: true);
		var size = label.Length != 0 ? s_measure.MeasureText(label, font) : new SizeF(0, 0);
		return (new RectF(14 * scale, 22 * scale, size.Width + 12 * scale, size.Height + 6 * scale), font);
	}

	/// <summary>Draw a filled label pill with centred text.</summary>
	private static void DrawPill(Canvas canvas, string label, Font font, RectF r, uint rgb, float scale)
	{
		// Labels may be empty (e.g. an agent highlight without text)
		if (label.Length == 0)
			return;

		canvas.RoundRect(r, r.Height / 2, 0xFF000000 | rgb, 0xC0FFFFFF, 1.2f * scale);
		canvas.DrawText(label, font, r.X + 6 * scale, r.Y + 3 * scale, ContrastText(rgb));
	}
}
