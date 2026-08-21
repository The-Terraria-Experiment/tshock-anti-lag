using System.Collections.Generic;
using System.Linq;
using AntiLag.Configuration;
using Xunit;

namespace AntiLag.Tests
{
	public class CurveTests
	{
		private static Curve Standard(InterpolationMode mode = InterpolationMode.Linear) => new Curve(
			mode,
			new CurvePoint(0, 600),
			new CurvePoint(10, 300),
			new CurvePoint(25, 120),
			new CurvePoint(50, 45));

		[Fact]
		public void Evaluate_AtKeyframes_ReturnsExactAuthoredValues()
		{
			Curve curve = Standard();

			Assert.Equal(600, curve.Evaluate(0));
			Assert.Equal(300, curve.Evaluate(10));
			Assert.Equal(120, curve.Evaluate(25));
			Assert.Equal(45, curve.Evaluate(50));
		}

		[Fact]
		public void Evaluate_BetweenKeyframes_InterpolatesLinearly()
		{
			Curve curve = Standard();

			// Midpoint of the 0..10 segment: halfway between 600 and 300.
			Assert.Equal(450, curve.Evaluate(5));
			// Midpoint of the 10..25 segment: halfway between 300 and 120.
			Assert.Equal(210, curve.Evaluate(17.5));
		}

		[Fact]
		public void Evaluate_OutsideAuthoredRange_ClampsInsteadOfExtrapolating()
		{
			Curve curve = Standard();

			Assert.Equal(600, curve.Evaluate(-5));
			Assert.Equal(45, curve.Evaluate(500));
		}

		[Fact]
		public void Evaluate_UnsortedPoints_SortsBeforeEvaluating()
		{
			var curve = new Curve(
				InterpolationMode.Linear,
				new CurvePoint(50, 45),
				new CurvePoint(0, 600),
				new CurvePoint(25, 120),
				new CurvePoint(10, 300));

			Assert.Equal(600, curve.Evaluate(0));
			Assert.Equal(450, curve.Evaluate(5));
			Assert.Equal(45, curve.Evaluate(50));
		}

		[Fact]
		public void Evaluate_EmptyCurve_ReturnsFallback()
		{
			var curve = new Curve(InterpolationMode.Linear);

			Assert.True(curve.IsEmpty);
			Assert.Equal(999, curve.Evaluate(10, fallback: 999));
		}

		[Fact]
		public void Evaluate_SinglePoint_ReturnsThatValueEverywhere()
		{
			var curve = new Curve(InterpolationMode.Linear, new CurvePoint(10, 42));

			Assert.Equal(42, curve.Evaluate(0));
			Assert.Equal(42, curve.Evaluate(10));
			Assert.Equal(42, curve.Evaluate(1000));
		}

		[Fact]
		public void StepMode_HoldsLowerKeyframeUntilTheNextOne()
		{
			Curve curve = Standard(InterpolationMode.Step);

			Assert.Equal(600, curve.Evaluate(0));
			Assert.Equal(600, curve.Evaluate(9.9));
			Assert.Equal(300, curve.Evaluate(10));
			Assert.Equal(300, curve.Evaluate(24.9));
			Assert.Equal(120, curve.Evaluate(25));
		}

		[Fact]
		public void SmoothStepMode_MatchesLinearAtKeyframesAndMidpoint()
		{
			Curve curve = Standard(InterpolationMode.SmoothStep);

			// Smoothstep is symmetric, so t=0, t=0.5 and t=1 agree with linear.
			Assert.Equal(600, curve.Evaluate(0));
			Assert.Equal(450, curve.Evaluate(5), 6);
			Assert.Equal(300, curve.Evaluate(10));

			// But it eases in, so a quarter of the way along it has moved less than linear would.
			double quarter = curve.Evaluate(2.5);
			Assert.True(quarter > 525, $"expected easing above the linear 525, got {quarter}");
		}

		[Fact]
		public void Prepare_DuplicatePlayerCounts_KeepsLastAndWarns()
		{
			var curve = new Curve(
				InterpolationMode.Linear,
				new CurvePoint(0, 100),
				new CurvePoint(10, 200),
				new CurvePoint(10, 250));

			bool usable = curve.Prepare(out IReadOnlyList<string> warnings);

			Assert.True(usable);
			Assert.Contains(warnings, w => w.Contains("duplicate"));
			Assert.Equal(250, curve.Evaluate(10));
		}

		[Fact]
		public void Prepare_NonFinitePoints_AreDroppedWithAWarning()
		{
			var curve = new Curve(
				InterpolationMode.Linear,
				new CurvePoint(0, 100),
				new CurvePoint(double.NaN, 5),
				new CurvePoint(10, double.PositiveInfinity),
				new CurvePoint(20, 300));

			bool usable = curve.Prepare(out IReadOnlyList<string> warnings);

			Assert.True(usable);
			Assert.Equal(2, warnings.Count(w => w.Contains("non-finite")));
			Assert.Equal(100, curve.Evaluate(0));
			Assert.Equal(300, curve.Evaluate(20));
		}

		[Fact]
		public void Prepare_EmptyCurve_ReportsUnusable()
		{
			var curve = new Curve(InterpolationMode.Linear);

			Assert.False(curve.Prepare(out IReadOnlyList<string> warnings));
			Assert.NotEmpty(warnings);
		}

		[Fact]
		public void EvaluateInt_RoundsHalfAwayFromZero()
		{
			var curve = new Curve(InterpolationMode.Linear, new CurvePoint(0, 0), new CurvePoint(10, 5));

			// t=0.5 along a 0..5 range gives 2.5, which must round to 3, not 2.
			Assert.Equal(3, curve.EvaluateInt(5));
		}

		[Fact]
		public void EvaluateInt_EmptyCurve_ReturnsFallback()
		{
			var curve = new Curve(InterpolationMode.Linear);

			Assert.Equal(77, curve.EvaluateInt(10, fallback: 77));
		}

		[Fact]
		public void SpawnRateStyleCurve_RisesWithPlayerCount()
		{
			// Spawn rate is a delay, so the shipped default curve must increase, not decrease.
			var curve = new Curve(
				InterpolationMode.Linear,
				new CurvePoint(0, 600),
				new CurvePoint(15, 800),
				new CurvePoint(30, 1100),
				new CurvePoint(50, 1600));

			Assert.True(curve.Evaluate(0) < curve.Evaluate(15));
			Assert.True(curve.Evaluate(15) < curve.Evaluate(30));
			Assert.True(curve.Evaluate(30) < curve.Evaluate(50));
		}
	}
}
