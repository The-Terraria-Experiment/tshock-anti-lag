using AntiLag.Load;
using Xunit;

namespace AntiLag.Tests
{
	public class LoadSampleTests
	{
		[Fact]
		public void Health_IsFractionOfTargetTps()
		{
			Assert.Equal(1.0d, new LoadSample(0, 60).Health, 6);
			Assert.Equal(0.5d, new LoadSample(0, 30).Health, 6);
			Assert.Equal(0.25d, new LoadSample(0, 15).Health, 6);
		}

		[Fact]
		public void Health_IsCappedAtOne_SoOvershootIsNotTreatedAsSpareCapacity()
		{
			Assert.Equal(1.0d, new LoadSample(0, 120).Health, 6);
		}

		[Fact]
		public void Constructor_RejectsNonsenseTpsAndFallsBackToTarget()
		{
			Assert.Equal(LoadSample.TargetTps, new LoadSample(0, double.NaN).Tps);
			Assert.Equal(LoadSample.TargetTps, new LoadSample(0, double.PositiveInfinity).Tps);
			Assert.Equal(LoadSample.TargetTps, new LoadSample(0, -5).Tps);
		}

		[Fact]
		public void Constructor_ClampsNegativePlayerCountToZero()
		{
			Assert.Equal(0, new LoadSample(-3, 60).PlayerCount);
		}

		[Fact]
		public void ApplyTpsFactor_AtFullHealth_LeavesBaselineAlone()
		{
			var healthy = new LoadSample(10, 60);

			Assert.Equal(100, healthy.ApplyTpsFactor(100, strength: 1.0d, min: 0, max: 1000));
		}

		[Fact]
		public void ApplyTpsFactor_AtZeroStrength_IgnoresTpsEntirely()
		{
			var struggling = new LoadSample(10, 15);

			Assert.Equal(100, struggling.ApplyTpsFactor(100, strength: 0d, min: 0, max: 1000));
		}

		[Fact]
		public void ApplyTpsFactor_AtFullStrength_ScalesDirectlyByHealth()
		{
			var half = new LoadSample(10, 30);

			Assert.Equal(50, half.ApplyTpsFactor(100, strength: 1.0d, min: 0, max: 1000));
		}

		[Fact]
		public void ApplyTpsFactor_AtPartialStrength_BlendsBetweenBaselineAndHealth()
		{
			var half = new LoadSample(10, 30);

			// factor = (1 - 0.5) + 0.5 * 0.5 = 0.75
			Assert.Equal(75, half.ApplyTpsFactor(100, strength: 0.5d, min: 0, max: 1000));
		}

		[Fact]
		public void ApplyTpsFactor_RespectsMinimumClamp()
		{
			var dying = new LoadSample(10, 6);

			Assert.Equal(40, dying.ApplyTpsFactor(100, strength: 1.0d, min: 40, max: 1000));
		}

		[Fact]
		public void ApplyTpsFactor_RespectsMaximumClamp()
		{
			var healthy = new LoadSample(10, 60);

			Assert.Equal(50, healthy.ApplyTpsFactor(100, strength: 1.0d, min: 0, max: 50));
		}

		[Fact]
		public void ApplyTpsFactor_SwappedBounds_AreTreatedAsARange_NotAnError()
		{
			var healthy = new LoadSample(10, 60);

			// min and max supplied backwards by a careless config edit should still clamp sanely.
			Assert.Equal(50, healthy.ApplyTpsFactor(100, strength: 0d, min: 50, max: 10));
		}

		[Fact]
		public void ApplyTpsFactor_StrengthAboveOne_IsClampedToOne()
		{
			var half = new LoadSample(10, 30);

			Assert.Equal(50, half.ApplyTpsFactor(100, strength: 5.0d, min: 0, max: 1000));
		}
	}
}
