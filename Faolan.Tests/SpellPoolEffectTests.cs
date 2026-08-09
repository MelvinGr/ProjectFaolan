using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// #702: the INSTANT half of what a spell does - the pools it moves the moment it lands, as opposed to the lasting
	// stat deltas `BuffStatEffects` owns. Nothing applied these, which meant HEALING did not exist: the Necromancer's
	// whole `Lifestrike` line, `Aura of Infusion`, every health potion in the catalog - all of them showed an icon and
	// moved no bar.
	//
	// Pure over synthetic rows for the classification, plus two gates against the shipped catalog for the population.
	public class SpellPoolEffectTests
	{
		private const uint Hit = 53002, InterpolatedHit = 53219, HitPercentage = 53007, MagicHit = 53256;

		private static SpellStatEffect Row(uint spell, CharacterStatId stat, uint kind, int amount)
		{
			return new SpellStatEffect { SpellId = spell, StatId = stat, EffectKind = kind, Amount = amount };
		}

		// The three CURRENT pools are stored at the ×100 wire scale - which the client's own divisor table confirms
		// for all three ids - so `Health Potion Effect 10`'s +40500 is 405 HP, not 40,500.
		[Fact]
		public void AFlatEffect_IsTheWireValueAtTheVitalsScale()
		{
			var catalog = SpellPoolEffects.BuildCatalog([Row(1, CharacterStatId.HealthCurrent, Hit, 40500)]);

			var effect = Assert.Single(catalog[1]);
			Assert.Equal(CharacterStatId.HealthCurrent, effect.Pool);
			Assert.Equal(405, effect.For(2000));
		}

		// ⭐ `HitPercentage` states its own unit through a carrier's NAME: the dev spell
		// `System Spell: Fire AOE damage 25% of Life` carries -25. So its Amount is a plain percent OF THE POOL, not
		// a value at the stat's display scale - which is what tells the two kinds apart without a fit.
		[Fact]
		public void APercentEffect_IsAPlainPercentOfThePool()
		{
			var catalog = SpellPoolEffects.BuildCatalog([Row(2, CharacterStatId.HealthCurrent, HitPercentage, -25)]);

			var effect = Assert.Single(catalog[2]);
			Assert.True(effect.IsPercent);
			Assert.Equal(-500, effect.For(2000)); // 25% of a 2000 pool
		}

		// ⚠️ The drain SENTINEL is a marker, not a number - `Demonic Puppet` carries -100000000 on health, which
		// applied literally would kill anything it touched.
		[Fact]
		public void TheDrainSentinel_IsNotAnAmount()
		{
			Assert.Empty(SpellPoolEffects.BuildCatalog([Row(3, CharacterStatId.HealthCurrent, Hit, -100_000_000)]));
		}

		// The MAX pools are a different mechanic and stay out - a `HealthMax` row is a max-health change, not a heal.
		[Fact]
		public void MaxPoolRows_AreNotInstantEffects()
		{
			Assert.Empty(SpellPoolEffects.BuildCatalog([
				Row(4, CharacterStatId.HealthMax, Hit, -27968),
				Row(5, CharacterStatId.ManaMax, Hit, 100)
			]));
		}

		// ⚠️ `MagicHit` carries NO stat key (`[39 Value, 71]`), so it can never name a pool - and it is out of the
		// kind set for that reason rather than by judgement. It used to reach this table under stat 1 `HealthMax`
		// with the `Venomstrike`/`Banestrike` ladders, which read as a max-health drain; that "stat" was key 39's own
		// value, read positionally (#701). Nothing carries it now, and nothing should.
		[Fact]
		public void MagicHit_CarriesNoStatAndIsNotAPoolEffect()
		{
			Assert.Empty(SpellPoolEffects.BuildCatalog([Row(6, CharacterStatId.HealthCurrent, MagicHit, -1567)]));
			Assert.Equal(0u, Scalar("SELECT COUNT(*) FROM SpellStatEffects WHERE EffectKind = 53256"));
		}

		// A LASTING stat delta is the buff engine's, not this one's - the two families live in the same table and are
		// told apart only by the kind, so a parser that keyed off the stat would apply an armour buff as a heal.
		[Fact]
		public void LastingStatDeltas_AreNotInstantEffects()
		{
			Assert.Empty(SpellPoolEffects.BuildCatalog([
				Row(6, CharacterStatId.Armor, 53045, 1000), // Fend: +1000 Armor for a duration
				Row(7, CharacterStatId.CriticalRating, 53067, 200) // Philtre of Guile: +2% crit
			]));
		}

		// All three pools, so a mana restore and a stamina refill are not quietly dropped for being less obvious than
		// a heal - `Adrenaline Surge` restores mana and `Formation of Resolve` stamina.
		[Fact]
		public void AllThreePools_AreCarried()
		{
			var catalog = SpellPoolEffects.BuildCatalog([
				Row(8, CharacterStatId.HealthCurrent, InterpolatedHit, 3400), // Lifestrike (Rank 1)
				Row(9, CharacterStatId.ManaCurrent, InterpolatedHit, 14941), // Adrenaline Surge
				Row(10, CharacterStatId.StaminaCurrent, InterpolatedHit, 2967) // Formation of Resolve
			]);

			Assert.Equal(3, catalog.Count);
			Assert.Equal(CharacterStatId.ManaCurrent, catalog[9][0].Pool);
			Assert.Equal(CharacterStatId.StaminaCurrent, catalog[10][0].Pool);
		}

		// The shipped catalog: the population this is for. A heal that stops being imported would otherwise fail
		// nothing, because the classification above is happy with zero rows.
		[Fact]
		public void TheShippedCatalog_CarriesTheHealsThisIsFor()
		{
			// Lifestrike's five ranks, the Necromancer's own sustain line - 34 HP at rank 1 up to 337 at rank 5.
			var ranks = Query("SELECT s.Name, e.Amount FROM SpellStatEffects e JOIN Spells s ON s.Id = e.SpellId " +
				"WHERE s.Name LIKE 'Lifestrike (Rank%' AND e.EffectKind = 53219 AND e.StatId = 27 ORDER BY e.Amount");

			Assert.Equal(5, ranks.Count);
			Assert.Equal(3400, ranks[0].Amount);
			Assert.Equal(33700, ranks[4].Amount);
		}

		// ⭐ A potion heals TWICE - instantly, and then over time - and the record says so in two `Hit` elements on the
		// SAME stat, told apart by the element's 0x2d6 period (the maintainer's reading, confirmed 2026-08-10):
		//     0x2d5=1,  0x2d6=0    27 = 40500     405 HP, at once
		//     0x2d5=-1, 0x2d6=100  27 = 6750      67.5 HP every second, for the row's 60 s duration
		// The spell row corroborates independently: DurationMs 60000, DotIntervalMs 1000, DotDelta 6750.
		//
		// ⚠️ This used to assert a SINGLE row at 40500, and that held only by accident: the element scan could see one
		// of the two (#772) and the importer keyed repeats on the stat id alone, so one silently overwrote the other.
		// When the scan was fixed the surviving value flipped to 6750 - the potion's heal changing by 6x for no reason
		// a player could observe. Both rows are kept now (the key carries the period), so this asserts the MECHANIC.
		[Fact]
		public void TheShippedCatalog_CarriesAHealthPotionThatHealsAtOnceAndOverTime()
		{
			var instant = Query("SELECT s.Name, e.Amount FROM Items i JOIN Spells s ON s.Id = i.OnUseSpell " +
				"JOIN SpellStatEffects e ON e.SpellId = s.Id WHERE i.Name = 'Daggamalt' AND e.StatId = 27 AND e.PeriodCentiseconds = 0");
			var overTime = Query("SELECT s.Name, e.Amount FROM Items i JOIN Spells s ON s.Id = i.OnUseSpell " +
				"JOIN SpellStatEffects e ON e.SpellId = s.Id WHERE i.Name = 'Daggamalt' AND e.StatId = 27 AND e.PeriodCentiseconds = 100");

			Assert.Equal(40500, Assert.Single(instant).Amount); // 405 HP at the vitals scale, immediately
			Assert.Equal(6750, Assert.Single(overTime).Amount); // 67.5 HP a second thereafter
		}

		// ⭐ The invariant the key-addressed read establishes (#701): every stored row's KIND carries a stat key, so
		// its `StatId` column is a stat rather than whatever field happened to sit in payload word 0. 1,191 rows
		// failed this before - `StartParticleEffect` filing a string's BYTE LENGTH as a stat id, `NanoResistance`
		// filing key 152, `SetSpellLineRecastTime` filing a spell LINE.
		[Fact]
		public void EveryStoredRowsKind_ActuallyCarriesAStat()
		{
			var kindsWithoutAStat = new[] { 53238u, 53239u, 53162u, 53189u, 53097u, 53077u, 53043u, 53256u, 53010u, 53105u };
			var offenders = Scalar($"SELECT COUNT(*) FROM SpellStatEffects WHERE EffectKind IN ({string.Join(",", kindsWithoutAStat)})");

			Assert.Equal(0u, offenders);
		}

		private static uint Scalar(string sql)
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;

			return (uint)(long)(cmd.ExecuteScalar() ?? 0L);
		}

		private static List<(string Name, int Amount)> Query(string sql)
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;
			using var r = cmd.ExecuteReader();
			var rows = new List<(string, int)>();
			while (r.Read())
				rows.Add((r.GetString(0), (int)r.GetInt64(1)));

			return rows;
		}
	}
}
