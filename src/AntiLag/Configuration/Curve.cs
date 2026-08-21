using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AntiLag.Configuration
{
	/// <summary>
	/// A piecewise curve mapping online player count to a strategy parameter (item age, projectile
	/// budget, spawn rate, ...). Server operators author the keyframes; this class fills in the rest.
	/// </summary>
	/// <remarks>
	/// Deliberately free of any Terraria or TShock type so it can be unit tested without a server.
	/// </remarks>
	public sealed class Curve
	{
		private CurvePoint[] _sorted = Array.Empty<CurvePoint>();
		private bool _prepared;

		/// <summary>How to interpolate between keyframes.</summary>
		/// <remarks>Written as a name ("Linear") rather than an integer, so the config stays readable.</remarks>
		[JsonProperty("Interpolation")]
		[JsonConverter(typeof(StringEnumConverter))]
		public InterpolationMode Interpolation { get; set; } = InterpolationMode.Linear;

		/// <summary>The keyframes. Need not be sorted; <see cref="Prepare"/> sorts a private copy.</summary>
		/// <remarks>
		/// <c>ObjectCreationHandling.Replace</c> is required, not cosmetic. Newtonsoft's default is to
		/// populate an already-initialized collection, so without it the default keyframes set in the
		/// constructor would be *appended to* by the ones read from disk, doubling every curve on load.
		/// </remarks>
		[JsonProperty("Points", ObjectCreationHandling = ObjectCreationHandling.Replace)]
		public List<CurvePoint> Points { get; set; } = new List<CurvePoint>();

		/// <summary>Creates an empty curve. Required for JSON deserialization.</summary>
		public Curve() { }

		/// <summary>Creates a curve from the given keyframes.</summary>
		public Curve(InterpolationMode interpolation, params CurvePoint[] points)
		{
			Interpolation = interpolation;
			Points = points?.ToList() ?? new List<CurvePoint>();
		}

		/// <summary>True when the curve has no usable keyframes and <see cref="Evaluate"/> will return the fallback.</summary>
		[JsonIgnore]
		public bool IsEmpty
		{
			get
			{
				EnsurePrepared();
				return _sorted.Length == 0;
			}
		}

		/// <summary>
		/// Sorts and caches the keyframes, and reports any problems worth telling the operator about.
		/// Call after deserializing or mutating <see cref="Points"/>.
		/// </summary>
		/// <param name="warnings">Human-readable problems found; empty when the curve is clean.</param>
		/// <returns>True when the curve has at least one usable keyframe.</returns>
		public bool Prepare(out IReadOnlyList<string> warnings)
		{
			var found = new List<string>();

			var usable = (Points ?? new List<CurvePoint>())
				.Where(p => p != null)
				.Where(p =>
				{
					if (double.IsNaN(p.Players) || double.IsInfinity(p.Players) ||
					    double.IsNaN(p.Value) || double.IsInfinity(p.Value))
					{
						found.Add($"dropped non-finite point {p}");
						return false;
					}
					return true;
				})
				.OrderBy(p => p.Players)
				.ToList();

			// Collapse duplicate player counts: last one authored wins, but say so.
			var deduped = new List<CurvePoint>(usable.Count);
			foreach (var p in usable)
			{
				if (deduped.Count > 0 && Math.Abs(deduped[deduped.Count - 1].Players - p.Players) < double.Epsilon)
				{
					found.Add($"duplicate point at Players={p.Players}; using Value={p.Value}");
					deduped[deduped.Count - 1] = p;
				}
				else
				{
					deduped.Add(p);
				}
			}

			_sorted = deduped.ToArray();
			_prepared = true;

			if (_sorted.Length == 0)
				found.Add("curve has no usable points; callers will fall back to their default");

			warnings = found;
			return _sorted.Length > 0;
		}

		private void EnsurePrepared()
		{
			if (!_prepared)
				Prepare(out _);
		}

		/// <summary>
		/// Evaluates the curve at the given player count. Clamps to the first/last keyframe outside
		/// the authored range, so the curve never extrapolates into nonsense.
		/// </summary>
		/// <param name="players">Online player count.</param>
		/// <param name="fallback">Returned when the curve has no usable keyframes.</param>
		public double Evaluate(double players, double fallback = 0d)
		{
			EnsurePrepared();

			if (_sorted.Length == 0)
				return fallback;
			if (_sorted.Length == 1 || players <= _sorted[0].Players)
				return _sorted[0].Value;
			if (players >= _sorted[_sorted.Length - 1].Players)
				return _sorted[_sorted.Length - 1].Value;

			// Find the segment [lo, hi] containing `players`.
			int lo = 0;
			int hi = _sorted.Length - 1;
			while (hi - lo > 1)
			{
				int mid = (lo + hi) / 2;
				if (_sorted[mid].Players <= players) lo = mid;
				else hi = mid;
			}

			CurvePoint a = _sorted[lo];
			CurvePoint b = _sorted[hi];

			double span = b.Players - a.Players;
			if (span <= 0d)
				return b.Value;

			double t = (players - a.Players) / span;

			switch (Interpolation)
			{
				case InterpolationMode.Step:
					return a.Value;
				case InterpolationMode.SmoothStep:
					t = t * t * (3d - 2d * t);
					break;
			}

			return a.Value + (b.Value - a.Value) * t;
		}

		/// <summary>Evaluates the curve and rounds to the nearest integer.</summary>
		public int EvaluateInt(double players, int fallback = 0)
		{
			double v = Evaluate(players, fallback);
			if (double.IsNaN(v) || double.IsInfinity(v))
				return fallback;
			return (int)Math.Round(v, MidpointRounding.AwayFromZero);
		}

		/// <inheritdoc />
		public override string ToString()
		{
			EnsurePrepared();
			return _sorted.Length == 0
				? "<empty curve>"
				: $"{Interpolation}[{string.Join(" ", _sorted.Select(p => p.ToString()))}]";
		}
	}
}
