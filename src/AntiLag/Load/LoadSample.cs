using System;

namespace AntiLag.Load
{
	/// <summary>
	/// An immutable snapshot of how hard the server is currently working.
	/// </summary>
	/// <remarks>
	/// Deliberately free of any Terraria or TShock type so it can be unit tested without a server.
	/// </remarks>
	public readonly struct LoadSample : IEquatable<LoadSample>
	{
		/// <summary>The tick rate Terraria targets when healthy.</summary>
		public const double TargetTps = 60d;

		/// <summary>Number of players connected and past the handshake.</summary>
		public int PlayerCount { get; }

		/// <summary>Measured server tick rate, smoothed. Equals <see cref="TargetTps"/> before the first full sample.</summary>
		public double Tps { get; }

		/// <summary>Creates a sample.</summary>
		public LoadSample(int playerCount, double tps)
		{
			PlayerCount = playerCount < 0 ? 0 : playerCount;
			Tps = double.IsNaN(tps) || double.IsInfinity(tps) || tps < 0d ? TargetTps : tps;
		}

		/// <summary>
		/// How healthy the server is, as a 0..1 fraction of the target tick rate.
		/// 1.0 means running at (or above) 60 TPS; 0.5 means running at half speed.
		/// </summary>
		public double Health
		{
			get
			{
				double h = Tps / TargetTps;
				return h > 1d ? 1d : (h < 0d ? 0d : h);
			}
		}

		/// <summary>
		/// Scales a curve-derived budget by server health, so a struggling server tightens its
		/// budgets below what player count alone would ask for.
		/// </summary>
		/// <param name="baseline">The value the curve produced from player count.</param>
		/// <param name="strength">
		/// How much TPS is allowed to matter, 0..1. At 0 the baseline passes through untouched;
		/// at 1 a half-speed server halves the baseline.
		/// </param>
		/// <param name="min">Lower clamp applied after scaling.</param>
		/// <param name="max">Upper clamp applied after scaling.</param>
		public int ApplyTpsFactor(int baseline, double strength, int min, int max)
		{
			if (min > max)
			{
				int swap = min;
				min = max;
				max = swap;
			}

			if (strength <= 0d)
				return Clamp(baseline, min, max);

			if (strength > 1d)
				strength = 1d;

			// Blend between "ignore TPS" (1.0) and "scale fully by health" (Health).
			double factor = (1d - strength) + strength * Health;
			double scaled = baseline * factor;

			int result = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
			return Clamp(result, min, max);
		}

		private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

		/// <inheritdoc />
		public bool Equals(LoadSample other) => PlayerCount == other.PlayerCount && Math.Abs(Tps - other.Tps) < 0.0001d;

		/// <inheritdoc />
		public override bool Equals(object? obj) => obj is LoadSample other && Equals(other);

		/// <inheritdoc />
		public override int GetHashCode() => unchecked((PlayerCount * 397) ^ Math.Round(Tps, 3).GetHashCode());

		/// <inheritdoc />
		public override string ToString() => $"{PlayerCount} players, {Tps:F1} TPS";
	}
}
