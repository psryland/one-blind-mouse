namespace OneBlindMouse.Relay;

/// <summary>
/// A token bucket rate limiter. Tokens refill continuously at <c>rate</c> per second up to <c>capacity</c>, and each action takes some tokens.
/// The capacity is the burst size: how many actions may happen at once after a quiet period. Not thread safe.
/// </summary>
public sealed class TokenBucket
{
	private readonly TimeProvider m_time;
	private readonly double m_capacity;
	private readonly double m_rate;
	private double m_tokens;
	private long m_last;

	/// <summary>Create a full bucket. <paramref name="rate"/> is in tokens per second.</summary>
	public TokenBucket(TimeProvider time, double capacity, double rate)
	{
		// Start full so a new connection can do its initial burst of work
		m_time = time;
		m_capacity = capacity;
		m_rate = rate;
		m_tokens = capacity;
		m_last = time.GetTimestamp();
	}

	/// <summary>Take <paramref name="cost"/> tokens. Returns false, and takes nothing, if there are not enough tokens.</summary>
	public bool TryTake(double cost = 1.0)
	{
		// Refill for the time since the last call, then spend if possible
		Refill();
		if (m_tokens < cost)
			return false;

		m_tokens -= cost;
		return true;
	}

	/// <summary>Add tokens earned since the last refill.</summary>
	private void Refill()
	{
		// Elapsed time comes from the time provider so tests can control it
		var now = m_time.GetTimestamp();
		var elapsed = m_time.GetElapsedTime(m_last, now).TotalSeconds;
		m_last = now;
		m_tokens = Math.Min(m_capacity, m_tokens + elapsed * m_rate);
	}
}
