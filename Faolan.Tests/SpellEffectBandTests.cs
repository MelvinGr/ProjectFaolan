using System.Collections.Generic;
using System.Linq;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// A spell record states one element per level band, and something has to choose between them. These gate the
	// choice and the shipped rows it is made over - the rule is only worth anything if the bands are really there.
	public class SpellEffectBandTests
	{
		// A banded set: exactly the member whose band contains the level.
		[Fact]
		public void ABandedSet_ResolvesToTheBandCoveringTheLevel()
		{
			var bands = Bands((1, 5), (6, 10), (11, 15), (16, 20), (21, 30));

			Assert.Equal([(1u, 5u)], SpellEffectBands.ForLevel(bands, 3, b => b));
			Assert.Equal([(11u, 15u)], SpellEffectBands.ForLevel(bands, 11, b => b));
			Assert.Equal([(21u, 30u)], SpellEffectBands.ForLevel(bands, 30, b => b));
		}

		// Clamped, not dropped. `Hellfire Stream`'s bands stop at 30 and the spell is castable far past that; a rule
		// that required coverage would make it do nothing at exactly the levels it is most often cast at. The client's
		// own ramp arithmetic clamps outside the band for the same reason.
		[Fact]
		public void ALevelOutsideEveryBand_TakesTheNearestOne()
		{
			var bands = Bands((1, 5), (6, 10), (21, 30));

			Assert.Equal([(21u, 30u)], SpellEffectBands.ForLevel(bands, 80, b => b));
			Assert.Equal([(1u, 5u)], SpellEffectBands.ForLevel(bands, 0, b => b));

			// A level in the gap between two bands takes whichever is closer, and a tie keeps both rather than
			// inventing a preference.
			Assert.Equal([(6u, 10u)], SpellEffectBands.ForLevel(bands, 12, b => b));
			Assert.Equal([(6u, 10u), (20u, 30u)], SpellEffectBands.ForLevel(Bands((6, 10), (20, 30)), 15, b => b));
		}

		// The other shape the same rule has to get right: `Life Leech`'s effect record states five elements on one
		// stat that all share one band - four heals and a drain. Every one of them covers the level, so every one of
		// them applies. Picking "the row for this level" here would throw three quarters of the heal away.
		[Fact]
		public void MembersSharingOneBand_AllApply()
		{
			var repeats = Bands((1, 120), (1, 120), (1, 120), (1, 120), (1, 120));

			Assert.Equal(5, SpellEffectBands.ForLevel(repeats, 40, b => b).Count);
		}

		// An unbanded member is level-independent and rides along with whatever band was chosen - a record mixing a
		// banded damage element with a flat `Modify` states both, and the flat one is not an alternative reading of
		// the banded one.
		[Fact]
		public void UnbandedMembers_AlwaysApply()
		{
			List<(uint Low, uint High)?> mixed = [(1, 5), (6, 10), null];

			Assert.Equal([(1u, 5u), null], SpellEffectBands.ForLevel(mixed, 3, b => b));
			Assert.Equal([null], SpellEffectBands.ForLevel<(uint Low, uint High)?>([null], 3, b => b));
		}

		// The shipped rows the rule exists for. `Hellfire Stream` (8600) carries five `InterpolatedHit` elements on
		// (stat 27, period 0), one per band - a set that collapses to a single row reading -488 for every caster of
		// every level if the bands are ignored.
		[Fact]
		public void TheShippedCatalog_CarriesHellfireStreamsFiveLevelBands()
		{
			var rows = Rows("SELECT LowLevel, HighLevel, Amount, MaxAmount FROM SpellStatEffects " +
			                "WHERE SpellId = 8600 AND EffectKind = 53219 AND StatId = 27 ORDER BY LowLevel");

			// Two of the five are stated twice by the record (body offsets 1106 and 1194 repeat 390 and 478), so the
			// row set is seven and the distinct set is five - see SpellStatEffect.Statement.
			Assert.Equal(5, rows.Select(r => (r.Low, r.High)).Distinct().Count());
			Assert.Equal([(1u, 5u), (6u, 10u), (11u, 15u), (16u, 20u), (21u, 30u)], rows.Select(r => (r.Low, r.High)).Distinct());

			// …and the level-1 band really is a different number from the level-21 one, which is the whole point.
			Assert.Equal(-414, rows.First(r => r.Low == 1).Amount);
			Assert.Equal(-691, rows.First(r => r.Low == 1).Max);
			Assert.Equal(-926, rows.First(r => r.Low == 21).Amount);
			Assert.Equal(-1544, rows.First(r => r.Low == 21).Max);
		}

		private static List<(uint Low, uint High)?> Bands(params (uint Low, uint High)[] bands)
		{
			return [.. bands.Select(b => (( uint Low, uint High )?)b)];
		}

		private static List<(uint Low, uint High, int Amount, int Max)> Rows(string sql)
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;
			using var r = cmd.ExecuteReader();
			var rows = new List<(uint, uint, int, int)>();
			while (r.Read())
				rows.Add(((uint)r.GetInt64(0), (uint)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3)));

			return rows;
		}
	}
}
