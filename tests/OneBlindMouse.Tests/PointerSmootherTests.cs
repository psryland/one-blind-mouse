using OneBlindMouse.Core;

namespace OneBlindMouse.Tests;

/// <summary>Tests for pointer playback smoothing.</summary>
public class PointerSmootherTests
{
	private const double Delay = 0.1;

	/// <summary>A visible sample from member 1.</summary>
	private static PointerFrame Frame(ushort seq, double x, double y = 0, EPointerFlags flags = EPointerFlags.Visible, byte size = 100)
	{
		return new PointerFrame(1, 0, seq, PointerFrame.Quantise(x), PointerFrame.Quantise(y), flags, size);
	}

	[Fact]
	public void InterpolatesBetweenSamples()
	{
		// Playback runs 'Delay' behind, so at 0.15 it is half way between the samples at 0 and 0.1
		var smoother = new PointerSmoother(Delay, stale: 2);
		Assert.True(smoother.Push(Frame(1, 0, 0), 0.0));
		Assert.True(smoother.Push(Frame(2, 1, 0.5), 0.1));
		Assert.True(smoother.TrySample(1, 0.15, out var state));
		Assert.Equal(0.5, state.X, 3);
		Assert.Equal(0.25, state.Y, 3);
	}

	[Fact]
	public void HoldsTheNearestSampleOutsideTheBufferedRange()
	{
		var smoother = new PointerSmoother(Delay, stale: 2);
		smoother.Push(Frame(1, 0.2), 0.0);
		smoother.Push(Frame(2, 0.8), 0.1);
		Assert.True(smoother.TrySample(1, 0.05, out var before));
		Assert.Equal(0.2, before.X, 3);
		Assert.True(smoother.TrySample(1, 1.0, out var after));
		Assert.Equal(0.8, after.X, 3);
	}

	[Fact]
	public void DropsOutOfOrderSamples()
	{
		var smoother = new PointerSmoother(Delay, stale: 2);
		Assert.True(smoother.Push(Frame(5, 0.1), 0.0));
		Assert.False(smoother.Push(Frame(4, 0.9), 0.01));
		Assert.False(smoother.Push(Frame(5, 0.9), 0.02));
		Assert.True(smoother.TrySample(1, 1.0, out var state));
		Assert.Equal(0.1, state.X, 3);
	}

	[Fact]
	public void AcceptsSequenceWrapAround()
	{
		var smoother = new PointerSmoother(Delay, stale: 2);
		Assert.True(smoother.Push(Frame(65535, 0.1), 0.0));
		Assert.True(smoother.Push(Frame(0, 0.9), 0.01));
		Assert.True(smoother.TrySample(1, 1.0, out var state));
		Assert.Equal(0.9, state.X, 3);
	}

	[Fact]
	public void HidesStalePointers()
	{
		// After going quiet the pointer is hidden and its buffered samples are discarded
		var smoother = new PointerSmoother(Delay, stale: 2);
		smoother.Push(Frame(1, 0.5), 0.0);
		Assert.True(smoother.TrySample(1, 1.9, out _));
		Assert.False(smoother.TrySample(1, 2.5, out _));
		Assert.False(smoother.TrySample(1, 1.0, out _));

		// A fresh sample shows it again
		smoother.Push(Frame(2, 0.6), 3.0);
		Assert.True(smoother.TrySample(1, 3.2, out var state));
		Assert.Equal(0.6, state.X, 3);
	}

	[Fact]
	public void HidesOnHiddenSampleWithoutSlidingIntoIt()
	{
		var smoother = new PointerSmoother(Delay, stale: 2);
		smoother.Push(Frame(1, 0.2), 0.0);
		smoother.Push(Frame(2, 0.9, flags: EPointerFlags.None), 0.1);

		// Between the samples the pointer holds its last visible position
		Assert.True(smoother.TrySample(1, 0.15, out var state));
		Assert.Equal(0.2, state.X, 3);

		// Once the hidden sample is reached the pointer disappears
		Assert.False(smoother.TrySample(1, 0.3, out _));
	}

	[Fact]
	public void PassesLaserAndSizeThrough()
	{
		var smoother = new PointerSmoother(Delay, stale: 2);
		smoother.Push(Frame(1, 0.5, flags: EPointerFlags.Visible | EPointerFlags.Laser, size: 200), 0.0);
		Assert.True(smoother.TrySample(1, 0.5, out var state));
		Assert.True(state.Laser);
		Assert.Equal(200, state.Size);
	}

	[Fact]
	public void UnknownAndRemovedMembersAreHidden()
	{
		var smoother = new PointerSmoother(Delay, stale: 2);
		Assert.False(smoother.TrySample(9, 0, out _));
		smoother.Push(Frame(1, 0.5), 0.0);
		smoother.Remove(1);
		Assert.False(smoother.TrySample(1, 0.5, out _));
		Assert.Empty(smoother.Members);
	}
}
