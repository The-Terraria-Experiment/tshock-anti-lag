using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AntiLag.Configuration
{
	/// <summary>Root of <c>AntiLag.json</c>.</summary>
	public sealed class AntiLagSettings
	{
		/// <summary>Master switch. When false every strategy stays dormant regardless of its own setting.</summary>
		[JsonProperty("Enabled")]
		public bool Enabled { get; set; } = true;

		/// <summary>How often, in game ticks, strategies run their periodic sweep. 60 ticks is one second.</summary>
		[JsonProperty("SweepIntervalTicks")]
		public int SweepIntervalTicks { get; set; } = 60;

		/// <summary>Tick-rate measurement and smoothing.</summary>
		[JsonProperty("LoadMonitor")]
		public LoadMonitorSettings LoadMonitor { get; set; } = new LoadMonitorSettings();

		/// <summary>Ground item cleanup.</summary>
		[JsonProperty("GroundItemCleanup")]
		public GroundItemCleanupSettings GroundItemCleanup { get; set; } = new GroundItemCleanupSettings();

		/// <summary>Per-player projectile budgeting.</summary>
		[JsonProperty("ProjectileBudget")]
		public ProjectileBudgetSettings ProjectileBudget { get; set; } = new ProjectileBudgetSettings();

		/// <summary>NPC spawn throttling.</summary>
		[JsonProperty("NpcSpawnThrottle")]
		public NpcSpawnThrottleSettings NpcSpawnThrottle { get; set; } = new NpcSpawnThrottleSettings();
	}

	/// <summary>Tuning for <see cref="Load.LoadMonitor"/>.</summary>
	public sealed class LoadMonitorSettings
	{
		/// <summary>Length of each tick-rate measurement window, in seconds.</summary>
		[JsonProperty("WindowSeconds")]
		public double WindowSeconds { get; set; } = 1.0d;

		/// <summary>
		/// EMA weight applied to each new tick-rate reading, 0..1. Lower is smoother and slower to
		/// react. Raise only if the plugin feels sluggish to respond; lowering fixes oscillation.
		/// </summary>
		[JsonProperty("Smoothing")]
		public double Smoothing { get; set; } = 0.25d;
	}

	/// <summary>Settings shared by every strategy.</summary>
	public abstract class StrategySettings
	{
		/// <summary>Whether this strategy runs.</summary>
		[JsonProperty("Enabled", Order = -10)]
		public bool Enabled { get; set; } = true;

		/// <summary>
		/// How strongly measured TPS is allowed to tighten this strategy's curve output, 0..1.
		/// At 0 the strategy responds to player count only. At 1 a half-speed server halves the budget.
		/// </summary>
		[JsonProperty("TpsInfluence", Order = -9)]
		public double TpsInfluence { get; set; } = 0.5d;
	}

	/// <summary>Settings for the ground item cleanup strategy.</summary>
	public sealed class GroundItemCleanupSettings : StrategySettings
	{
		/// <summary>
		/// Player count to maximum item age, in seconds. More players means a shorter grace period.
		/// An item is only eligible for cleanup once it has been on the ground this long.
		/// </summary>
		[JsonProperty("MaxItemAgeSeconds")]
		public Curve MaxItemAgeSeconds { get; set; } = new Curve(
			InterpolationMode.Linear,
			new CurvePoint(0, 600),
			new CurvePoint(10, 300),
			new CurvePoint(25, 120),
			new CurvePoint(50, 45));

		/// <summary>Never let the curve plus TPS scaling drop the grace period below this many seconds.</summary>
		[JsonProperty("MinItemAgeSeconds")]
		public int MinItemAgeSeconds { get; set; } = 20;

		/// <summary>Upper clamp on the grace period, in seconds.</summary>
		[JsonProperty("MaxItemAgeSecondsCap")]
		public int MaxItemAgeSecondsCap { get; set; } = 1800;

		/// <summary>
		/// Skip the sweep entirely while fewer than this many items are on the ground. Terraria caps
		/// ground items at 400, so this keeps the plugin idle on a quiet server.
		/// </summary>
		[JsonProperty("MinimumItemsBeforeSweep")]
		public int MinimumItemsBeforeSweep { get; set; } = 50;

		/// <summary>Item rarity at or above which items are never cleared. Terraria rarities run -1 (gray) to 11 (red).</summary>
		[JsonProperty("ProtectedRarity")]
		public int ProtectedRarity { get; set; } = 5;

		/// <summary>Total stack value (copper) at or above which items are never cleared. Set to 0 to disable.</summary>
		[JsonProperty("ProtectedStackValue")]
		public int ProtectedStackValue { get; set; } = 500000;

		/// <summary>Item type IDs that are never cleared regardless of age.</summary>
		[JsonProperty("ExemptItemIds", ObjectCreationHandling = ObjectCreationHandling.Replace)]
		public List<int> ExemptItemIds { get; set; } = new List<int>();

		/// <summary>Whether to leave coins alone. Coins are usually someone's income, not litter.</summary>
		[JsonProperty("ProtectCoins")]
		public bool ProtectCoins { get; set; } = true;

		/// <summary>Whether to announce sweeps that cleared at least <see cref="AnnounceThreshold"/> items.</summary>
		[JsonProperty("AnnounceSweeps")]
		public bool AnnounceSweeps { get; set; } = true;

		/// <summary>Minimum number of cleared items before a sweep is announced in chat.</summary>
		[JsonProperty("AnnounceThreshold")]
		public int AnnounceThreshold { get; set; } = 25;
	}

	/// <summary>What to do when a player exceeds their projectile budget.</summary>
	public enum ProjectileEnforcement
	{
		/// <summary>Refuse the new projectile and tell the client to drop it.</summary>
		RejectNew = 0,

		/// <summary>Allow the new projectile, but kill that player's oldest one to make room.</summary>
		KillOldest = 1
	}

	/// <summary>Settings for the per-player projectile budget strategy.</summary>
	public sealed class ProjectileBudgetSettings : StrategySettings
	{
		// Projectile budgeting is the strategy most able to affect gameplay, so it ships off.
		/// <summary>Whether this strategy runs. Defaults to false: enable deliberately after tuning.</summary>
		public ProjectileBudgetSettings() { Enabled = false; }

		/// <summary>
		/// Player count to per-player concurrent projectile budget. Terraria's global ceiling is 1000,
		/// so with 25 players a budget of 30 already allows 750 in the worst case.
		/// </summary>
		[JsonProperty("BudgetPerPlayer")]
		public Curve BudgetPerPlayer { get; set; } = new Curve(
			InterpolationMode.Linear,
			new CurvePoint(0, 120),
			new CurvePoint(10, 60),
			new CurvePoint(25, 32),
			new CurvePoint(50, 18));

		/// <summary>Never let the budget drop below this. Set high enough that normal weapons still work.</summary>
		[JsonProperty("MinBudget")]
		public int MinBudget { get; set; } = 12;

		/// <summary>Upper clamp on the per-player budget.</summary>
		[JsonProperty("MaxBudget")]
		public int MaxBudget { get; set; } = 200;

		/// <summary>What to do when a player is over budget.</summary>
		/// <remarks>Written as a name ("RejectNew") rather than an integer, so the config stays readable.</remarks>
		[JsonProperty("Enforcement")]
		[JsonConverter(typeof(StringEnumConverter))]
		public ProjectileEnforcement Enforcement { get; set; } = ProjectileEnforcement.RejectNew;

		/// <summary>
		/// Exempt pets and light pets. Strongly recommended: TShock's own Bouncer notes that removing
		/// a pet projectile makes the client re-send it forever, producing a packet storm.
		/// </summary>
		[JsonProperty("ExemptPets")]
		public bool ExemptPets { get; set; } = true;

		/// <summary>Exempt minions and sentries. These are a deliberate build choice, not spam.</summary>
		[JsonProperty("ExemptMinionsAndSentries")]
		public bool ExemptMinionsAndSentries { get; set; } = true;

		/// <summary>Projectile type IDs that never count against a budget and are never removed.</summary>
		[JsonProperty("ExemptProjectileIds", ObjectCreationHandling = ObjectCreationHandling.Replace)]
		public List<int> ExemptProjectileIds { get; set; } = new List<int>();

		/// <summary>Whether to tell a player when their projectiles are being throttled.</summary>
		[JsonProperty("NotifyPlayer")]
		public bool NotifyPlayer { get; set; } = true;

		/// <summary>Minimum seconds between throttle notices to the same player.</summary>
		[JsonProperty("NotifyCooldownSeconds")]
		public int NotifyCooldownSeconds { get; set; } = 15;

		/// <summary>Players with this permission are never throttled.</summary>
		[JsonProperty("BypassPermission")]
		public string BypassPermission { get; set; } = "antilag.bypass";
	}

	/// <summary>Settings for the NPC spawn throttling strategy.</summary>
	public sealed class NpcSpawnThrottleSettings : StrategySettings
	{
		/// <summary>Player count to <c>NPC.defaultMaxSpawns</c>. Vanilla default is 5. Lower means fewer mobs per wave.</summary>
		[JsonProperty("MaxSpawns")]
		public Curve MaxSpawns { get; set; } = new Curve(
			InterpolationMode.Linear,
			new CurvePoint(0, 5),
			new CurvePoint(15, 4),
			new CurvePoint(30, 3),
			new CurvePoint(50, 2));

		/// <summary>
		/// Player count to <c>NPC.defaultSpawnRate</c>. Vanilla default is 600. This is a delay between
		/// waves, so a <em>higher</em> value means <em>fewer</em> mobs.
		/// </summary>
		[JsonProperty("SpawnRate")]
		public Curve SpawnRate { get; set; } = new Curve(
			InterpolationMode.Linear,
			new CurvePoint(0, 600),
			new CurvePoint(15, 800),
			new CurvePoint(30, 1100),
			new CurvePoint(50, 1600));

		/// <summary>Lower clamp for max spawns.</summary>
		[JsonProperty("MinMaxSpawns")]
		public int MinMaxSpawns { get; set; } = 1;

		/// <summary>Upper clamp for max spawns.</summary>
		[JsonProperty("MaxMaxSpawns")]
		public int MaxMaxSpawns { get; set; } = 10;

		/// <summary>Lower clamp for spawn rate.</summary>
		[JsonProperty("MinSpawnRate")]
		public int MinSpawnRate { get; set; } = 200;

		/// <summary>Upper clamp for spawn rate.</summary>
		[JsonProperty("MaxSpawnRate")]
		public int MaxSpawnRate { get; set; } = 4000;

		/// <summary>
		/// Mirror the computed values into TShock's own config so <c>/maxspawns</c> and
		/// <c>/spawnrate</c> report the truth instead of stale startup values.
		/// </summary>
		[JsonProperty("MirrorToTShockConfig")]
		public bool MirrorToTShockConfig { get; set; } = true;
	}
}
