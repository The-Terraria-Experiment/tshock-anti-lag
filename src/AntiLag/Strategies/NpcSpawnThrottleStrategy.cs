using System;
using System.Collections.Generic;
using AntiLag.Configuration;
using AntiLag.Load;
using Terraria;
using TShockAPI;

namespace AntiLag.Strategies
{
	/// <summary>
	/// Scales natural NPC spawning down as load rises, by driving Terraria's own spawn tuning knobs.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the gentlest capping strategy available: it changes how fast mobs appear rather than
	/// deleting mobs that already exist, so it can never yank an enemy out from under a player
	/// mid-fight and cannot desync anything.
	/// </para>
	/// <para>
	/// Note the inverted sense of <c>defaultSpawnRate</c>: it is a delay between spawn waves, so a
	/// larger number produces <em>fewer</em> mobs. The default curve therefore rises with player count
	/// while the <c>MaxSpawns</c> curve falls.
	/// </para>
	/// <para>
	/// <strong>Ownership caveat:</strong> while enabled, this strategy owns
	/// <c>NPC.defaultMaxSpawns</c> and <c>NPC.defaultSpawnRate</c>, the same globals TShock's
	/// <c>/maxspawns</c> and <c>/spawnrate</c> write. Manual changes via those commands are
	/// overwritten the next time load shifts. The values captured at startup are restored on dispose.
	/// </para>
	/// </remarks>
	public sealed class NpcSpawnThrottleStrategy : IAntiLagStrategy
	{
		private AntiLagContext? _context;

		private int _originalMaxSpawns;
		private int _originalSpawnRate;
		private bool _capturedOriginals;

		private int _currentMaxSpawns;
		private int _currentSpawnRate;
		private bool _applied;

		/// <inheritdoc />
		public string Name => "npcspawn";

		/// <inheritdoc />
		public string Description => "Throttles natural NPC spawn rate and wave size.";

		/// <inheritdoc />
		public bool Enabled => Settings?.Enabled == true;

		private NpcSpawnThrottleSettings? Settings => _context?.Settings.NpcSpawnThrottle;

		/// <inheritdoc />
		public void Initialize(AntiLagContext context)
		{
			_context = context ?? throw new ArgumentNullException(nameof(context));

			// Capture once, on first initialize only. Re-capturing on reload would record our own
			// throttled values as the "originals" and make restore a no-op.
			if (!_capturedOriginals)
			{
				_originalMaxSpawns = NPC.defaultMaxSpawns;
				_originalSpawnRate = NPC.defaultSpawnRate;
				_capturedOriginals = true;
				context.LogInfo($"{Name}: captured vanilla spawn settings (maxSpawns={_originalMaxSpawns}, spawnRate={_originalSpawnRate})");
			}

			NpcSpawnThrottleSettings settings = context.Settings.NpcSpawnThrottle;
			WarnAboutCurve(context, settings.MaxSpawns, nameof(settings.MaxSpawns));
			WarnAboutCurve(context, settings.SpawnRate, nameof(settings.SpawnRate));

			if (!settings.Enabled)
			{
				Restore();
				return;
			}

			OnLoadChanged(context.LoadMonitor.Current);
		}

		private void WarnAboutCurve(AntiLagContext context, Curve curve, string label)
		{
			curve.Prepare(out IReadOnlyList<string> warnings);
			foreach (string w in warnings)
				context.LogWarn($"{Name}.{label}: {w}");
		}

		/// <inheritdoc />
		public void OnLoadChanged(in LoadSample load)
		{
			NpcSpawnThrottleSettings? settings = Settings;
			if (settings == null || !settings.Enabled)
				return;

			int maxSpawns = load.ApplyTpsFactor(
				settings.MaxSpawns.EvaluateInt(load.PlayerCount, _originalMaxSpawns),
				settings.TpsInfluence,
				settings.MinMaxSpawns,
				settings.MaxMaxSpawns);

			// Spawn rate is inverted: low TPS should mean a LONGER delay, so the health factor is
			// applied as a divisor rather than a multiplier.
			int spawnBaseline = settings.SpawnRate.EvaluateInt(load.PlayerCount, _originalSpawnRate);
			int spawnRate = ApplyInverseTpsFactor(
				load,
				spawnBaseline,
				settings.TpsInfluence,
				settings.MinSpawnRate,
				settings.MaxSpawnRate);

			if (_applied && maxSpawns == _currentMaxSpawns && spawnRate == _currentSpawnRate)
				return;

			_currentMaxSpawns = maxSpawns;
			_currentSpawnRate = spawnRate;
			_applied = true;

			NPC.defaultMaxSpawns = maxSpawns;
			NPC.defaultSpawnRate = spawnRate;

			if (settings.MirrorToTShockConfig)
			{
				TShock.Config.Settings.DefaultMaximumSpawns = maxSpawns;
				TShock.Config.Settings.DefaultSpawnRate = spawnRate;
			}
		}

		/// <summary>
		/// Scales a "higher means less work" value so that poor health raises it, mirroring
		/// <see cref="LoadSample.ApplyTpsFactor"/> for inverted knobs like spawn delay.
		/// </summary>
		private static int ApplyInverseTpsFactor(in LoadSample load, int baseline, double strength, int min, int max)
		{
			if (min > max)
			{
				int swap = min;
				min = max;
				max = swap;
			}

			if (strength <= 0d)
				return Math.Clamp(baseline, min, max);

			if (strength > 1d)
				strength = 1d;

			// Health of 1.0 leaves the baseline alone; health of 0.5 at full strength doubles the delay.
			double health = Math.Max(load.Health, 0.1d);
			double factor = (1d - strength) + strength / health;

			double scaled = baseline * factor;
			if (double.IsNaN(scaled) || double.IsInfinity(scaled))
				return Math.Clamp(baseline, min, max);

			return Math.Clamp((int)Math.Round(scaled, MidpointRounding.AwayFromZero), min, max);
		}

		/// <inheritdoc />
		public void OnSweep(long tick)
		{
			// Nothing periodic to do: everything happens when load changes.
		}

		/// <inheritdoc />
		public string ForceRun()
		{
			if (_context == null)
				return "not initialized";

			_applied = false; // force a re-apply even if the computed values match
			OnLoadChanged(_context.LoadMonitor.Current);
			return $"applied maxSpawns={_currentMaxSpawns}, spawnRate={_currentSpawnRate}";
		}

		/// <summary>Puts the spawn globals back to the values captured before the plugin touched them.</summary>
		public void Restore()
		{
			if (!_capturedOriginals || !_applied)
				return;

			NPC.defaultMaxSpawns = _originalMaxSpawns;
			NPC.defaultSpawnRate = _originalSpawnRate;

			if (Settings?.MirrorToTShockConfig == true)
			{
				TShock.Config.Settings.DefaultMaximumSpawns = _originalMaxSpawns;
				TShock.Config.Settings.DefaultSpawnRate = _originalSpawnRate;
			}

			_applied = false;
			_context?.LogInfo($"{Name}: restored spawn settings (maxSpawns={_originalMaxSpawns}, spawnRate={_originalSpawnRate})");
		}

		/// <inheritdoc />
		public string DescribeState()
		{
			if (Settings?.Enabled != true)
				return "disabled";
			if (!_applied)
				return "enabled, not yet applied";

			return $"maxSpawns {_currentMaxSpawns} (vanilla {_originalMaxSpawns}) | " +
			       $"spawnRate {_currentSpawnRate} (vanilla {_originalSpawnRate}, higher = fewer mobs)";
		}

		/// <inheritdoc />
		public void Dispose()
		{
			Restore();
			_context = null;
		}
	}
}
