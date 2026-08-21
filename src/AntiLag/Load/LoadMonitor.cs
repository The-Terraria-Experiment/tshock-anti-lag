using System;
using System.Diagnostics;

namespace AntiLag.Load
{
	/// <summary>
	/// Derives the current <see cref="LoadSample"/> by counting game ticks against wall-clock time.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Terraria has no "current TPS" property, so we measure it: <see cref="RecordTick"/> is called
	/// once per <c>GameUpdate</c>, and once per sampling window we divide the tick count by elapsed
	/// real time.
	/// </para>
	/// <para>
	/// The result is smoothed with an exponential moving average. Without smoothing a single long
	/// tick (a world save, a chunk of GC) would look like a TPS collapse and make every strategy
	/// slam its budgets shut for a second, producing visible oscillation.
	/// </para>
	/// <para>
	/// The player-count source is injected so this class stays testable without a live server.
	/// </para>
	/// </remarks>
	public sealed class LoadMonitor
	{
		private readonly Func<int> _playerCountSource;
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private readonly double _smoothing;
		private readonly TimeSpan _window;

		private long _ticksThisWindow;
		private TimeSpan _windowStart;
		private double _smoothedTps = LoadSample.TargetTps;
		private bool _hasFirstSample;

		/// <summary>Creates a monitor.</summary>
		/// <param name="playerCountSource">Returns the current online player count.</param>
		/// <param name="smoothing">
		/// EMA weight for each new reading, 0..1. Lower reacts more slowly and smoothly.
		/// </param>
		/// <param name="windowSeconds">How long each measurement window is.</param>
		public LoadMonitor(Func<int> playerCountSource, double smoothing = 0.25d, double windowSeconds = 1.0d)
		{
			_playerCountSource = playerCountSource ?? throw new ArgumentNullException(nameof(playerCountSource));
			_smoothing = smoothing <= 0d ? 0.01d : (smoothing > 1d ? 1d : smoothing);
			_window = TimeSpan.FromSeconds(windowSeconds <= 0d ? 1.0d : windowSeconds);
			_windowStart = _clock.Elapsed;
		}

		/// <summary>The most recent sample. Updated once per measurement window.</summary>
		public LoadSample Current { get; private set; } = new LoadSample(0, LoadSample.TargetTps);

		/// <summary>Raw (unsmoothed) tick rate from the last completed window, for diagnostics.</summary>
		public double LastRawTps { get; private set; } = LoadSample.TargetTps;

		/// <summary>
		/// Records one game tick. Call from <c>GameUpdate</c>. Cheap: an increment plus one
		/// stopwatch read; the division only happens once per window.
		/// </summary>
		/// <returns>True when this tick closed a measurement window and <see cref="Current"/> changed.</returns>
		public bool RecordTick()
		{
			_ticksThisWindow++;

			TimeSpan now = _clock.Elapsed;
			TimeSpan elapsed = now - _windowStart;
			if (elapsed < _window)
				return false;

			double seconds = elapsed.TotalSeconds;
			double rawTps = seconds > 0d ? _ticksThisWindow / seconds : LoadSample.TargetTps;

			// Terraria never intentionally runs faster than 60; treat overshoot as healthy, not as a spike.
			if (rawTps > LoadSample.TargetTps)
				rawTps = LoadSample.TargetTps;

			LastRawTps = rawTps;

			// Seed the EMA with the first real reading instead of easing away from the 60 TPS default.
			_smoothedTps = _hasFirstSample
				? _smoothedTps + _smoothing * (rawTps - _smoothedTps)
				: rawTps;
			_hasFirstSample = true;

			_ticksThisWindow = 0;
			_windowStart = now;

			Current = new LoadSample(_playerCountSource(), _smoothedTps);
			return true;
		}

		/// <summary>
		/// Forgets the measured tick rate and starts over. Used after events that stall the server
		/// for reasons unrelated to load (world save, config reload).
		/// </summary>
		public void Reset()
		{
			_ticksThisWindow = 0;
			_windowStart = _clock.Elapsed;
			_smoothedTps = LoadSample.TargetTps;
			_hasFirstSample = false;
			LastRawTps = LoadSample.TargetTps;
		}
	}
}
