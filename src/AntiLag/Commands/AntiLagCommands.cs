using System;
using System.Collections.Generic;
using System.Linq;
using AntiLag.Configuration;
using AntiLag.Load;
using AntiLag.Strategies;
using TShockAPI;

namespace AntiLag.Commands
{
	/// <summary>Registers and handles the <c>/antilag</c> command tree.</summary>
	public sealed class AntiLagCommands
	{
		/// <summary>Permission required to use <c>/antilag</c>.</summary>
		public const string AdminPermission = "antilag.admin";

		private readonly StrategyManager _manager;
		private readonly Func<AntiLagSettings> _settings;
		private readonly Func<LoadSample> _load;
		private readonly Action _reloadFromDisk;
		private readonly Action _reapply;
		private Command? _command;

		/// <summary>Creates the command handler.</summary>
		/// <param name="manager">The strategy manager to inspect and drive.</param>
		/// <param name="settings">Returns the live settings object.</param>
		/// <param name="load">Returns the current load sample.</param>
		/// <param name="reloadFromDisk">Re-reads the config file and re-initializes strategies.</param>
		/// <param name="reapply">
		/// Re-initializes strategies against the settings already in memory, without touching disk.
		/// Distinct from <paramref name="reloadFromDisk"/> because the session-only toggles must not
		/// have their in-memory change overwritten by the file they are deliberately diverging from.
		/// </param>
		public AntiLagCommands(
			StrategyManager manager,
			Func<AntiLagSettings> settings,
			Func<LoadSample> load,
			Action reloadFromDisk,
			Action reapply)
		{
			_manager = manager ?? throw new ArgumentNullException(nameof(manager));
			_settings = settings ?? throw new ArgumentNullException(nameof(settings));
			_load = load ?? throw new ArgumentNullException(nameof(load));
			_reloadFromDisk = reloadFromDisk ?? throw new ArgumentNullException(nameof(reloadFromDisk));
			_reapply = reapply ?? throw new ArgumentNullException(nameof(reapply));
		}

		/// <summary>Adds <c>/antilag</c> to TShock's command list.</summary>
		public void Register()
		{
			_command = new Command(AdminPermission, Handle, "antilag", "al")
			{
				HelpText = "Inspects and controls anti-lag strategies. Use /antilag help."
			};
			TShockAPI.Commands.ChatCommands.Add(_command);
		}

		/// <summary>Removes <c>/antilag</c>. Required so a plugin reload does not leave a duplicate behind.</summary>
		public void Deregister()
		{
			if (_command != null)
				TShockAPI.Commands.ChatCommands.Remove(_command);
			_command = null;
		}

		private void Handle(CommandArgs args)
		{
			string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "status";

			switch (sub)
			{
				case "status": Status(args); break;
				case "list": List(args); break;
				case "reload": Reload(args); break;
				case "enable": Toggle(args, true); break;
				case "disable": Toggle(args, false); break;
				case "sweep": Sweep(args); break;
				default: Help(args); break;
			}
		}

		private void Status(CommandArgs args)
		{
			LoadSample load = _load();
			AntiLagSettings settings = _settings();

			args.Player.SendInfoMessage("AntiLag status");
			args.Player.SendInfoMessage(
				settings.Enabled
					? $"  Load: {load.PlayerCount} players, {load.Tps:F1} TPS ({load.Health:P0} of target)"
					: "  Plugin is globally DISABLED (Enabled: false in AntiLag.json)");

			foreach (IAntiLagStrategy s in _manager.Strategies)
			{
				string state;
				try
				{
					state = s.DescribeState();
				}
				catch (Exception ex)
				{
					state = $"error: {ex.Message}";
				}

				args.Player.SendInfoMessage($"  {s.Name}: {state}");
			}
		}

		private void List(CommandArgs args)
		{
			args.Player.SendInfoMessage("AntiLag strategies");
			foreach (IAntiLagStrategy s in _manager.Strategies)
				args.Player.SendInfoMessage($"  {s.Name} [{(s.Enabled ? "on" : "off")}] - {s.Description}");
		}

		private void Reload(CommandArgs args)
		{
			try
			{
				_reloadFromDisk();
				args.Player.SendSuccessMessage("[AntiLag] Configuration reloaded from disk.");
			}
			catch (Exception ex)
			{
				args.Player.SendErrorMessage($"[AntiLag] Reload failed: {ex.Message}");
			}
		}

		private void Toggle(CommandArgs args, bool enable)
		{
			if (args.Parameters.Count < 2)
			{
				args.Player.SendErrorMessage($"Usage: /antilag {(enable ? "enable" : "disable")} <strategy>");
				List(args);
				return;
			}

			string name = args.Parameters[1];
			IAntiLagStrategy? strategy = _manager.Find(name);
			if (strategy == null)
			{
				args.Player.SendErrorMessage($"No strategy named '{name}'. Known: {string.Join(", ", _manager.Strategies.Select(s => s.Name))}");
				return;
			}

			StrategySettings? section = SectionFor(strategy.Name, _settings());
			if (section == null)
			{
				args.Player.SendErrorMessage($"Strategy '{strategy.Name}' has no settings section to toggle.");
				return;
			}

			section.Enabled = enable;

			// Re-initialize against the in-memory settings so the strategy registers or releases its
			// hooks to match. Deliberately NOT a reload-from-disk: that would re-read AntiLag.json and
			// immediately discard the toggle we just made.
			try
			{
				_reapply.Invoke();
			}
			catch (Exception ex)
			{
				args.Player.SendErrorMessage($"[AntiLag] Toggle applied but re-initialize failed: {ex.Message}");
				return;
			}

			args.Player.SendSuccessMessage(
				$"[AntiLag] Strategy '{strategy.Name}' {(enable ? "enabled" : "disabled")} for this session. " +
				"Edit AntiLag.json to make it permanent.");
		}

		/// <summary>
		/// Maps a strategy name to its settings section. Kept explicit rather than reflective so that
		/// adding a strategy fails to compile here rather than silently doing nothing at runtime.
		/// </summary>
		private static StrategySettings? SectionFor(string name, AntiLagSettings settings) => name switch
		{
			"grounditems" => settings.GroundItemCleanup,
			"projectiles" => settings.ProjectileBudget,
			"npcspawn" => settings.NpcSpawnThrottle,
			_ => null
		};

		private void Sweep(CommandArgs args)
		{
			IEnumerable<IAntiLagStrategy> targets = _manager.Strategies;

			if (args.Parameters.Count >= 2)
			{
				IAntiLagStrategy? one = _manager.Find(args.Parameters[1]);
				if (one == null)
				{
					args.Player.SendErrorMessage($"No strategy named '{args.Parameters[1]}'.");
					return;
				}
				targets = new[] { one };
			}

			foreach (IAntiLagStrategy s in targets)
			{
				string result;
				try
				{
					result = s.ForceRun();
				}
				catch (Exception ex)
				{
					result = $"error: {ex.Message}";
				}

				args.Player.SendInfoMessage($"[AntiLag] {s.Name}: {result}");
			}
		}

		private void Help(CommandArgs args)
		{
			args.Player.SendInfoMessage("AntiLag commands");
			args.Player.SendInfoMessage("  /antilag status            - current load and per-strategy state");
			args.Player.SendInfoMessage("  /antilag list              - all strategies and whether they are on");
			args.Player.SendInfoMessage("  /antilag reload            - re-read AntiLag.json from disk");
			args.Player.SendInfoMessage("  /antilag enable <name>     - turn a strategy on for this session");
			args.Player.SendInfoMessage("  /antilag disable <name>    - turn a strategy off for this session");
			args.Player.SendInfoMessage("  /antilag sweep [name]      - run strategies immediately");
		}
	}
}
