using System.Diagnostics;
using OneBlindMouse.Core;
using OneBlindMouse.Windows.Native;
using OneBlindMouse.Windows.Ui;

namespace OneBlindMouse.Windows;

/// <summary>
/// Draws remote members' pointers, click effects and local agent annotations on this screen.
/// Each item is a small sprite window, so only the pixels that change are redrawn. A 16 ms timer runs only while something is animating.
/// UI thread only.
/// </summary>
internal sealed class Overlay : IDisposable
{
	/// <summary>Default colour for agent annotations (amber, distinct from the member palette).</summary>
	public const uint AgentColour = 0xFFB000;

	/// <summary>How long a laser trail point stays visible, in seconds.</summary>
	private const double TrailLife = 0.5;

	/// <summary>Durations of the click effects, in seconds.</summary>
	private const double RippleTime = 0.5, PulseTime = 0.9;

	/// <summary>Delay between the two ripples that prompt the user to click, in seconds.</summary>
	private const double ClickPromptGap = 0.35;

	/// <summary>A pointer (a remote member's or the agent's) and its trail.</summary>
	private sealed class Remote(Sprite pointer, Sprite trail)
	{
		public readonly Sprite Pointer = pointer;
		public readonly Sprite Trail = trail;
		public readonly List<(double Time, int X, int Y)> TrailPoints = [];
		public Art.PointerLayout Layout;
		public (string Name, uint Rgb, float Scale, bool Laser) Drawn;
	}

	/// <summary>A short animation (ripple or pulse) at a screen position. It is not drawn before 'Start', so effects can be staggered.</summary>
	private sealed record Effect(Sprite Sprite, bool Pulse, int X, int Y, uint Rgb, float Scale, double Start);

	/// <summary>An agent annotation that expires at 'Expires' (never if infinite).</summary>
	private sealed class Annotation(Sprite sprite, double expires)
	{
		public readonly Sprite Sprite = sprite;
		public double Expires = expires;
	}

	/// <summary>
	/// One leg of the agent pointer's motion: the pointer moves along 'Path' (screen pixels) over 'Duration' seconds, at constant speed along the path
	/// after the optional ease-in-out. 'Progress' receives the part of the path covered so far each frame; 'Arrived' runs once at the end.
	/// </summary>
	private sealed class Leg
	{
		private readonly PointF[] m_path;
		private readonly float[] m_distance;

		/// <summary>Create a leg along 'path' (at least one point).</summary>
		public Leg(PointF[] path, double duration, bool ease, Action<PointF[]>? progress = null, Action? arrived = null, Action? abandoned = null)
		{
			// Cumulative distance along the path, for constant-speed movement
			m_path = path;
			m_distance = new float[path.Length];
			for (var i = 1; i != path.Length; ++i)
				m_distance[i] = m_distance[i - 1] + MathF.Sqrt(Sq(path[i].X - path[i - 1].X) + Sq(path[i].Y - path[i - 1].Y));

			Duration = duration;
			Ease = ease;
			Progress = progress;
			Arrived = arrived;
			Abandoned = abandoned;
		}

		public double Duration { get; }
		public bool Ease { get; }
		public Action<PointF[]>? Progress { get; }
		public Action? Arrived { get; }

		/// <summary>Runs instead of 'Arrived' if a new motion replaces this leg before it finishes.</summary>
		public Action? Abandoned { get; }

		/// <summary>When the leg started; NaN until it becomes the current leg.</summary>
		public double Start { get; set; } = double.NaN;

		/// <summary>The path from its start to fraction 'u' (0..1) of its length. The last point is the pointer position.</summary>
		public PointF[] Prefix(float u)
		{
			// Find the segment containing the distance and interpolate within it
			var target = u * m_distance[^1];
			var i = 1;
			while (i < m_path.Length && m_distance[i] < target)
				++i;

			if (i >= m_path.Length)
				return m_path;

			var seg = m_distance[i] - m_distance[i - 1];
			var f = seg > 0 ? (target - m_distance[i - 1]) / seg : 0;
			var result = new PointF[i + 1];
			Array.Copy(m_path, result, i);
			result[i] = new PointF(m_path[i - 1].X + (m_path[i].X - m_path[i - 1].X) * f, m_path[i - 1].Y + (m_path[i].Y - m_path[i - 1].Y) * f);
			return result;
		}

		/// <summary>Square of a number.</summary>
		private static float Sq(float v)
		{
			return v * v;
		}
	}

	private readonly UiDispatcher m_ui;
	private readonly Target m_target;
	private readonly PointerSmoother m_smoother = new();
	private readonly Stopwatch m_clock = Stopwatch.StartNew();
	private readonly Dictionary<int, Remote> m_remotes = [];
	private readonly List<Effect> m_effects = [];
	private readonly List<Annotation> m_annotations = [];
	private readonly Queue<Leg> m_agent_legs = [];
	private Remote? m_agent;
	private PointF m_agent_pos;
	private bool m_agent_shown;
	private string m_agent_label = string.Empty;
	private uint m_agent_rgb = AgentColour;
	private double m_agent_hold;
	private double m_agent_expires = double.PositiveInfinity;
	private Dictionary<int, MemberInfo> m_members = [];
	private int m_me;
	private bool m_hide_from_capture;
	private nuint m_timer;

	/// <summary>Create the overlay. Remote coordinates map onto 'target'.</summary>
	public Overlay(UiDispatcher ui, Target target, bool hide_from_capture)
	{
		m_ui = ui;
		m_target = target;
		m_hide_from_capture = hide_from_capture;
	}

	/// <summary>Seconds since the overlay was created.</summary>
	private double Now => m_clock.Elapsed.TotalSeconds;

	/// <summary>Whether overlay windows are excluded from screen capture. Applies to windows created after the change.</summary>
	public bool HideFromCapture
	{
		get => m_hide_from_capture;
		set
		{
			// Existing sprites are recreated so the setting applies immediately
			m_hide_from_capture = value;
			ClearRemotes();
			ClearAgent();
		}
	}

	/// <summary>Update the member list. Pointers of members who left are removed.</summary>
	public void OnSession(SessionSnapshot snap)
	{
		// Keep a lookup of names and colours by member id
		m_me = snap.Me;
		m_members = snap.Members.ToDictionary(m => m.Id);

		// Remove pointers whose member is gone (or after switching rooms, where ids are reused)
		foreach (var id in m_remotes.Keys.Where(id => !m_members.ContainsKey(id) || id == m_me).ToList())
			RemoveRemote(id);
	}

	/// <summary>Handle a pointer sample from another member.</summary>
	public void OnFrame(PointerFrame frame)
	{
		// Ignore samples from unknown members (e.g. sent just before a room switch)
		if (!m_members.TryGetValue(frame.From, out var member) || frame.From == m_me)
			return;

		if (!m_smoother.Push(frame, Now))
			return;

		// Click effects play immediately at the sample position rather than waiting for the smoothing delay
		if ((frame.Flags & (EPointerFlags.Click | EPointerFlags.Pulse)) != 0 && m_target.TryGetRect(out var rc))
		{
			var x = rc.Left + (int)Math.Round(PointerFrame.Fraction(frame.X) * (rc.Width - 1));
			var y = rc.Top + (int)Math.Round(PointerFrame.Fraction(frame.Y) * (rc.Height - 1));
			var scale = Scale(x, y, frame.Size);
			AddEffect((frame.Flags & EPointerFlags.Pulse) != 0, x, y, Palette.Rgb(member.Slot), scale);
		}
		EnsureTimer();
	}

	/// <summary>Glide the agent pointer to a screen position, show 'label' beside it and pulse on arrival. 'duration' is in seconds after arrival; 0 means until cleared.</summary>
	public void AgentPointAt(int x, int y, string label, uint rgb, double duration)
	{
		// The pulse on arrival draws the eye to the exact spot
		AgentMove(label, rgb, duration, GlideTo(new PointF(x, y), () => AddEffect(pulse: true, x, y, rgb, Scale(x, y, PointerFrame.DefaultSize))));
	}

	/// <summary>Glide the agent pointer to a screen position and play click ripples there, prompting the user to click. Nothing is clicked.</summary>
	public void AgentClick(int x, int y, string label, uint rgb, double duration)
	{
		// Two ripples read as "click here" and are harder to miss than one
		AgentMove(label, rgb, duration, GlideTo(new PointF(x, y), () =>
		{
			// Stagger the ripples so they look like a repeated click
			var scale = Scale(x, y, PointerFrame.DefaultSize);
			AddEffect(pulse: false, x, y, rgb, scale);
			AddEffect(pulse: false, x, y, rgb, scale, ClickPromptGap);
		}));
	}

	/// <summary>Glide the agent pointer to a screen rectangle and draw a hand-drawn ellipse around it, with an optional label. The ellipse stays for 'duration' seconds after it is drawn; 0 means until cleared.</summary>
	public void AgentCircle(RECT rect, string label, uint rgb, double duration)
	{
		// The ellipse passes just outside the rectangle's corners (an ellipse through the corners has radii of half the size times root 2)
		var cx = rect.Left + rect.Width / 2f;
		var cy = rect.Top + rect.Height / 2f;
		var scale = Scale((int)cx, (int)cy, PointerFrame.DefaultSize);
		var rx = rect.Width / 2f * MathF.Sqrt(2) + 6 * scale;
		var ry = rect.Height / 2f * MathF.Sqrt(2) + 6 * scale;
		var path = Art.CirclePath(cx, cy, rx, ry);

		// The sprite covers the ellipse, the stroke width and the label above it
		var margin = Art.PathMargin(scale);
		var label_size = Art.LabelSize(label, scale);
		var label_gap = label.Length != 0 ? (int)MathF.Ceiling(label_size.Height + 4 * scale) : 0;
		var l = (int)MathF.Floor(path.Min(p => p.X)) - margin;
		var t = (int)MathF.Floor(path.Min(p => p.Y)) - margin - label_gap;
		var r = Math.Max((int)MathF.Ceiling(path.Max(p => p.X)) + margin, l + margin + (int)MathF.Ceiling(label_size.Width));
		var b = (int)MathF.Ceiling(path.Max(p => p.Y)) + margin;
		var sprite = new Sprite(m_hide_from_capture);
		var annotation = AddAnnotation(sprite, 0);

		// Redraw the part of the ellipse covered so far, so the pointer appears to draw it
		void Draw(PointF[] drawn)
		{
			// Work in sprite-local coordinates
			var canvas = sprite.Begin(r - l, b - t);
			var local = drawn.Select(p => new PointF(p.X - l, p.Y - t)).ToArray();
			if (local.Length >= 2)
				Art.DrawStroke(canvas, local, rgb, scale);

			Art.DrawLabel(canvas, label, margin, 0, rgb, scale);
			sprite.Present(l, t);
		}

		// Start the ellipse's expiry once it is complete
		void Drawn()
		{
			annotation.Expires = duration > 0 ? Now + duration : double.PositiveInfinity;
		}

		// Drawing speed follows the ellipse size, within limits that keep it quick but followable
		var perimeter = MathF.PI * (rx + ry);
		var draw_time = Math.Clamp(0.6 + perimeter / 4000.0, 0.7, 1.4);
		AgentMove(string.Empty, rgb, duration, GlideTo(path[0], null), new Leg(path, draw_time, ease: true, Draw, Drawn, abandoned: () =>
		{
			// Finish the ellipse at once if another motion interrupts it, so it isn't left half drawn forever
			Draw(path);
			Drawn();
		}));
	}

	/// <summary>Outline a screen rectangle with an optional label.</summary>
	public void AgentHighlight(RECT rect, string label, uint rgb, double duration)
	{
		// The sprite extends beyond the rectangle to fit the border and label
		var sprite = new Sprite(m_hide_from_capture);
		var scale = Scale(rect.Left, rect.Top, PointerFrame.DefaultSize);
		var (w, h, ox, oy) = Art.LayoutHighlight(rect.Width, rect.Height, label, scale);
		var canvas = sprite.Begin(w, h);
		Art.DrawHighlight(canvas, ox, oy, rect.Width, rect.Height, label, rgb, scale);
		sprite.Present(rect.Left - ox, rect.Top - oy);
		AddAnnotation(sprite, duration);
	}

	/// <summary>Draw a path through screen points, ending in an arrowhead.</summary>
	public void AgentPath(IReadOnlyList<POINT> points, uint rgb, double duration)
	{
		// Size the sprite to the path's bounding box plus room for the line width and arrowhead
		var scale = Scale(points[0].X, points[0].Y, PointerFrame.DefaultSize);
		var margin = Art.PathMargin(scale);
		var l = points.Min(p => p.X) - margin;
		var t = points.Min(p => p.Y) - margin;
		var r = points.Max(p => p.X) + margin;
		var b = points.Max(p => p.Y) + margin;

		// Draw in sprite-local coordinates
		var sprite = new Sprite(m_hide_from_capture);
		var canvas = sprite.Begin(r - l, b - t);
		var local = points.Select(p => new PointF(p.X - l, p.Y - t)).ToArray();
		Art.DrawPath(canvas, local, rgb, scale);
		sprite.Present(l, t);
		AddAnnotation(sprite, duration);
	}

	/// <summary>Remove all agent annotations and hide the agent pointer.</summary>
	public void ClearAgent()
	{
		// Pending motion is dropped without running its callbacks, because its sprites are being removed too
		m_agent_legs.Clear();
		foreach (var a in m_annotations)
			a.Sprite.Dispose();

		m_annotations.Clear();
		m_agent?.Pointer.Dispose();
		m_agent?.Trail.Dispose();
		m_agent = null;
		m_agent_shown = false;
	}

	/// <summary>Remove everything.</summary>
	public void Dispose()
	{
		// Stop animating and destroy all sprites
		m_ui.StopTimer(ref m_timer);
		ClearRemotes();
		ClearAgent();
		foreach (var e in m_effects)
			e.Sprite.Dispose();

		m_effects.Clear();
	}

	/// <summary>Scale factor for drawing at a screen position: monitor DPI times the pointer size percentage.</summary>
	private static float Scale(int x, int y, byte size_percent)
	{
		return Monitors.DpiAt(x, y) / 96f * size_percent / 100f;
	}

	/// <summary>Start a ripple or pulse animation, optionally 'delay' seconds from now.</summary>
	private void AddEffect(bool pulse, int x, int y, uint rgb, float scale, double delay = 0)
	{
		m_effects.Add(new Effect(new Sprite(m_hide_from_capture), pulse, x, y, rgb, scale, Now + delay));
		EnsureTimer();
	}

	/// <summary>Track an annotation so it can expire or be cleared.</summary>
	private Annotation AddAnnotation(Sprite sprite, double duration)
	{
		var annotation = new Annotation(sprite, duration > 0 ? Now + duration : double.PositiveInfinity);
		m_annotations.Add(annotation);
		EnsureTimer();
		return annotation;
	}

	/// <summary>
	/// Replace the agent pointer's motion with 'legs'. The pointer shows 'label' in 'rgb' and hides 'hold' seconds after the last leg ends (0 = stays until cleared).
	/// A hidden pointer starts from the user's cursor, where the user is most likely looking.
	/// </summary>
	private void AgentMove(string label, uint rgb, double hold, params Leg[] legs)
	{
		// Legs of an interrupted motion are abandoned; the new motion starts from wherever the pointer is now
		foreach (var old in m_agent_legs)
			old.Abandoned?.Invoke();

		m_agent_legs.Clear();
		if (!m_agent_shown)
			m_agent_pos = CursorPos();

		m_agent ??= new Remote(new Sprite(m_hide_from_capture), new Sprite(m_hide_from_capture));
		m_agent_shown = true;
		m_agent_label = label;
		m_agent_rgb = rgb;
		m_agent_hold = hold;
		m_agent_expires = double.PositiveInfinity;
		foreach (var leg in legs)
			m_agent_legs.Enqueue(leg);

		EnsureTimer();
	}

	/// <summary>A leg that glides the agent pointer from its position at the time of the call to 'to'.</summary>
	private Leg GlideTo(PointF to, Action? arrived)
	{
		// Longer glides take a little longer, but stay short enough not to keep the user waiting
		var from = m_agent_shown ? m_agent_pos : CursorPos();
		var distance = MathF.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
		var duration = Math.Clamp(0.25 + distance / 3000.0, 0.3, 0.8);
		return new Leg([from, to], duration, ease: true, arrived: arrived);
	}

	/// <summary>The user's cursor position.</summary>
	private static PointF CursorPos()
	{
		User32.GetCursorPos(out var cursor);
		return new PointF(cursor.X, cursor.Y);
	}

	/// <summary>Start the animation timer if it is not running.</summary>
	private void EnsureTimer()
	{
		// 16 ms is about one frame on a 60 Hz display
		if (m_timer == 0)
			m_timer = m_ui.StartTimer(16, Tick);
	}

	/// <summary>Advance all animations by one frame. Stops the timer when nothing needs it.</summary>
	private void Tick()
	{
		// Each part reports whether it still needs ticking
		var now = Now;
		var busy = TickRemotes(now);
		busy |= TickAgent(now);
		busy |= TickEffects(now);
		busy |= TickAnnotations(now);
		if (!busy)
			m_ui.StopTimer(ref m_timer);
	}

	/// <summary>Position remote pointers from the smoothed samples.</summary>
	private bool TickRemotes(double now)
	{
		// Without a visible target (e.g. the shared window is minimised) all pointers are hidden but sampling continues
		var have_target = m_target.TryGetRect(out var rc);
		var busy = false;
		foreach (var member in m_members.Values)
		{
			if (member.Id == m_me)
				continue;

			// Hidden or stale pointers are hidden; their trail still fades out
			var remote = m_remotes.GetValueOrDefault(member.Id);
			if (!have_target || !m_smoother.TrySample(member.Id, now, out var state))
			{
				if (remote != null)
				{
					remote.Pointer.Hide();
					busy |= TickTrail(remote, now, false, 0, 0, Palette.Rgb(member.Slot));
				}
				continue;
			}

			// Map the normalised position onto the target area
			busy = true;
			remote ??= m_remotes[member.Id] = new Remote(new Sprite(m_hide_from_capture), new Sprite(m_hide_from_capture));
			var x = rc.Left + (int)Math.Round(state.X * (rc.Width - 1));
			var y = rc.Top + (int)Math.Round(state.Y * (rc.Height - 1));

			// Redraw only when the appearance changes; otherwise just move the window
			var scale = Scale(x, y, state.Size);
			var rgb = Palette.Rgb(member.Slot);
			var look = (member.Name, rgb, scale, state.Laser);
			if (look != remote.Drawn)
			{
				remote.Layout = Art.LayoutPointer(member.Name, scale);
				var canvas = remote.Pointer.Begin(remote.Layout.Width, remote.Layout.Height);
				Art.DrawPointer(canvas, remote.Layout, rgb, member.Name, scale, state.Laser);
				remote.Pointer.Present(x - remote.Layout.HotX, y - remote.Layout.HotY);
				remote.Drawn = look;
			}
			else
			{
				remote.Pointer.Move(x - remote.Layout.HotX, y - remote.Layout.HotY);
			}
			TickTrail(remote, now, state.Laser, x, y, rgb);
		}
		return busy;
	}

	/// <summary>Move the agent pointer along its legs and hide it when its time is up. Returns true while it needs the timer.</summary>
	private bool TickAgent(double now)
	{
		// Nothing to do until an agent has pointed
		if (m_agent is null)
			return false;

		// Advance through the legs. A finished leg hands its leftover time to the next, so chained legs move without a pause
		var moving = false;
		while (m_agent_legs.TryPeek(out var leg))
		{
			if (double.IsNaN(leg.Start))
				leg.Start = now;

			var t = leg.Duration > 0 ? Math.Min(1.0, (now - leg.Start) / leg.Duration) : 1.0;
			var u = (float)(leg.Ease ? t * t * (3 - 2 * t) : t);
			var covered = leg.Prefix(u);
			m_agent_pos = covered[^1];
			leg.Progress?.Invoke(covered);
			if (t < 1)
			{
				moving = true;
				break;
			}

			// The leg is complete; start the hold time after the last leg
			m_agent_legs.Dequeue();
			leg.Arrived?.Invoke();
			if (m_agent_legs.TryPeek(out var next))
				next.Start = leg.Start + leg.Duration;
			else
				m_agent_expires = m_agent_hold > 0 ? now + m_agent_hold : double.PositiveInfinity;
		}

		// Hide the pointer once its hold time has passed
		if (m_agent_shown && !moving && now >= m_agent_expires)
		{
			m_agent_shown = false;
			m_agent.Pointer.Hide();
		}

		// Draw the pointer, redrawing only when its appearance changes
		var x = (int)MathF.Round(m_agent_pos.X);
		var y = (int)MathF.Round(m_agent_pos.Y);
		if (m_agent_shown)
		{
			var scale = Scale(x, y, PointerFrame.DefaultSize);
			var look = (m_agent_label, m_agent_rgb, scale, false);
			if (look != m_agent.Drawn)
			{
				m_agent.Layout = Art.LayoutPointer(m_agent_label, scale);
				var canvas = m_agent.Pointer.Begin(m_agent.Layout.Width, m_agent.Layout.Height);
				Art.DrawPointer(canvas, m_agent.Layout, m_agent_rgb, m_agent_label, scale, laser: false);
				m_agent.Pointer.Present(x - m_agent.Layout.HotX, y - m_agent.Layout.HotY);
				m_agent.Drawn = look;
			}
			else
			{
				m_agent.Pointer.Move(x - m_agent.Layout.HotX, y - m_agent.Layout.HotY);
			}
		}

		// The trail follows the pointer only while it moves, which makes the motion easy to follow
		var trail = TickTrail(m_agent, now, moving, x, y, m_agent_rgb);
		return moving || trail || (m_agent_shown && !double.IsPositiveInfinity(m_agent_expires));
	}

	/// <summary>Update a pointer's trail: add the current point while 'active', drop old points, and redraw. Returns true while any point remains.</summary>
	private static bool TickTrail(Remote remote, double now, bool active, int x, int y, uint rgb)
	{
		// Record the position only when it moved, so a still pointer does not build up points
		var pts = remote.TrailPoints;
		if (active && (pts.Count == 0 || pts[^1].X != x || pts[^1].Y != y))
			pts.Add((now, x, y));

		pts.RemoveAll(p => now - p.Time > TrailLife);
		if (pts.Count < 2)
		{
			remote.Trail.Hide();
			return pts.Count != 0;
		}

		// Draw the trail in a sprite covering just its bounding box
		var scale = Scale(pts[^1].X, pts[^1].Y, PointerFrame.DefaultSize);
		var margin = (int)MathF.Ceiling(8 * scale);
		var l = pts.Min(p => p.X) - margin;
		var t = pts.Min(p => p.Y) - margin;
		var canvas = remote.Trail.Begin(pts.Max(p => p.X) + margin - l, pts.Max(p => p.Y) + margin - t);
		var local = pts.Select(p => ((float)(p.X - l), (float)(p.Y - t), (float)((now - p.Time) / TrailLife))).ToArray();
		Art.DrawTrail(canvas, local, rgb, scale);
		remote.Trail.Present(l, t);
		return true;
	}

	/// <summary>Animate ripples and pulses, removing finished ones.</summary>
	private bool TickEffects(double now)
	{
		// Iterate backwards so finished effects can be removed in place
		for (var i = m_effects.Count - 1; i >= 0; --i)
		{
			var e = m_effects[i];
			var t = (float)((now - e.Start) / (e.Pulse ? PulseTime : RippleTime));
			if (t >= 1)
			{
				e.Sprite.Dispose();
				m_effects.RemoveAt(i);
				continue;
			}
			if (t < 0)
				continue;

			// Effects are centred on their position
			var size = Art.RippleSize(e.Scale);
			var canvas = e.Sprite.Begin(size, size);
			if (e.Pulse)
				Art.DrawPulse(canvas, e.Rgb, e.Scale, t);
			else
				Art.DrawRipple(canvas, e.Rgb, e.Scale, t);

			e.Sprite.Present(e.X - size / 2, e.Y - size / 2);
		}
		return m_effects.Count != 0;
	}

	/// <summary>Remove expired annotations. Returns true while any annotation is waiting to expire.</summary>
	private bool TickAnnotations(double now)
	{
		// Annotations without an expiry don't need the timer
		m_annotations.RemoveAll(a =>
		{
			// Dispose expired sprites as they are removed
			if (now < a.Expires)
				return false;

			a.Sprite.Dispose();
			return true;
		});
		return m_annotations.Any(a => !double.IsPositiveInfinity(a.Expires));
	}

	/// <summary>Remove one remote pointer.</summary>
	private void RemoveRemote(int id)
	{
		// Forget its samples too, so a new member reusing the id starts clean
		if (m_remotes.Remove(id, out var remote))
		{
			remote.Pointer.Dispose();
			remote.Trail.Dispose();
		}
		m_smoother.Remove(id);
	}

	/// <summary>Remove all remote pointers.</summary>
	private void ClearRemotes()
	{
		foreach (var id in m_remotes.Keys.ToList())
			RemoveRemote(id);
	}
}
