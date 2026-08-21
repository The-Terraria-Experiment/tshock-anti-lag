using System.Threading;
using AntiLag.Load;
using Xunit;

namespace AntiLag.Tests
{
	public class LoadMonitorTests
	{
		[Fact]
		public void Current_BeforeAnyWindowCloses_ReportsTargetTps()
		{
			var monitor = new LoadMonitor(() => 3);

			Assert.Equal(LoadSample.TargetTps, monitor.Current.Tps);
		}

		[Fact]
		public void RecordTick_ReturnsFalseUntilTheWindowElapses()
		{
			var monitor = new LoadMonitor(() => 0, windowSeconds: 60);

			for (int i = 0; i < 100; i++)
				Assert.False(monitor.RecordTick());
		}

		[Fact]
		public void RecordTick_ClosingAWindow_PublishesPlayerCountFromTheSource()
		{
			int players = 7;
			var monitor = new LoadMonitor(() => players, windowSeconds: 0.05d);

			CloseAWindow(monitor);

			Assert.Equal(7, monitor.Current.PlayerCount);
		}

		[Fact]
		public void MeasuredTps_NeverExceedsTarget_EvenWhenTicksArriveFasterThanRealTime()
		{
			// Hammer ticks with no delay: the raw rate would be enormous, but Terraria never
			// intentionally runs above 60, so overshoot must be reported as healthy rather than as a spike.
			var monitor = new LoadMonitor(() => 0, windowSeconds: 0.05d);

			CloseAWindow(monitor, ticksPerLoop: 500);

			Assert.True(monitor.LastRawTps <= LoadSample.TargetTps,
				$"raw TPS should be capped at {LoadSample.TargetTps}, got {monitor.LastRawTps}");
			Assert.True(monitor.Current.Tps <= LoadSample.TargetTps);
		}

		[Fact]
		public void FirstWindow_SeedsTheAverageInsteadOfEasingFromTheDefault()
		{
			// Only a handful of ticks in a comparatively long window: a genuinely slow server.
			var monitor = new LoadMonitor(() => 0, smoothing: 0.25d, windowSeconds: 0.1d);

			Thread.Sleep(120);
			monitor.RecordTick();

			// Seeded directly from the first reading, so it must be far below 60 rather than
			// the ~59 an EMA starting at 60 would have produced.
			Assert.True(monitor.Current.Tps < 30d,
				$"first sample should seed the average, expected well under 30 TPS, got {monitor.Current.Tps:F2}");
		}

		[Fact]
		public void Reset_RestoresTheTargetTpsBaseline()
		{
			var monitor = new LoadMonitor(() => 0, windowSeconds: 0.05d);
			Thread.Sleep(60);
			monitor.RecordTick();

			monitor.Reset();

			Assert.Equal(LoadSample.TargetTps, monitor.LastRawTps);
		}

		private static void CloseAWindow(LoadMonitor monitor, int ticksPerLoop = 1)
		{
			for (int attempt = 0; attempt < 200; attempt++)
			{
				for (int i = 0; i < ticksPerLoop; i++)
				{
					if (monitor.RecordTick())
						return;
				}
				Thread.Sleep(5);
			}

			Assert.Fail("the measurement window never closed");
		}
	}
}
