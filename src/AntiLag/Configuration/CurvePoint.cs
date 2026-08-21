using Newtonsoft.Json;

namespace AntiLag.Configuration
{
	/// <summary>
	/// A single keyframe on a <see cref="Curve"/>: at <see cref="Players"/> online players,
	/// the curve evaluates to <see cref="Value"/>.
	/// </summary>
	public sealed class CurvePoint
	{
		/// <summary>Player count this keyframe applies at.</summary>
		[JsonProperty("Players")]
		public double Players { get; set; }

		/// <summary>Value the curve takes at <see cref="Players"/>.</summary>
		[JsonProperty("Value")]
		public double Value { get; set; }

		/// <summary>Creates an empty keyframe. Required for JSON deserialization.</summary>
		public CurvePoint() { }

		/// <summary>Creates a keyframe at <paramref name="players"/> with the given <paramref name="value"/>.</summary>
		public CurvePoint(double players, double value)
		{
			Players = players;
			Value = value;
		}

		/// <inheritdoc />
		public override string ToString() => $"({Players}, {Value})";
	}
}
