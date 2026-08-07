using System.Collections.Generic;
using Faolan.Core.Rdb;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// #349: the two numbers a ground-targeted AoE needs - how wide it blasts and how often it ticks - read off the
	// client instead of guessed. Both are shipped data, so this gates the committed faolan.db.
	//
	// The radius is NOT a property field, which is why three sweeps of the field run found none: it is the `Radius`
	// key (11) of the spell's area-TARGETING GameAction element, one region over. The interval IS a property field
	// (447 `ChannelingInterval`).
	public class SpellAreaAndChannelTests
	{
		// The single strongest check available on the channel interval, and it is an independent one: DotIntervalMs
		// was MEASURED from the gaps between captured StatDamageIIR ticks, months before field 447 was read. Where a
		// spell has both, they agree exactly - and one of the three is Deathly Blizzard, the spell #349 is about.
		[Fact]
		public void ChannelingInterval_AgreesWithEveryIntervalMeasuredOffTheWire()
		{
			var rows = Query("SELECT m.Name, m.DotIntervalMs, c.ChannelingIntervalMs FROM Spells m " +
				"JOIN Spells c ON lower(c.Name) = lower(m.Name) AND c.ChannelingIntervalMs > 0 WHERE m.DotIntervalMs > 0");

			// Three overlaps today. Assert the count so a widened corpus that ADDS one cannot quietly stop being
			// checked, and assert the values so a disagreement fails rather than averages.
			Assert.Equal(3, rows.Count);
			foreach (var (name, measured, stated) in rows)
				Assert.True(measured == stated, $"{name}: measured {measured} ms off the wire, client states {stated} ms");
		}

		[Fact]
		public void DeathlyBlizzard_TicksAtTheClientsTwoAndAHalfSeconds()
		{
			// 144526 is the CAST record (the one carrying range + cast time); 144432 is its effect twin, which is
			// where the measured DoT interval landed. The channel interval rides the cast record.
			Assert.Equal(2500u, Scalar("SELECT ChannelingIntervalMs FROM Spells WHERE Id = 144526"));
		}

		// Waves of Flame is a cone: `ConeFormula 88=<effect spell> 212=-45 213=90 11=5`. The radius column is the
		// reach; the two angles are not modelled yet, so a cone currently blasts as a ring of the same radius.
		[Theory, InlineData(1136242u, 5f), InlineData(1136253u, 6f), InlineData(144050u, 5f)]
		public void AreaRadius_IsLiftedFromTheAreaTargetingElement(uint spellId, float expected)
			=> Assert.Equal(expected, (float)Scalar($"SELECT CAST(AreaRadius * 100 AS INTEGER) FROM Spells WHERE Id = {spellId}") / 100f);

		// Deathly Blizzard states a channel interval and NO area. That is a real answer, not a gap in the import:
		// its blast is server-side, so the ground-AoE path falls back to CombatService.GroundAoeRadiusUnits.
		[Fact]
		public void AreaRadius_IsAbsentForASpellWhoseAreaIsServerSide()
			=> Assert.Equal(0u, Scalar("SELECT CAST(AreaRadius AS INTEGER) FROM Spells WHERE Id = 144526"));

		[Fact]
		public void AreaRadius_IsCarriedByOverAThousandSpells_AndNoneAtAnAbsurdWidth()
		{
			// A sanity bound rather than an exact count (the catalog grows): the radius is METRES, so a four-digit
			// one would mean the unit was misread - the splash triple's radius, by contrast, is metres x100.
			Assert.True(Scalar("SELECT COUNT(*) FROM Spells WHERE AreaRadius > 0") > 1000);
			Assert.Equal(0u, Scalar("SELECT COUNT(*) FROM Spells WHERE AreaRadius > 500"));
		}

		// The set is a deliberate SUBSET of the 17 types carrying a Radius key - a radius is only a blast radius
		// when the element is selecting targets. Locks that intent so an audio falloff or a radius MODIFIER cannot
		// be folded in later without the choice being made again.
		[Fact]
		public void AreaTargetKinds_AreTheTargetSelectorsOnly()
		{
			Assert.Contains(53087u, GameActionTypes.AreaTargetKinds); // AreaFormula
			Assert.Contains(53259u, GameActionTypes.AreaTargetKinds); // ConeFormula
			Assert.DoesNotContain(53011u, GameActionTypes.AreaTargetKinds); // AudioEffect - an audio falloff
			Assert.DoesNotContain(53032u, GameActionTypes.AreaTargetKinds); // AOERadiusModification - adjusts someone else's
			Assert.DoesNotContain(53092u, GameActionTypes.AreaTargetKinds); // SetTradingPostAccess - a facility range

			// Every one of them really does carry the key it is selected for.
			foreach (var kind in GameActionTypes.AreaTargetKinds)
				Assert.Contains(GameActionTypes.KeyRadius, GameActionTypes.Get(kind)!.Keys);
		}

		// #633: the per-spell CRIT columns, against the shipped catalog. The point of gating them is that the data
		// makes a claim the code had been making on its own authority - see the modal-value assertion below.
		[Fact]
		public void SpellCritFields_AreImportedForTheSpellsThatCarryThem()
		{
			Assert.Equal(1219u, Scalar("SELECT COUNT(*) FROM Spells WHERE CriticalChanceHundredths > 0"));
			Assert.Equal(1229u, Scalar("SELECT COUNT(*) FROM Spells WHERE CriticalDamageHundredths > 0"));

			// Two of the Demonologist's own nukes, at the modal 2.50% chance for +50% damage.
			Assert.Equal(250u, Scalar("SELECT CriticalChanceHundredths FROM Spells WHERE Name = 'Shockstrike (Rank 3)'"));
			Assert.Equal(5000u, Scalar("SELECT CriticalDamageHundredths FROM Spells WHERE Name = 'Shockstrike (Rank 3)'"));
		}

		// ⭐ The independent corroboration. `CombatResolver.BaseCritDamagePercent` is 150 - a crit hits for 1.5x -
		// and that file says outright that the constant is provisional and "waiting on an in-client combat log".
		// The client's own spell table states +50.00% on 607 of the 1,228 records that state anything, which is the
		// same number arrived at from a different direction entirely.
		[Fact]
		public void TheModalSpellCritDamage_IsTheBaseCritThisServerAlreadyAssumed()
		{
			var modal = Scalar("SELECT CriticalDamageHundredths FROM Spells WHERE CriticalDamageHundredths > 0 " +
				"GROUP BY CriticalDamageHundredths ORDER BY COUNT(*) DESC LIMIT 1");

			Assert.Equal(5000u, modal);
			Assert.Equal((uint)(CombatResolver.BaseCritDamagePercent - 100) * 100, modal);
		}

		private static uint Scalar(string sql)
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;
			return (uint)(long)(cmd.ExecuteScalar() ?? 0L);
		}

		private static List<(string Name, uint Measured, uint Stated)> Query(string sql)
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;
			using var r = cmd.ExecuteReader();
			var rows = new List<(string, uint, uint)>();
			while (r.Read())
				rows.Add((r.GetString(0), (uint)r.GetInt64(1), (uint)r.GetInt64(2)));
			return rows;
		}
	}
}
