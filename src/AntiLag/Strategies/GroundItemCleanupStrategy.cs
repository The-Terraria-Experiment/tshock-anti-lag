using System;
using System.Collections.Generic;
using AntiLag.Configuration;
using AntiLag.Load;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace AntiLag.Strategies
{
	/// <summary>
	/// Destroys item entities that have been lying on the ground longer than a player-count-driven
	/// grace period.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Ground items are the cheapest large win available: Terraria keeps up to 400 of them alive,
	/// each ticking physics and syncing to every client in range, and on a busy server most of them
	/// are junk nobody will ever pick up.
	/// </para>
	/// <para>
	/// Item age comes from the engine's own <c>WorldItem.timeSinceItemSpawned</c> counter rather than
	/// a table maintained here. That matters because Terraria recycles item slots: a hand-rolled
	/// "first seen in slot N" table silently ages the wrong item whenever a slot is reused, which is
	/// exactly when a server is busy enough for this strategy to be running.
	/// </para>
	/// </remarks>
	public sealed class GroundItemCleanupStrategy : IAntiLagStrategy
	{
		private const int TicksPerSecond = 60;

		private AntiLagContext? _context;
		private int _currentMaxAgeSeconds;
		private int _lastSweepCleared;
		private long _totalCleared;

		/// <inheritdoc />
		public string Name => "grounditems";

		/// <inheritdoc />
		public string Description => "Clears aged item entities off the ground.";

		/// <inheritdoc />
		public bool Enabled => Settings?.Enabled == true;

		private GroundItemCleanupSettings? Settings => _context?.Settings.GroundItemCleanup;

		/// <inheritdoc />
		public void Initialize(AntiLagContext context)
		{
			_context = context ?? throw new ArgumentNullException(nameof(context));

			// No hooks of its own; the manager drives this strategy purely through OnSweep.
			GroundItemCleanupSettings settings = context.Settings.GroundItemCleanup;
			WarnAboutCurve(context, settings.MaxItemAgeSeconds, nameof(settings.MaxItemAgeSeconds));

			OnLoadChanged(context.LoadMonitor.Current);
		}

		private void WarnAboutCurve(AntiLagContext context, Curve curve, string label)
		{
			if (curve.Prepare(out IReadOnlyList<string> warnings))
			{
				foreach (string w in warnings)
					context.LogWarn($"{Name}.{label}: {w}");
				return;
			}

			context.LogWarn($"{Name}.{label}: {string.Join("; ", warnings)}");
		}

		/// <inheritdoc />
		public void OnLoadChanged(in LoadSample load)
		{
			GroundItemCleanupSettings? settings = Settings;
			if (settings == null)
				return;

			int baseline = settings.MaxItemAgeSeconds.EvaluateInt(load.PlayerCount, settings.MaxItemAgeSecondsCap);

			// A struggling server should clear sooner, so TPS pressure shortens the grace period.
			_currentMaxAgeSeconds = load.ApplyTpsFactor(
				baseline,
				settings.TpsInfluence,
				settings.MinItemAgeSeconds,
				settings.MaxItemAgeSecondsCap);
		}

		/// <inheritdoc />
		public void OnSweep(long tick)
		{
			GroundItemCleanupSettings? settings = Settings;
			if (settings == null || !settings.Enabled)
				return;

			Sweep(settings, announce: settings.AnnounceSweeps);
		}

		/// <inheritdoc />
		public string ForceRun()
		{
			GroundItemCleanupSettings? settings = Settings;
			if (settings == null)
				return "not initialized";

			int cleared = Sweep(settings, announce: false);
			return $"cleared {cleared} ground item(s)";
		}

		private int Sweep(GroundItemCleanupSettings settings, bool announce)
		{
			int ageTicks = Math.Max(1, _currentMaxAgeSeconds) * TicksPerSecond;

			// One pass: count what is active, and clear what qualifies. Counting first in a separate
			// pass would double the work for no benefit, since MinimumItemsBeforeSweep is only a
			// guard against churning on a nearly empty world.
			int active = 0;
			var doomed = new List<int>();

			for (int i = 0; i < Main.maxItems; i++)
			{
				WorldItem item = Main.item[i];
				if (item == null || !item.active || item.type <= 0 || item.stack <= 0)
					continue;

				active++;

				if (item.timeSinceItemSpawned < ageTicks)
					continue;
				if (IsProtected(item, settings))
					continue;

				doomed.Add(i);
			}

			if (active < settings.MinimumItemsBeforeSweep)
				return 0;

			int cleared = 0;
			foreach (int i in doomed)
			{
				WorldItem item = Main.item[i];
				if (item == null || !item.active)
					continue;

				item.TurnToAir(true);
				TSPlayer.All.SendData(PacketTypes.SyncItemDespawn, "", i);
				cleared++;
			}

			_lastSweepCleared = cleared;
			_totalCleared += cleared;

			if (announce && cleared >= settings.AnnounceThreshold)
			{
				TSPlayer.All.SendInfoMessage(
					cleared == 1
						? "[AntiLag] Cleared 1 dropped item to reduce lag."
						: $"[AntiLag] Cleared {cleared} dropped items to reduce lag.");
			}

			return cleared;
		}

		/// <summary>
		/// Decides whether an item is someone's loot rather than litter. Errs toward keeping items:
		/// eating a player's boss drop is far worse than leaving one extra entity alive.
		/// </summary>
		private static bool IsProtected(WorldItem item, GroundItemCleanupSettings settings)
		{
			if (settings.ProtectCoins && IsCoin(item.type))
				return true;

			if (item.rare >= settings.ProtectedRarity)
				return true;

			if (settings.ProtectedStackValue > 0)
			{
				// value is per-unit copper; a large stack of cheap items is still worth keeping.
				long stackValue = (long)item.value * item.stack;
				if (stackValue >= settings.ProtectedStackValue)
					return true;
			}

			if (settings.ExemptItemIds != null && settings.ExemptItemIds.Contains(item.type))
				return true;

			return false;
		}

		private static bool IsCoin(int type) =>
			type == ItemID.CopperCoin || type == ItemID.SilverCoin ||
			type == ItemID.GoldCoin || type == ItemID.PlatinumCoin;

		/// <inheritdoc />
		public string DescribeState()
		{
			if (Settings?.Enabled != true)
				return "disabled";

			int active = CountActiveItems();
			return $"grace {_currentMaxAgeSeconds}s | {active}/{Main.maxItems} items on ground | " +
			       $"last sweep cleared {_lastSweepCleared} | {_totalCleared} total";
		}

		private static int CountActiveItems()
		{
			int n = 0;
			for (int i = 0; i < Main.maxItems; i++)
			{
				WorldItem item = Main.item[i];
				if (item != null && item.active && item.type > 0)
					n++;
			}
			return n;
		}

		/// <inheritdoc />
		public void Dispose()
		{
			_context = null;
		}
	}
}
