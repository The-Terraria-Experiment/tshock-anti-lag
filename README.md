# AntiLag

A TShock 6 plugin that reduces Terraria server lag through several independent, load-aware
strategies. Each strategy can be enabled, disabled and tuned separately, and adding new ones is a
one-class change.

Built against **TShock 6.1.0** / **TSAPI 6.1.0** / **OTAPI 1.4.5.6** (Terraria 1.4.5.6) on **.NET 9**.

DISCLAIMER: This plugin is almost entirely created by AI. Although it has been tested and used, most of the source code has not been thoroughly human-reviewed. Use with caution.

## Strategies

| Name | What it does | Default |
|---|---|---|
| `grounditems` | Destroys item entities that have been on the ground past a grace period. | **on** |
| `npcspawn` | Scales natural NPC spawn rate and wave size down as load rises. | **on** |
| `projectiles` | Caps how many projectiles each player may have alive at once. | **off** |

`projectiles` ships disabled because it is the only strategy that can change how the game feels to
play. Turn it on deliberately, after reading the caveats below.

## How the curves work

Every tunable is driven by a **curve**: a list of keyframes mapping online player count to a value,
interpolated in between. Author as many or as few points as you like; the curve clamps to the first
and last point rather than extrapolating past them.

```json
"MaxItemAgeSeconds": {
  "Interpolation": "Linear",
  "Points": [
    { "Players": 0,  "Value": 600 },
    { "Players": 10, "Value": 300 },
    { "Players": 25, "Value": 120 },
    { "Players": 50, "Value": 45  }
  ]
}
```

Read that as: with nobody online items survive ten minutes; by 50 players they survive 45 seconds;
at 17 players you get roughly 210 seconds.

`Interpolation` accepts:

- **`Linear`** — straight line between keyframes. The sane default.
- **`Step`** — hold each keyframe's value until the next one is reached. Use when you want hard
  tiers rather than a gradient.
- **`SmoothStep`** — eased transition. Cosmetic; useful if linear feels too abrupt around a
  keyframe you cross often.

Points do not need to be sorted, and duplicates or non-finite values are dropped with a warning in
the server log rather than breaking the config.

### TPS feedback

Player count alone misses the case that actually hurts: four players fighting a boss can lag far
worse than thirty players standing around. So each strategy also has a `TpsInfluence` (0..1):

- `0` — ignore tick rate, follow the curve exactly.
- `0.5` (default) — a server at half speed tightens the curve's value by 25%.
- `1` — a server at half speed tightens it by 50%.

Measured TPS is smoothed with an exponential moving average, because a single long tick (a world
save, a GC pause) would otherwise look like a collapse and make every budget slam shut for a
second. If you see values oscillating, lower `LoadMonitor.Smoothing`.

## Configuration

Written to `tshock/AntiLag.json` on first run. `/antilag reload` or TShock's `/reload` re-reads it.

### Top level

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. When false, no strategy does anything. |
| `SweepIntervalTicks` | `60` | Ticks between periodic sweeps. 60 ticks is one second. |
| `LoadMonitor.WindowSeconds` | `1.0` | Length of each tick-rate measurement window. |
| `LoadMonitor.Smoothing` | `0.25` | EMA weight per reading, 0..1. Lower is smoother and slower. |

### `GroundItemCleanup`

| Key | Default | Meaning |
|---|---|---|
| `MaxItemAgeSeconds` | curve, 600 → 45 | Grace period before an item may be cleared. |
| `MinItemAgeSeconds` | `20` | Floor on the grace period after TPS scaling. |
| `MaxItemAgeSecondsCap` | `1800` | Ceiling on the grace period. |
| `MinimumItemsBeforeSweep` | `50` | Skip the sweep below this many ground items. |
| `ProtectedRarity` | `5` | Never clear items at or above this rarity (-1 gray … 11 red). Quest, Expert and Master rarity items (treasure bags included) are always kept. |
| `ProtectedStackValue` | `500000` | Never clear stacks worth at least this much copper. `0` disables. |
| `ExemptItemIds` | `[]` | Item type IDs never cleared. |
| `ProtectCoins` | `true` | Leave coins alone. |
| `AnnounceSweeps` | `true` | Announce sweeps in chat. |
| `AnnounceThreshold` | `25` | Minimum items cleared before announcing. |

Item age comes from the engine's own `timeSinceItemSpawned` counter, so slot reuse cannot cause the
plugin to clear a freshly-dropped item that happened to land in a recycled slot.

### `NpcSpawnThrottle`

| Key | Default | Meaning |
|---|---|---|
| `MaxSpawns` | curve, 5 → 2 | Drives `NPC.defaultMaxSpawns`: mobs per spawn wave. |
| `SpawnRate` | curve, 600 → 1600 | Drives `NPC.defaultSpawnRate`: **delay** between waves. |
| `MinMaxSpawns` / `MaxMaxSpawns` | `1` / `10` | Clamps for max spawns. |
| `MinSpawnRate` / `MaxSpawnRate` | `200` / `4000` | Clamps for spawn rate. |
| `MirrorToTShockConfig` | `true` | Keep TShock's own config in sync so `/maxspawns` reports the truth. |

> **`SpawnRate` is inverted.** It is a delay between waves, so a **higher** number means **fewer**
> mobs. This is why the shipped `SpawnRate` curve rises with player count while `MaxSpawns` falls.
> Getting this backwards makes a busy server spawn *more* mobs.

> **This strategy takes ownership of the spawn globals.** While enabled it drives the same
> `NPC.defaultMaxSpawns` / `NPC.defaultSpawnRate` values that TShock's `/maxspawns` and `/spawnrate`
> write, and it will overwrite manual changes the next time load shifts. Use
> `/antilag disable npcspawn` to hand them back. The values present at plugin load are captured and
> restored on unload.

### `ProjectileBudget`

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Opt in deliberately. |
| `BudgetPerPlayer` | curve, 120 → 18 | Concurrent projectiles allowed per player. |
| `MinBudget` / `MaxBudget` | `12` / `200` | Clamps after TPS scaling. |
| `Enforcement` | `"RejectNew"` | `RejectNew` or `KillOldest`. |
| `ExemptPets` | `true` | **Leave this on.** See below. |
| `ExemptMinionsAndSentries` | `true` | Summoner builds are a choice, not spam. |
| `ExemptProjectileIds` | `[]` | Projectile type IDs that never count and are never removed. |
| `NotifyPlayer` | `true` | Tell a throttled player why. |
| `NotifyCooldownSeconds` | `15` | Minimum seconds between notices to the same player. |
| `BypassPermission` | `antilag.bypass` | Players with this permission are never throttled. |

> **Do not set `ExemptPets` to false.** Terraria clients re-send pet and light-pet projectiles
> immediately when the server removes them, forever. TShock's own anti-cheat layer documents this
> and silently rejects pets rather than removing them. Disabling this exemption invites a packet
> storm between server and client. The plugin logs a warning at startup if you do it anyway.

`RejectNew` refuses the projectile outright. `KillOldest` frees the player's longest-lived
projectile and lets the new one through, which feels better for sustained-fire weapons but means
a player's earlier shots vanish mid-flight. Start with `RejectNew`.

## Commands

All require the `antilag.admin` permission.

| Command | Effect |
|---|---|
| `/antilag status` | Current load and per-strategy state. |
| `/antilag list` | All strategies and whether they are on. |
| `/antilag reload` | Re-read `AntiLag.json` from disk. |
| `/antilag enable <name>` | Turn a strategy on for this session. |
| `/antilag disable <name>` | Turn a strategy off for this session. |
| `/antilag sweep [name]` | Run strategies immediately, ignoring their schedule. |

`enable` and `disable` apply until the next reload; edit `AntiLag.json` to make them permanent.
`/antilag` is also aliased to `/al`.

## Building

```sh
dotnet build -c Release
dotnet test
```

Copy `src/AntiLag/bin/Release/net9.0/AntiLag.dll` into your server's `ServerPlugins/` folder.

Only `AntiLag.dll` is produced: the project excludes the TShock package's runtime and content assets
so the server's own copies of TShock, TSAPI, OTAPI and `HttpServer.dll` are not shadowed by
duplicates in the plugin folder.

## Adding a strategy

1. Implement `IAntiLagStrategy` (see `src/AntiLag/Strategies/`).
2. Add a settings class to `AntiLagSettings` deriving from `StrategySettings`.
3. Register it in `StrategyManager.RegisterDefaults()`.
4. Add its name to `AntiLagCommands.SectionFor()`.

Step 4 is a `switch` on purpose: a new strategy that forgets its settings section fails to compile
there rather than silently ignoring `/antilag enable`.

Keep anything worth unit testing free of Terraria types — that is what lets `Curve`, `LoadSample`
and `LoadMonitor` be tested without a running server.

## Design notes

**Why not just raise the entity limits?** You cannot. `Main.maxItems` (400) and
`Main.maxProjectiles` (1000) are compile-time constants and `Main.maxNPCs` is a static readonly;
none can be changed at runtime. "Dynamic entity capping" therefore means enforcing budgets
*underneath* those fixed ceilings, which is what this plugin does.

**Why one game-update hook?** `GameUpdate` fires 60 times a second forever. The manager owns the
single registration and rate-limits everything downstream, so no strategy scans a 400- or
1000-entry array on every tick.

**Why per-player projectile budgets instead of a global cull?** A global high-water cull deletes
whatever is nearest when the ceiling is hit, which punishes bystanders. Per-player budgets cost the
player actually generating the load.
