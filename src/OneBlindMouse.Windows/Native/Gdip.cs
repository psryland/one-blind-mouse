using System.Runtime.InteropServices;

namespace OneBlindMouse.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PointF
{
	public float X;
	public float Y;

	public PointF(float x, float y)
	{
		X = x;
		Y = y;
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct RectF
{
	public float X;
	public float Y;
	public float Width;
	public float Height;

	public RectF(float x, float y, float w, float h)
	{
		X = x;
		Y = y;
		Width = w;
		Height = h;
	}
}

[StructLayout(LayoutKind.Sequential)]
internal struct GdiplusStartupInput
{
	public uint GdiplusVersion;
	public nint DebugEventCallback;
	public int SuppressBackgroundThread;
	public int SuppressExternalCodecs;
}

/// <summary>The GDI+ flat API. Every function returns a status code where 0 means success.</summary>
internal static unsafe partial class Gdip
{
	public const int PixelFormat32bppPARGB = 0x000E200B;
	public const int SmoothingModeAntiAlias = 4, PixelOffsetModeHalf = 4, TextRenderingHintAntiAlias = 4;
	public const int UnitPixel = 2, FontStyleRegular = 0, FontStyleBold = 1;
	public const int LineJoinRound = 2, LineCapRound = 2;

	[LibraryImport("gdiplus.dll")]
	public static partial int GdiplusStartup(out nint token, in GdiplusStartupInput input, nint output);

	[LibraryImport("gdiplus.dll")]
	public static partial void GdiplusShutdown(nint token);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, nint scan0, out nint bitmap);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDisposeImage(nint image);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipCreateHICONFromBitmap(nint bitmap, out nint icon);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDeleteGraphics(nint graphics);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipSetSmoothingMode(nint graphics, int mode);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipSetTextRenderingHint(nint graphics, int hint);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipGraphicsClear(nint graphics, uint argb);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipCreateSolidFill(uint argb, out nint brush);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDeleteBrush(nint brush);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDeletePen(nint pen);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipSetPenLineJoin(nint pen, int join);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipSetPenStartCap(nint pen, int cap);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipSetPenEndCap(nint pen, int cap);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipFillPolygon(nint graphics, nint brush, PointF* points, int count, int fill_mode);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDrawPolygon(nint graphics, nint pen, PointF* points, int count);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDrawLines(nint graphics, nint pen, PointF* points, int count);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDrawEllipse(nint graphics, nint pen, float x, float y, float w, float h);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipCreatePath(int fill_mode, out nint path);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDeletePath(nint path);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipClosePathFigure(nint path);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipFillPath(nint graphics, nint brush, nint path);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDrawPath(nint graphics, nint pen, nint path);

	[LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDeleteFontFamily(nint family);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipCreateFont(nint family, float size, int style, int unit, out nint font);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipDeleteFont(nint font);

	[LibraryImport("gdiplus.dll")]
	public static partial int GdipStringFormatGetGenericTypographic(out nint format);

	[LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial int GdipDrawString(nint graphics, string text, int length, nint font, RectF* layout, nint format, nint brush);

	[LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
	public static partial int GdipMeasureString(nint graphics, string text, int length, nint font, RectF* layout, nint format, RectF* bounds, out int chars_fitted, out int lines_filled);
}
