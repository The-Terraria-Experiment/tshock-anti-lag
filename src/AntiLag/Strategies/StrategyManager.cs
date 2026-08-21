using System;
using System.Collections.Generic;
using System.Linq;
using AntiLag.Load;

namespace AntiLag.Strategies
{
	/// <summary>
	/// Owns the strategy list and drives every strategy from a single game-update hook.
	/// </summary>
	/// <remarks>
	/// One hook for all strategies is deliberate. <c>GameUpdate</c> fires 60 times a second, so each
	/// additional registration is 60 more delegate invocations per second forever; more importantly,
	/// centralising the tick lets the manager enforce that nobody scans a 400- or 1000-entry array on
	/// every tick.
	/// </remarks>
	public sealed class StrategyManager : IDisposable
	{
		private readonly List<IAntiLagStrategy> _strategies = new List<IAntiLagStrategy>();
		private AntiLagContext? _context;
		private long _tick;
		private LoadSample _lastDispatched;
		private bool _hasDispatched;

		/// <summary>The registered strategies, in registration order.</summary>
		public IReadOnlyList<IAntiLagStrategy> Strategies => _strategies;

		/// <summary>Registers the strategies that ship with the plugin.</summary>
		/// <remarks>This is the single place a new strategy needs to be added.</remarks>
		public void RegisterDefaults()
		{
			Register(new GroundItemCleanupStrategy());
			Register(new ProjectileBudgetStrategy());
			Register(new NpcSpawnThrottleStrategy());
		}

		/// <summary>Adds a strategy. Call before <see cref="Initialize"/>.</summary>
		public void Register(IAntiLagStrategy strategy)
		{
			if (strategy == null)
				throw new ArgumentNullException(nameof(strategy));
			if (_strategies.Any(s => string.Equals(s.Name, strategy.Name, StringComparison.OrdinalIgnoreCase)))
				throw new ArgumentException($"A strategy named '{strategy.Name}' is already registered.", nameof(strategy));

			_strategies.Add(strategy);
		}

		/// <summary>Finds a strategy by its <see cref="IAntiLagStrategy.Name"/>, case-insensitively.</summary>
		public IAntiLagStrategy? Find(string name) =>
			_strategies.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// Initializes (or re-initializes, after a config reload) every strategy. A strategy that
		/// throws is logged and skipped rather than taking the whole plugin down with it.
		/// </summary>
		public void Initialize(AntiLagContext context)
		{
			_context = context ?? throw new ArgumentNullException(nameof(context));
			_hasDispatched = false;

			foreach (IAntiLagStrategy s in _strategies)
			{
				try
				{
					s.Initialize(context);
				}
				catch (Exception ex)
				{
					context.LogWarn($"strategy '{s.Name}' failed to initialize and will be skipped: {ex.Message}");
				}
			}
		}

		/// <summary>
		/// Called once per game tick. Keeps per-tick work to a counter increment plus a stopwatch
		/// read; everything else is rate limited.
		/// </summary>
		public void OnGameUpdate()
		{
			AntiLagContext? context = _context;
			if (context == null || !context.Settings.Enabled)
				return;

			_tick++;

			if (context.LoadMonitor.RecordTick())
				DispatchLoadChange(context, context.LoadMonitor.Current);

			int interval = context.Settings.SweepIntervalTicks;
			if (interval < 1)
				interval = 1;

			if (_tick % interval == 0)
				DispatchSweep(context);
		}

		/// <summary>
		/// Pushes a load change to strategies, but only when something actually moved. Without this
		/// guard the NPC strategy would rewrite Terraria's spawn globals every sampling window.
		/// </summary>
		private void DispatchLoadChange(AntiLagContext context, in LoadSample sample)
		{
			if (_hasDispatched && !IsMeaningfullyDifferent(_lastDispatched, sample))
				return;

			_lastDispatched = sample;
			_hasDispatched = true;

			foreach (IAntiLagStrategy s in _strategies)
			{
				if (!s.Enabled)
					continue;

				try
				{
					s.OnLoadChanged(sample);
				}
				catch (Exception ex)
				{
					context.LogWarn($"strategy '{s.Name}' threw during OnLoadChanged: {ex.Message}");
				}
			}
		}

		/// <summary>
		/// Whether two samples differ enough to be worth acting on. A one-player change always counts;
		/// tick rate needs to move by a whole TPS, which filters out measurement jitter.
		/// </summary>
		private static bool IsMeaningfullyDifferent(in LoadSample a, in LoadSample b) =>
			a.PlayerCount != b.PlayerCount || Math.Abs(a.Tps - b.Tps) >= 1.0d;

		private void DispatchSweep(AntiLagContext context)
		{
			foreach (IAntiLagStrategy s in _strategies)
			{
				if (!s.Enabled)
					continue;

				try
				{
					s.OnSweep(_tick);
				}
				catch (Exception ex)
				{
					context.LogWarn($"strategy '{s.Name}' threw during OnSweep: {ex.Message}");
				}
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			foreach (IAntiLagStrategy s in _strategies)
			{
				try
				{
					s.Dispose();
				}
				catch (Exception ex)
				{
					_context?.LogWarn($"strategy '{s.Name}' threw during dispose: {ex.Message}");
				}
			}

			_strategies.Clear();
			_context = null;
		}
	}
}
