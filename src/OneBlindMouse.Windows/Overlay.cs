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

	/// <summary>A remote member's pointer and laser trail.</summary>
	private sealed class Remote(Sprite pointer, Sprite trail)
	{
		public readonly Sprite Pointer = pointer;
		public readonly Sprite Trail = trail;
		public readonly List<(double Time, int X, int Y)> TrailPoints = [];
		public Art.PointerLayout Layout;
		public (string Name, int Slot, float Scale, bool Laser) Drawn;
	}

	/// <summary>A short animation (ripple or pulse) at a screen position.</summary>
	private sealed record Effect(Sprite Sprite, bool Pulse, int X, int Y, uint Rgb, float Scale, double Start);

	/// <summary>A static agent annotation that expires at 'Expires' (or never if infinite).</summary>
	private sealed record Annotation(Sprite Sprite, double Expires);

	private readonly UiDispatcher m_ui;
	private readonly Target m_target;
	private readonly PointerSmoother m_smoother = new();
	private readonly Stopwatch m_clock = Stopwatch.StartNew();
	private readonly Dictionary<int, Remote> m_remotes = [];
	private readonly List<Effect> m_effects = [];
	private readonly List<Annotation> m_annotations = [];
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

	/// <summary>Show an agent pointer with a label at a screen position. 'duration' is in seconds; 0 means until cleared.</summary>
	public void AgentPointAt(int x, int y, string label, uint rgb, double duration)
	{
		// Agent pointers are drawn like member pointers, at the system's default size
		var sprite = new Sprite(m_hide_from_capture);
		var scale = Scale(x, y, PointerFrame.DefaultSize);
		var layout = Art.LayoutPointer(label, scale);
		var canvas = sprite.Begin(layout.Width, layout.Height);
		Art.DrawPointer(canvas, layout, rgb, label, scale, laser: false);
		sprite.Present(x - layout.HotX, y - layout.HotY);
		AddAnnotation(sprite, duration);
		AddEffect(pulse: true, x, y, rgb, scale);
	}

	/// <summary>Play a click ripple at a screen position.</summary>
	public void AgentClick(int x, int y, uint rgb)
	{
		AddEffect(pulse: false, x, y, rgb, Scale(x, y, PointerFrame.DefaultSize));
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

	/// <summary>Remove all agent annotations.</summary>
	public void ClearAgent()
	{
		foreach (var a in m_annotations)
			a.Sprite.Dispose();

		m_annotations.Clear();
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

	/// <summary>Start a ripple or pulse animation.</summary>
	private void AddEffect(bool pulse, int x, int y, uint rgb, float scale)
	{
		m_effects.Add(new Effect(new Sprite(m_hide_from_capture), pulse, x, y, rgb, scale, Now));
		EnsureTimer();
	}

	/// <summary>Track an annotation so it can expire or be cleared.</summary>
	private void AddAnnotation(Sprite sprite, double duration)
	{
		m_annotations.Add(new Annotation(sprite, duration > 0 ? Now + duration : double.PositiveInfinity));
		EnsureTimer();
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
					busy |= TickTrail(remote, now, false, 0, 0, member.Slot);
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
			var look = (member.Name, member.Slot, scale, state.Laser);
			if (look != remote.Drawn)
			{
				remote.Layout = Art.LayoutPointer(member.Name, scale);
				var canvas = remote.Pointer.Begin(remote.Layout.Width, remote.Layout.Height);
				Art.DrawPointer(canvas, remote.Layout, Palette.Rgb(member.Slot), member.Name, scale, state.Laser);
				remote.Pointer.Present(x - remote.Layout.HotX, y - remote.Layout.HotY);
				remote.Drawn = look;
			}
			else
			{
				remote.Pointer.Move(x - remote.Layout.HotX, y - remote.Layout.HotY);
			}
			TickTrail(remote, now, state.Laser, x, y, member.Slot);
		}
		return busy;
	}

	/// <summary>Update a laser trail: add the current point while the laser is on, drop old points, and redraw. Returns true while any point remains.</summary>
	private static bool TickTrail(Remote remote, double now, bool laser, int x, int y, int slot)
	{
		// Record the position only when it moved, so a still laser does not build up points
		var pts = remote.TrailPoints;
		if (laser && (pts.Count == 0 || pts[^1].X != x || pts[^1].Y != y))
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
		Art.DrawTrail(canvas, local, Palette.Rgb(slot), scale);
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
