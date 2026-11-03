using System.Buffers.Binary;

namespace OneBlindMouse.Core;

/// <summary>Flags carried by a pointer sample.</summary>
[Flags]
public enum EPointerFlags : byte
{
	None = 0,

	/// <summary>The pointer is shown. A sample without this flag hides the pointer.</summary>
	Visible = 1 << 0,

	/// <summary>A left click happened at this position (receiver plays a ripple).</summary>
	Click = 1 << 1,

	/// <summary>A right click happened at this position (receiver plays a pulse).</summary>
	Pulse = 1 << 2,

	/// <summary>The left button is held (receiver draws a laser trail).</summary>
	Laser = 1 << 3,
}

/// <summary>
/// One pointer sample, sent as a fixed 12-byte binary WebSocket frame. Layout (little endian):
/// [0] type (0x01), [1] from member (set by the relay), [2] to member (0 = everyone), [3] reserved (0),
/// [4..5] sequence number, [6..7] x, [8..9] y, [10] flags, [11] size percent.
/// X and Y are normalised to the receiver's target area: 0 is the left/top edge and 65535 the right/bottom edge.
/// </summary>
public readonly record struct PointerFrame(byte From, byte To, ushort Seq, ushort X, ushort Y, EPointerFlags Flags, byte Size)
{
	/// <summary>Size of an encoded frame in bytes.</summary>
	public const int Length = 12;

	/// <summary>Frame type byte for pointer samples.</summary>
	public const byte Type = 0x01;

	/// <summary>Allowed range of <see cref="Size"/>, as a percentage of the default pointer size.</summary>
	public const byte MinSize = 50, MaxSize = 250, DefaultSize = 100;

	/// <summary>All defined flag bits.</summary>
	private const EPointerFlags AllFlags = EPointerFlags.Visible | EPointerFlags.Click | EPointerFlags.Pulse | EPointerFlags.Laser;

	/// <summary>Write the frame into <paramref name="dst"/>, which must be at least <see cref="Length"/> bytes.</summary>
	public void Write(Span<byte> dst)
	{
		dst[0] = Type;
		dst[1] = From;
		dst[2] = To;
		dst[3] = 0;
		BinaryPrimitives.WriteUInt16LittleEndian(dst[4..], Seq);
		BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], X);
		BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], Y);
		dst[10] = (byte)Flags;
		dst[11] = Size;
	}

	/// <summary>Encode the frame into a new array.</summary>
	public byte[] ToArray()
	{
		var buf = new byte[Length];
		Write(buf);
		return buf;
	}

	/// <summary>Decode and validate a frame. Returns false for wrong length, type, reserved bits, unknown flags or out-of-range size.</summary>
	public static bool TryRead(ReadOnlySpan<byte> src, out PointerFrame frame)
	{
		// Exact length and known type only
		frame = default;
		if (src.Length != Length || src[0] != Type || src[3] != 0)
			return false;

		// Unknown flags and out-of-range sizes indicate a corrupt or hostile sender
		var flags = (EPointerFlags)src[10];
		var size = src[11];
		if ((flags & ~AllFlags) != 0 || size < MinSize || size > MaxSize)
			return false;

		frame = new PointerFrame(
			From: src[1],
			To: src[2],
			Seq: BinaryPrimitives.ReadUInt16LittleEndian(src[4..]),
			X: BinaryPrimitives.ReadUInt16LittleEndian(src[6..]),
			Y: BinaryPrimitives.ReadUInt16LittleEndian(src[8..]),
			Flags: flags,
			Size: size
		);
		return true;
	}

	/// <summary>Convert a normalised fraction in [0,1] to the wire coordinate range. Values outside the range are clamped.</summary>
	public static ushort Quantise(double fraction)
	{
		return (ushort)Math.Round(Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * ushort.MaxValue);
	}

	/// <summary>Convert a wire coordinate to a fraction in [0,1].</summary>
	public static double Fraction(ushort value)
	{
		return value / (double)ushort.MaxValue;
	}

	/// <summary>True if sequence number <paramref name="a"/> is after <paramref name="b"/>, allowing for 16-bit wrap-around.</summary>
	public static bool IsNewer(ushort a, ushort b)
	{
		return (short)(a - b) > 0;
	}
}
