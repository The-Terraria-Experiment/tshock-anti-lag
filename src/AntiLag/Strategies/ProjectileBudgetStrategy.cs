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
	/// Caps how many projectiles each player may have alive at once, with the cap shrinking as the
	/// server gets busier.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Terraria's global projectile array is a fixed 1000 entries, so the engine will not lag from
	/// overflow. It lags because a handful of players can each keep a hundred projectiles ticking.
	/// Budgeting per player targets that directly and, unlike a global cull, costs the offending
	/// player rather than whoever happens to stand nearby.
	/// </para>
	/// <para>
	/// <strong>Two traps this implementation is built around:</strong>
	/// </para>
	/// <list type="number">
	/// <item><description>
	/// <em>Pets fight back.</em> Removing a pet or light-pet projectile makes the client immediately
	/// re-send it, forever. TShock's own Bouncer documents this and silently rejects instead. Pets are
	/// exempt by default here, and the exemption is checked before any removal path.
	/// </description></item>
	/// <item><description>
	/// <em>Slot identities are recycled.</em> A <c>NewProjectile</c> packet whose
	/// <c>(owner, identity)</c> pair is already alive is an <em>update</em> to that projectile, not a
	/// new one. Counting those as creations massively over-counts held and channelled weapons and
	/// throttles players who are doing nothing wrong. <see cref="IsUpdateToExisting"/> filters them.
	/// </description></item>
	/// </list>
	/// </remarks>
	public sealed class ProjectileBudgetStrategy : IAntiLagStrategy
	{
		private AntiLagContext? _context;
		private bool _hooked;

		private readonly int[] _countByOwner = new int[Main.maxPlayers + 1];
		private readonly long[] _seqByIndex = new long[Main.maxProjectiles + 1];
		private readonly DateTime[] _lastNotify = new DateTime[Main.maxPlayers + 1];

		private long _sequence;
		private int _currentBudget;
		private long _rejected;
		private long _culled;

		/// <inheritdoc />
		public string Name => "projectiles";

		/// <inheritdoc />
		public string Description => "Caps concurrent projectiles per player.";

		/// <inheritdoc />
		public bool Enabled => Settings?.Enabled == true;

		private ProjectileBudgetSettings? Settings => _context?.Settings.ProjectileBudget;

		/// <inheritdoc />
		public void Initialize(AntiLagContext context)
		{
			_context = context ?? throw new ArgumentNullException(nameof(context));

			ProjectileBudgetSettings settings = context.Settings.ProjectileBudget;
			settings.BudgetPerPlayer.Prepare(out IReadOnlyList<string> warnings);
			foreach (string w in warnings)
				context.LogWarn($"{Name}.BudgetPerPlayer: {w}");

			if (settings.Enabled && !settings.ExemptPets)
			{
				context.LogWarn(
					$"{Name}: ExemptPets is false. Removing pet projectiles makes clients re-send them " +
					"in a loop; this is very likely to cause a packet storm. Set ExemptPets to true unless " +
					"you are certain.");
			}

			if (settings.Enabled && !_hooked)
			{
				GetDataHandlers.NewProjectile += OnNewProjectile;
				GetDataHandlers.ProjectileKill += OnProjectileKill;
				_hooked = true;
			}
			else if (!settings.Enabled && _hooked)
			{
				Unhook();
			}

			RecountFromWorld();
			OnLoadChanged(context.LoadMonitor.Current);
		}

		private void Unhook()
		{
			if (!_hooked)
				return;

			GetDataHandlers.NewProjectile -= OnNewProjectile;
			GetDataHandlers.ProjectileKill -= OnProjectileKill;
			_hooked = false;
		}

		/// <inheritdoc />
		public void OnLoadChanged(in LoadSample load)
		{
			ProjectileBudgetSettings? settings = Settings;
			if (settings == null)
				return;

			int baseline = settings.BudgetPerPlayer.EvaluateInt(load.PlayerCount, settings.MaxBudget);
			_currentBudget = load.ApplyTpsFactor(baseline, settings.TpsInfluence, settings.MinBudget, settings.MaxBudget);
		}

		/// <inheritdoc />
		public void OnSweep(long tick)
		{
			if (Settings?.Enabled != true)
				return;

			// Packet-driven counters drift: projectiles expire on their own, players disconnect, and
			// kill packets go missing. The periodic recount is the source of truth.
			RecountFromWorld();
		}

		/// <inheritdoc />
		public string ForceRun()
		{
			if (Settings?.Enabled != true)
				return "disabled";

			RecountFromWorld();
			return $"recounted; budget {_currentBudget}/player, {TotalActive()} projectiles active";
		}

		/// <summary>
		/// Rebuilds per-owner counts from the live projectile array and re-stamps sequence numbers for
		/// any projectile we have not seen before.
		/// </summary>
		private void RecountFromWorld()
		{
			Array.Clear(_countByOwner, 0, _countByOwner.Length);

			ProjectileBudgetSettings? settings = Settings;

			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				Projectile p = Main.projectile[i];
				if (p == null || !p.active)
				{
					_seqByIndex[i] = 0;
					continue;
				}

				if (settings != null && IsExemptProjectile(p, settings))
				{
					_seqByIndex[i] = 0;
					continue;
				}

				int owner = p.owner;
				if (owner >= 0 && owner < _countByOwner.Length)
					_countByOwner[owner]++;

				// Projectiles that predate our tracking get a stamp now so KillOldest has an ordering.
				if (_seqByIndex[i] == 0)
					_seqByIndex[i] = ++_sequence;
			}
		}

		private void OnNewProjectile(object? sender, GetDataHandlers.NewProjectileEventArgs args)
		{
			ProjectileBudgetSettings? settings = Settings;
			if (settings == null || !settings.Enabled || args.Handled)
				return;

			int owner = args.Owner;
			if (owner < 0 || owner >= _countByOwner.Length)
				return;

			int type = args.Type;

			// Exempt types neither count against the budget nor get removed.
			if (IsExemptType(type, settings))
				return;

			// An update to a projectile that already exists is not a new entity; ignore it entirely.
			if (IsUpdateToExisting(args.Index, args.Identity, owner))
				return;

			if (args.Player != null && !string.IsNullOrEmpty(settings.BypassPermission) &&
			    args.Player.HasPermission(settings.BypassPermission))
			{
				_countByOwner[owner]++;
				StampSequence(args.Index);
				return;
			}

			if (_countByOwner[owner] < _currentBudget)
			{
				_countByOwner[owner]++;
				StampSequence(args.Index);
				return;
			}

			if (settings.Enforcement == ProjectileEnforcement.KillOldest && TryKillOldest(owner, settings))
			{
				// Room was made; let the new projectile through and count it.
				_countByOwner[owner]++;
				StampSequence(args.Index);
				Notify(args.Player, settings);
				return;
			}

			// RejectNew, or KillOldest found nothing removable.
			args.Player?.RemoveProjectile(args.Identity, owner);
			args.Handled = true;
			_rejected++;
			Notify(args.Player, settings);
		}

		/// <summary>
		/// Records creation order for a projectile slot, so <see cref="TryKillOldest"/> has an ordering.
		/// </summary>
		/// <remarks>
		/// TShock reports index 1000 (<c>Main.maxProjectiles</c>) to mean "no slot holds this
		/// identity yet", so there is nothing to stamp for a genuinely new projectile. The periodic
		/// recount picks it up and stamps it once Terraria has assigned it a real slot.
		/// </remarks>
		private void StampSequence(int index)
		{
			if (index >= 0 && index < Main.maxProjectiles)
				_seqByIndex[index] = ++_sequence;
		}

		/// <summary>
		/// True when this packet targets a projectile that is already alive under the same
		/// <c>(owner, identity)</c> pair, which Terraria treats as an update rather than a spawn.
		/// </summary>
		/// <remarks>
		/// Safe to read the pre-packet world state here: TShock's data handlers run before Terraria
		/// applies the packet.
		/// </remarks>
		private static bool IsUpdateToExisting(int index, int identity, int owner)
		{
			if (index < 0 || index >= Main.maxProjectiles)
				return false;

			Projectile p = Main.projectile[index];
			return p != null && p.active && p.owner == owner && p.identity == identity;
		}

		private void OnProjectileKill(object? sender, GetDataHandlers.ProjectileKillEventArgs args)
		{
			ProjectileBudgetSettings? settings = Settings;
			if (settings == null || !settings.Enabled)
				return;

			int owner = args.ProjectileOwner;
			if (owner < 0 || owner >= _countByOwner.Length)
				return;

			int index = args.ProjectileIndex;
			if (index >= 0 && index < Main.maxProjectiles)
			{
				// Exempt projectiles were never counted, so they must not decrement either. Decide
				// that from the projectile itself rather than from whether we stamped it: a brand new
				// projectile is counted before Terraria assigns it a slot to stamp, and keying off the
				// stamp would leak those counts until the next recount.
				Projectile p = Main.projectile[index];
				if (p != null && p.active && IsExemptProjectile(p, settings))
					return;

				_seqByIndex[index] = 0;
			}

			if (_countByOwner[owner] > 0)
				_countByOwner[owner]--;
		}

		/// <summary>Removes the given owner's longest-lived tracked projectile, if any is removable.</summary>
		private bool TryKillOldest(int owner, ProjectileBudgetSettings settings)
		{
			int best = -1;
			long bestSeq = long.MaxValue;

			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				Projectile p = Main.projectile[i];
				if (p == null || !p.active || p.owner != owner)
					continue;
				if (IsExemptProjectile(p, settings))
					continue;

				long seq = _seqByIndex[i];
				if (seq == 0 || seq >= bestSeq)
					continue;

				bestSeq = seq;
				best = i;
			}

			if (best < 0)
				return false;

			KillProjectile(best);
			if (_countByOwner[owner] > 0)
				_countByOwner[owner]--;
			_culled++;
			return true;
		}

		/// <summary>
		/// Deactivates a projectile and tells every client. Mirrors TShock's own <c>/clear projectile</c>
		/// handling: blank the type, then broadcast the slot as a fresh (empty) projectile.
		/// </summary>
		private static void KillProjectile(int index)
		{
			Projectile p = Main.projectile[index];
			p.active = false;
			p.type = 0;
			TSPlayer.All.SendData(PacketTypes.ProjectileNew, "", index);
		}

		/// <summary>
		/// Type-level exemption, usable before the projectile exists. Used on the packet path.
		/// </summary>
		private static bool IsExemptType(int type, ProjectileBudgetSettings settings)
		{
			if (type <= 0)
				return true;

			if (settings.ExemptProjectileIds != null && settings.ExemptProjectileIds.Contains(type))
				return true;

			if (settings.ExemptPets && IsPetType(type))
				return true;

			if (settings.ExemptMinionsAndSentries && IsMinionType(type))
				return true;

			return false;
		}

		/// <summary>
		/// Instance-level exemption, used when culling. Checks the same type rules plus the
		/// projectile's own minion/sentry flags, which are only known once it exists.
		/// </summary>
		private static bool IsExemptProjectile(Projectile p, ProjectileBudgetSettings settings)
		{
			if (IsExemptType(p.type, settings))
				return true;

			if (settings.ExemptMinionsAndSentries && (p.minion || p.sentry))
				return true;

			return false;
		}

		private static bool IsPetType(int type)
		{
			if (type > 0 && type < Main.projPet.Length && Main.projPet[type])
				return true;
			if (type > 0 && type < ProjectileID.Sets.LightPet.Length && ProjectileID.Sets.LightPet[type])
				return true;
			return false;
		}

		private static bool IsMinionType(int type) =>
			type > 0 &&
			type < ProjectileID.Sets.MinionSacrificable.Length &&
			ProjectileID.Sets.MinionSacrificable[type];

		private void Notify(TSPlayer? player, ProjectileBudgetSettings settings)
		{
			if (!settings.NotifyPlayer || player == null)
				return;

			int index = player.Index;
			if (index < 0 || index >= _lastNotify.Length)
				return;

			DateTime now = DateTime.UtcNow;
			if ((now - _lastNotify[index]).TotalSeconds < settings.NotifyCooldownSeconds)
				return;

			_lastNotify[index] = now;
			player.SendWarningMessage(
				$"[AntiLag] Projectile limit reached ({_currentBudget}). Some of your projectiles are being limited to reduce server lag.");
		}

		private static int TotalActive()
		{
			int n = 0;
			for (int i = 0; i < Main.maxProjectiles; i++)
			{
				Projectile p = Main.projectile[i];
				if (p != null && p.active)
					n++;
			}
			return n;
		}

		/// <inheritdoc />
		public string DescribeState()
		{
			if (Settings?.Enabled != true)
				return "disabled";

			int busiest = 0;
			int busiestOwner = -1;
			for (int i = 0; i < _countByOwner.Length; i++)
			{
				if (_countByOwner[i] > busiest)
				{
					busiest = _countByOwner[i];
					busiestOwner = i;
				}
			}

			string busiestName = busiestOwner >= 0 && busiestOwner < Main.maxPlayers
				? TShock.Players[busiestOwner]?.Name ?? "?"
				: "none";

			return $"budget {_currentBudget}/player | {TotalActive()}/{Main.maxProjectiles} active | " +
			       $"busiest {busiestName} ({busiest}) | {_rejected} rejected, {_culled} culled";
		}

		/// <inheritdoc />
		public void Dispose()
		{
			Unhook();
			_context = null;
		}
	}
}
