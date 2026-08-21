using System;
using AntiLag.Configuration;
using AntiLag.Load;
using TShockAPI;

namespace AntiLag
{
	/// <summary>
	/// Services handed to every strategy at initialization. Keeps strategies from reaching into
	/// plugin statics, which is what makes them testable and independently disableable.
	/// </summary>
	public sealed class AntiLagContext
	{
		/// <summary>Creates a context.</summary>
		public AntiLagContext(AntiLagSettings settings, LoadMonitor loadMonitor)
		{
			Settings = settings ?? throw new ArgumentNullException(nameof(settings));
			LoadMonitor = loadMonitor ?? throw new ArgumentNullException(nameof(loadMonitor));
		}

		/// <summary>The live settings object. Replaced wholesale on reload, so read it, do not cache it.</summary>
		public AntiLagSettings Settings { get; }

		/// <summary>The shared load monitor.</summary>
		public LoadMonitor LoadMonitor { get; }

		/// <summary>Writes an informational line to the server log, tagged with the plugin name.</summary>
		public void LogInfo(string message) => TShock.Log.ConsoleInfo($"[AntiLag] {message}");

		/// <summary>Writes a warning to the server log, tagged with the plugin name.</summary>
		public void LogWarn(string message) => TShock.Log.ConsoleError($"[AntiLag] {message}");

		/// <summary>Writes a debug line to the server log, tagged with the plugin name.</summary>
		public void LogDebug(string message) => TShock.Log.ConsoleDebug($"[AntiLag] {message}");
	}
}
