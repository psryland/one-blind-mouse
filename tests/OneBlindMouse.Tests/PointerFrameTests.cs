using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Tests for the binary pointer frame encoding.</summary>
public class PointerFrameTests
{
	private static readonly PointerFrame Sample = new(From: 3, To: 7, Seq: 0xBEEF, X: 1234, Y: 65535, Flags: EPointerFlags.Visible | EPointerFlags.Laser, Size: 150);

	[Fact]
	public void RoundTrips()
	{
		var bytes = Sample.ToArray();
		Assert.Equal(PointerFrame.Length, bytes.Length);
		Assert.Equal(PointerFrame.Type, bytes[0]);
		Assert.True(PointerFrame.TryRead(bytes, out var frame));
		Assert.Equal(Sample, frame);
	}

	[Fact]
	public void UsesLittleEndianLayout()
	{
		var bytes = Sample.ToArray();
		Assert.Equal(new byte[] { 0x01, 3, 7, 0, 0xEF, 0xBE, 0xD2, 0x04, 0xFF, 0xFF, 0x09, 150 }, bytes);
	}

	[Theory]
	[InlineData(11)]
	[InlineData(13)]
	[InlineData(0)]
	public void RejectsWrongLength(int length)
	{
		var bytes = new byte[length];
		Sample.ToArray().AsSpan(0, Math.Min(length, PointerFrame.Length)).CopyTo(bytes);
		Assert.False(PointerFrame.TryRead(bytes, out _));
	}

	[Theory]
	[InlineData(0, 0x02)]
	[InlineData(3, 0x01)]
	[InlineData(10, 0x10)]
	[InlineData(10, 0x81)]
	[InlineData(11, 49)]
	[InlineData(11, 251)]
	public void RejectsInvalidFields(int offset, byte value)
	{
		var bytes = Sample.ToArray();
		bytes[offset] = value;
		Assert.False(PointerFrame.TryRead(bytes, out _));
	}

	[Theory]
	[InlineData(1, 0, true)]
	[InlineData(0, 1, false)]
	[InlineData(0, 65535, true)]
	[InlineData(65535, 0, false)]
	[InlineData(5, 5, false)]
	[InlineData(100, 65500, true)]
	public void ComparesSequenceNumbersWithWrapAround(int a, int b, bool expected)
	{
		Assert.Equal(expected, PointerFrame.IsNewer((ushort)a, (ushort)b));
	}

	[Fact]
	public void QuantisesAndClamps()
	{
		Assert.Equal(0, PointerFrame.Quantise(-1));
		Assert.Equal(0, PointerFrame.Quantise(double.NaN));
		Assert.Equal(65535, PointerFrame.Quantise(2));
		Assert.Equal(65535, PointerFrame.Quantise(1));
		Assert.Equal(0.5, PointerFrame.Fraction(PointerFrame.Quantise(0.5)), 4);
	}
}
