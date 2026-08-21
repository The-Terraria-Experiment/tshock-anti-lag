using System;
using System.IO;
using AntiLag.Commands;
using AntiLag.Configuration;
using AntiLag.Load;
using AntiLag.Strategies;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Configuration;
using TShockAPI.Hooks;

namespace AntiLag
{
	/// <summary>
	/// Plugin entry point. Owns configuration, the load monitor, and the strategy manager, and wires
	/// the single game-update hook that drives everything else.
	/// </summary>
	[ApiVersion(2, 1)]
	public sealed class AntiLagPlugin : TerrariaPlugin
	{
		private readonly StrategyManager _manager = new StrategyManager();
		private ConfigFile<AntiLagSettings> _config = new ConfigFile<AntiLagSettings>();
		private LoadMonitor? _loadMonitor;
		private AntiLagCommands? _commands;
		private bool _hooked;

		/// <inheritdoc />
		public override string Name => "AntiLag";

		/// <inheritdoc />
		public override string Author => "Caleb Dougal";

		/// <inheritdoc />
		public override string Description => "Reduces server lag through configurable, load-aware strategies.";

		/// <inheritdoc />
		public override Version Version => new Version(0, 1, 0, 0);

		/// <summary>Path to this plugin's config file, alongside TShock's own.</summary>
		public static string ConfigPath => Path.Combine(TShock.SavePath, "AntiLag.json");

		/// <summary>Creates the plugin.</summary>
		public AntiLagPlugin(Main game) : base(game)
		{
			// Load before TShock's own plugins that might depend on spawn settings.
			Order = 1;
		}

		/// <inheritdoc />
		public override void Initialize()
		{
			LoadConfig();

			_loadMonitor = new LoadMonitor(
				() => TShock.Utils.GetActivePlayerCount(),
				_config.Settings.LoadMonitor.Smoothing,
				_config.Settings.LoadMonitor.WindowSeconds);

			_manager.RegisterDefaults();
			_manager.Initialize(new AntiLagContext(_config.Settings, _loadMonitor));

			_commands = new AntiLagCommands(
				_manager,
				() => _config.Settings,
				() => _loadMonitor!.Current,
				Reload,
				Reapply);
			_commands.Register();

			ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
			GeneralHooks.ReloadEvent += OnReload;
			_hooked = true;

			TShock.Log.ConsoleInfo($"[AntiLag] v{Version} loaded with {_manager.Strategies.Count} strategies.");
		}

		private void OnGameUpdate(EventArgs args) => _manager.OnGameUpdate();

		private void OnReload(ReloadEventArgs e)
		{
			try
			{
				Reload();
				e.Player?.SendSuccessMessage("[AntiLag] Configuration reloaded.");
			}
			catch (Exception ex)
			{
				e.Player?.SendErrorMessage($"[AntiLag] Reload failed: {ex.Message}");
				TShock.Log.ConsoleError($"[AntiLag] Reload failed: {ex}");
			}
		}

		/// <summary>
		/// Re-reads the config from disk and re-initializes every strategy against it.
		/// </summary>
		/// <remarks>
		/// Strategies are re-initialized rather than recreated, so state that should survive a reload
		/// (the NPC strategy's captured vanilla spawn values, for instance) is preserved.
		/// </remarks>
		public void Reload()
		{
			LoadConfig();

			// The monitor's smoothing and window come from config, so it is rebuilt rather than reused.
			_loadMonitor = new LoadMonitor(
				() => TShock.Utils.GetActivePlayerCount(),
				_config.Settings.LoadMonitor.Smoothing,
				_config.Settings.LoadMonitor.WindowSeconds);

			_manager.Initialize(new AntiLagContext(_config.Settings, _loadMonitor));
		}

		/// <summary>
		/// Re-initializes every strategy against the settings currently in memory, without reading
		/// from disk.
		/// </summary>
		/// <remarks>
		/// Backs the session-only <c>/antilag enable</c> and <c>/antilag disable</c> toggles. Those
		/// deliberately diverge from the file on disk, so re-reading it here would discard them.
		/// </remarks>
		public void Reapply()
		{
			if (_loadMonitor == null)
				return;

			_manager.Initialize(new AntiLagContext(_config.Settings, _loadMonitor));
		}

		private void LoadConfig()
		{
			try
			{
				string path = ConfigPath;

				if (File.Exists(path))
				{
					_config.Read(path, out bool incomplete);
					// A config written by an older version is missing the newer keys; rewrite it so the
					// operator can see and edit everything that is actually available.
					if (incomplete)
						_config.Write(path);
				}
				else
				{
					_config.Settings = new AntiLagSettings();
					_config.Write(path);
					TShock.Log.ConsoleInfo($"[AntiLag] Wrote default config to {path}");
				}

				if (_config.Settings == null)
					_config.Settings = new AntiLagSettings();
			}
			catch (Exception ex)
			{
				TShock.Log.ConsoleError($"[AntiLag] Failed to read config, falling back to defaults: {ex.Message}");
				_config.Settings = new AntiLagSettings();
			}
		}

		/// <summary>Persists the current settings to disk.</summary>
		public void SaveConfig()
		{
			try
			{
				_config.Write(ConfigPath);
			}
			catch (Exception ex)
			{
				TShock.Log.ConsoleError($"[AntiLag] Failed to write config: {ex.Message}");
			}
		}

		/// <inheritdoc />
		protected override void Dispose(bool disposing)
		{
			if (disposing && _hooked)
			{
				ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
				GeneralHooks.ReloadEvent -= OnReload;
				_commands?.Deregister();
				_manager.Dispose();
				_hooked = false;
			}

			base.Dispose(disposing);
		}
	}
}
