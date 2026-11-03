namespace OneBlindMouse.Core;

/// <summary>The smoothed state of one remote pointer at a moment in time.</summary>
/// <param name="X">Horizontal position as a fraction of the target area (0 = left, 1 = right).</param>
/// <param name="Y">Vertical position as a fraction of the target area (0 = top, 1 = bottom).</param>
/// <param name="Laser">True while the sender holds the left button.</param>
/// <param name="Size">Pointer size as a percentage of the default size.</param>
public readonly record struct PointerState(double X, double Y, bool Laser, byte Size);

/// <summary>
/// Turns irregular network pointer samples into smooth motion.
/// Samples are played back a fixed delay behind real time, and positions between samples are linearly interpolated.
/// The delay absorbs network jitter at the cost of a small, constant lag. Not thread safe.
/// </summary>
public sealed class PointerSmoother
{
	/// <summary>Buffered samples for one member, oldest first.</summary>
	private sealed class Track
	{
		public readonly List<(double Time, PointerFrame Frame)> Samples = [];
		public ushort LastSeq;
		public double LastArrival;
	}

	private readonly Dictionary<int, Track> m_tracks = [];
	private readonly double m_delay;
	private readonly double m_stale;

	/// <summary>
	/// Create a smoother. <paramref name="delay"/> is the playback delay in seconds.
	/// A pointer is hidden when no sample arrives for <paramref name="stale"/> seconds, so senders must repeat their last sample more often than that.
	/// </summary>
	public PointerSmoother(double delay = 0.066, double stale = 2.0)
	{
		m_delay = delay;
		m_stale = stale;
	}

	/// <summary>Members that currently have buffered samples.</summary>
	public IEnumerable<int> Members => m_tracks.Keys;

	/// <summary>Add a sample that arrived at time <paramref name="now"/> (seconds, any monotonic clock). Returns false if the sample is out of date.</summary>
	public bool Push(PointerFrame frame, double now)
	{
		// Find or create the member's track. The first sample is always accepted.
		if (!m_tracks.TryGetValue(frame.From, out var track))
		{
			track = new Track { LastSeq = (ushort)(frame.Seq - 1) };
			m_tracks.Add(frame.From, track);
		}

		// Drop samples that arrive after a newer one; they would make the pointer jump backwards
		if (!PointerFrame.IsNewer(frame.Seq, track.LastSeq))
			return false;

		track.LastSeq = frame.Seq;
		track.LastArrival = now;
		track.Samples.Add((now, frame));
		return true;
	}

	/// <summary>Forget a member, e.g. when it leaves the session.</summary>
	public void Remove(int member)
	{
		m_tracks.Remove(member);
	}

	/// <summary>Forget all members.</summary>
	public void Clear()
	{
		m_tracks.Clear();
	}

	/// <summary>Get the pointer state of <paramref name="member"/> at time <paramref name="now"/>. Returns false if the pointer is hidden.</summary>
	public bool TrySample(int member, double now, out PointerState state)
	{
		// No data, or the sender went quiet: hide the pointer
		state = default;
		if (!m_tracks.TryGetValue(member, out var track) || track.Samples.Count == 0)
			return false;

		if (now - track.LastArrival > m_stale)
		{
			track.Samples.Clear();
			return false;
		}

		// Discard samples that are fully in the past, keeping the one just before the playback time for interpolation
		var t = now - m_delay;
		var samples = track.Samples;
		var drop = 0;
		while (drop + 1 < samples.Count && samples[drop + 1].Time <= t)
			++drop;

		if (drop != 0)
			samples.RemoveRange(0, drop);

		// Before the first sample or after the last one, hold the nearest sample. Otherwise interpolate between the two around 't'.
		var (t0, f0) = samples[0];
		if (samples.Count == 1 || t <= t0)
			return ToState(f0, f0, 0, out state);

		var (t1, f1) = samples[1];
		var u = Math.Clamp((t - t0) / Math.Max(t1 - t0, 1e-6), 0, 1);
		return ToState(f0, f1, u, out state);
	}

	/// <summary>Blend two samples. Visibility and button state come from the earlier sample so changes happen at the right time.</summary>
	private static bool ToState(PointerFrame a, PointerFrame b, double u, out PointerState state)
	{
		// A hidden sample hides the pointer until the next visible one is reached
		state = default;
		if (!a.Flags.HasFlag(EPointerFlags.Visible))
			return false;

		// Do not slide from a visible position into a hidden one; hold instead
		if (!b.Flags.HasFlag(EPointerFlags.Visible))
			b = a;

		var x = PointerFrame.Fraction(a.X) + (PointerFrame.Fraction(b.X) - PointerFrame.Fraction(a.X)) * u;
		var y = PointerFrame.Fraction(a.Y) + (PointerFrame.Fraction(b.Y) - PointerFrame.Fraction(a.Y)) * u;
		state = new PointerState(x, y, a.Flags.HasFlag(EPointerFlags.Laser), a.Size);
		return true;
	}
}
