using AntiLag.Configuration;
using Newtonsoft.Json;
using Xunit;

namespace AntiLag.Tests
{
	/// <summary>
	/// Config serialization is otherwise only exercised on a live server, where a mistake shows up as
	/// a silently-defaulted setting rather than an error. These tests pin the on-disk shape.
	/// </summary>
	public class SettingsSerializationTests
	{
		private static string Serialize(AntiLagSettings s) => JsonConvert.SerializeObject(s, Formatting.Indented);

		[Fact]
		public void Defaults_SerializeAndDeserializeUnchanged()
		{
			var original = new AntiLagSettings();

			var round = JsonConvert.DeserializeObject<AntiLagSettings>(Serialize(original));

			Assert.NotNull(round);
			Assert.Equal(original.Enabled, round!.Enabled);
			Assert.Equal(original.SweepIntervalTicks, round.SweepIntervalTicks);
			Assert.Equal(original.GroundItemCleanup.MinItemAgeSeconds, round.GroundItemCleanup.MinItemAgeSeconds);
			Assert.Equal(original.ProjectileBudget.MinBudget, round.ProjectileBudget.MinBudget);
			Assert.Equal(original.NpcSpawnThrottle.MaxMaxSpawns, round.NpcSpawnThrottle.MaxMaxSpawns);
		}

		[Fact]
		public void InterpolationMode_IsWrittenAsAName_NotAnInteger()
		{
			string json = Serialize(new AntiLagSettings());

			Assert.Contains("\"Interpolation\": \"Linear\"", json);
			Assert.DoesNotContain("\"Interpolation\": 0", json);
		}

		[Fact]
		public void Enforcement_IsWrittenAsAName_NotAnInteger()
		{
			string json = Serialize(new AntiLagSettings());

			Assert.Contains("\"Enforcement\": \"RejectNew\"", json);
		}

		[Fact]
		public void CurvePoints_SurviveARoundTripWithTheirValues()
		{
			var original = new AntiLagSettings();

			var round = JsonConvert.DeserializeObject<AntiLagSettings>(Serialize(original))!;
			Curve curve = round.GroundItemCleanup.MaxItemAgeSeconds;

			Assert.Equal(original.GroundItemCleanup.MaxItemAgeSeconds.Points.Count, curve.Points.Count);
			Assert.Equal(600, curve.Evaluate(0));
			Assert.Equal(45, curve.Evaluate(50));
		}

		[Fact]
		public void DeserializingACurve_ReplacesTheDefaultPoints_RatherThanAppendingToThem()
		{
			// Regression: Newtonsoft's default ObjectCreationHandling populates an already-initialized
			// collection, so a curve with constructor defaults would end up holding both the defaults
			// and the values read from disk. Prepare() collapses the duplicates, which hides the bug
			// behind correct-looking Evaluate() results, so assert on the point count directly.
			const string json = @"{
				""Interpolation"": ""Linear"",
				""Points"": [ { ""Players"": 0, ""Value"": 1 } ]
			}";

			var settings = JsonConvert.DeserializeObject<GroundItemCleanupSettings>(
				@"{ ""MaxItemAgeSeconds"": " + json + " }")!;

			Assert.Single(settings.MaxItemAgeSeconds.Points);
			Assert.Equal(1, settings.MaxItemAgeSeconds.Evaluate(0));
		}

		[Fact]
		public void ExemptIdLists_AreReplacedOnDeserialize_NotAppendedTo()
		{
			var settings = JsonConvert.DeserializeObject<ProjectileBudgetSettings>(
				@"{ ""ExemptProjectileIds"": [1, 2, 3] }")!;

			Assert.Equal(3, settings.ExemptProjectileIds.Count);
		}

		[Fact]
		public void HandAuthoredCurve_UsingModeNames_Deserializes()
		{
			const string json = @"{
				""Interpolation"": ""SmoothStep"",
				""Points"": [
					{ ""Players"": 0, ""Value"": 100 },
					{ ""Players"": 20, ""Value"": 10 }
				]
			}";

			var curve = JsonConvert.DeserializeObject<Curve>(json)!;

			Assert.Equal(InterpolationMode.SmoothStep, curve.Interpolation);
			Assert.Equal(100, curve.Evaluate(0));
			Assert.Equal(10, curve.Evaluate(20));
		}

		[Fact]
		public void ProjectileBudget_ShipsDisabledByDefault()
		{
			// The strategy most able to affect gameplay must be opt-in, not opt-out.
			Assert.False(new AntiLagSettings().ProjectileBudget.Enabled);
			Assert.True(new AntiLagSettings().GroundItemCleanup.Enabled);
			Assert.True(new AntiLagSettings().NpcSpawnThrottle.Enabled);
		}

		[Fact]
		public void ProjectileBudget_ExemptsPetsByDefault()
		{
			// Getting this wrong causes a client/server packet storm, so pin it.
			Assert.True(new AntiLagSettings().ProjectileBudget.ExemptPets);
		}

		[Fact]
		public void PartialConfig_LeavesUnspecifiedSectionsAtTheirDefaults()
		{
			// Mirrors an operator hand-editing only one section of AntiLag.json.
			const string json = @"{ ""GroundItemCleanup"": { ""MinItemAgeSeconds"": 5 } }";

			var settings = JsonConvert.DeserializeObject<AntiLagSettings>(json)!;

			Assert.Equal(5, settings.GroundItemCleanup.MinItemAgeSeconds);
			Assert.NotNull(settings.NpcSpawnThrottle);
			Assert.Equal(600, settings.NpcSpawnThrottle.SpawnRate.Evaluate(0));
		}
	}
}
