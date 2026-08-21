using System;
using AntiLag.Load;

namespace AntiLag.Strategies
{
	/// <summary>
	/// One independent anti-lag tactic. Strategies are self-contained: they own their hooks, their
	/// state, and their slice of the config, and they are told about load changes rather than
	/// measuring load themselves.
	/// </summary>
	/// <remarks>
	/// To add a strategy: implement this interface and register it in
	/// <see cref="StrategyManager.RegisterDefaults"/>. Nothing else needs to change.
	/// </remarks>
	public interface IAntiLagStrategy : IDisposable
	{
		/// <summary>
		/// Stable identifier used as the config section name and the <c>/antilag</c> command token.
		/// Lowercase, no spaces.
		/// </summary>
		string Name { get; }

		/// <summary>One-line description shown by <c>/antilag list</c>.</summary>
		string Description { get; }

		/// <summary>Whether this strategy is currently doing anything.</summary>
		bool Enabled { get; }

		/// <summary>
		/// Called once at plugin start, and again after each config reload. Register hooks and read
		/// settings here. Implementations must tolerate being called repeatedly without leaking
		/// duplicate hook registrations.
		/// </summary>
		void Initialize(AntiLagContext context);

		/// <summary>
		/// Called when the measured load has changed meaningfully. Recompute budgets from curves here
		/// rather than in <see cref="OnSweep"/>, so the expensive maths happens about once a second
		/// instead of on every sweep.
		/// </summary>
		void OnLoadChanged(in LoadSample load);

		/// <summary>
		/// Called on the configured sweep interval to do periodic bulk work. Runs on the main server
		/// thread, so it must stay cheap.
		/// </summary>
		/// <param name="tick">Monotonic tick counter since plugin start.</param>
		void OnSweep(long tick);

		/// <summary>One-line current state for <c>/antilag status</c>.</summary>
		string DescribeState();

		/// <summary>
		/// Force this strategy to act immediately, ignoring its normal schedule. Backs
		/// <c>/antilag sweep</c>. Returns a human-readable summary of what happened.
		/// </summary>
		string ForceRun();
	}
}
