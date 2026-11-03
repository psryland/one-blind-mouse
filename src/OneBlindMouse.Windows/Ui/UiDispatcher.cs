using System.Collections.Concurrent;
using OneBlindMouse.Windows.Native;

namespace OneBlindMouse.Windows.Ui;

/// <summary>
/// Runs work on the UI thread. It owns a hidden message-only window that receives posted work items and timer ticks.
/// <see cref="Post"/> and <see cref="InvokeAsync{T}"/> may be called from any thread; everything else is UI thread only.
/// </summary>
internal sealed class UiDispatcher : NativeWindow
{
	private const uint WM_WORK = User32.WM_APP + 1;

	private readonly ConcurrentQueue<Action> m_work = new();
	private readonly Dictionary<nuint, Action> m_timers = [];
	private readonly int m_thread_id;
	private nuint m_next_timer = 1;
	private int m_posted;

	/// <summary>Create the dispatcher on the current (UI) thread.</summary>
	public UiDispatcher()
	{
		m_thread_id = Environment.CurrentManagedThreadId;
		CreateWindow("OBM.Dispatcher", 0, 0, null, default, User32.HWND_MESSAGE);
	}

	/// <summary>True when called on the UI thread.</summary>
	public bool IsUiThread => Environment.CurrentManagedThreadId == m_thread_id;

	/// <summary>Run 'action' on the UI thread at the next opportunity.</summary>
	public void Post(Action action)
	{
		// Only one wake-up message is outstanding at a time; the handler drains everything queued
		m_work.Enqueue(action);
		if (Interlocked.Exchange(ref m_posted, 1) == 0)
			User32.PostMessageW(Handle, WM_WORK, 0, 0);
	}

	/// <summary>Run 'func' on the UI thread and return its result.</summary>
	public Task<T> InvokeAsync<T>(Func<T> func)
	{
		// Complete asynchronously so callers never resume on the UI thread by accident
		var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(() =>
		{
			// Report failures to the caller rather than crashing the UI
			try
			{
				tcs.SetResult(func());
			}
			catch (Exception ex)
			{
				tcs.SetException(ex);
			}
		});
		return tcs.Task;
	}

	/// <summary>Call 'tick' every 'interval_ms' on the UI thread until <see cref="StopTimer"/>. Returns the timer id.</summary>
	public nuint StartTimer(uint interval_ms, Action tick)
	{
		// Each timer has its own id on the dispatcher window
		var id = m_next_timer++;
		m_timers[id] = tick;
		User32.SetTimer(Handle, id, interval_ms, 0);
		return id;
	}

	/// <summary>Stop a timer started by <see cref="StartTimer"/>. Stopping id 0 does nothing.</summary>
	public void StopTimer(ref nuint id)
	{
		// Clear the caller's id so repeated stops are harmless
		if (id == 0)
			return;

		User32.KillTimer(Handle, id);
		m_timers.Remove(id);
		id = 0;
	}

	/// <summary>Run the message loop until <see cref="Quit"/>. 'dialog' returns the window that should get dialog keyboard handling.</summary>
	public static void Run(Func<nint> dialog)
	{
		// Dialog-style windows need IsDialogMessage for Tab, Enter and Esc handling
		while (User32.GetMessageW(out var msg, 0, 0, 0) > 0)
		{
			var hwnd = dialog();
			if (hwnd != 0 && User32.IsDialogMessageW(hwnd, msg))
				continue;

			User32.TranslateMessage(msg);
			User32.DispatchMessageW(msg);
		}
	}

	/// <summary>End the message loop.</summary>
	public static void Quit()
	{
		User32.PostQuitMessage(0);
	}

	/// <inheritdoc/>
	protected override nint WndProc(uint msg, nint wparam, nint lparam)
	{
		// Handle work items and timers; everything else is default
		switch (msg)
		{
			case WM_WORK:
			{
				// Re-arm before draining so work queued during the drain posts a new wake-up
				Interlocked.Exchange(ref m_posted, 0);
				while (m_work.TryDequeue(out var action))
					action();

				return 0;
			}
			case User32.WM_TIMER:
			{
				if (m_timers.TryGetValue((nuint)wparam, out var tick))
					tick();

				return 0;
			}
			default:
			{
				return base.WndProc(msg, wparam, lparam);
			}
		}
	}
}
