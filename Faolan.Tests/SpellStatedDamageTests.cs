using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// #395: a cast's damage is the SPELL's, not the level's.
	//
	// `CombatResolver.ResolveSpellDamage` was `20 + level*5` and ignored its `spellId` outright, so every spell a
	// character owned hit for the same number and the only thing that could ever separate two of them was the
	// caster's gear. The per-spell power was not missing from the data — it is the health magnitude the spell's own
	// RDB template states, which the client renders verbatim in its tooltip: `Ice Strike (Rank 6)` reads
	// *"163-189.5 points of Health"*, and the imported row is `-16300..-18950` on stat 27. 2,149 shipped spells
	// carry one.
	//
	// ⚠️ These read the SHIPPED catalog on purpose. The arithmetic below is trivial and would pass on synthetic
	// rows whether or not a single real spell states a hit — which is the state this ticket was in.
	public class SpellStatedDamageTests
	{
		private const uint InterpolatedHit = 53219;

		// The two ranks the ticket is named against, straight out of `faolan.db`, each read at the LOW end of its own
		// level ramp: rank 2 states 43-50 and rank 6 states 163-189. Under the old resolver a level-80 caster dealt
		// 420 with either. The rank-2 row also ramps to 71-82 by level 34, which is what makes the level argument
		// real rather than decoration.
		[Theory]
		[InlineData(144307u, 20u, 43, 50)] // Ice Strike (Rank 2) - ramp 20..34
		[InlineData(144307u, 34u, 71, 82)] // …the same row at the top of its ramp
		[InlineData(144315u, 80u, 163, 189)] // Ice Strike (Rank 6) - ramp 80..100, the "163-189.5" the tooltip renders
		public void ASpellsOwnStatedHit_IsItsDamageRange(uint spellId, uint level, int low, int high)
		{
			var effect = Assert.Single(Catalog(spellId));

			Assert.Equal(low, -effect.For(0, level));
			Assert.Equal(high, -effect.MaxFor(0, level));
		}

		// ⭐ The point of the whole ticket, stated as the comparison the old code could not make: two ranks of ONE
		// spell, at ONE caster level, must not deal the same damage.
		[Fact]
		public void TwoRanksOfOneSpell_DoNotDealTheSameDamage()
		{
			var rank2 = Damage(144307u, 80);
			var rank6 = Damage(144315u, 80);

			Assert.True(rank6 > rank2 * 2, $"rank 6 ({rank6}) should far outhit rank 2 ({rank2})");

			// …and neither is the placeholder, which is what the old resolver answered for both.
			var placeholder = CombatResolver.ResolveSpellDamage(0, 80);
			Assert.NotEqual(placeholder, rank2);
			Assert.NotEqual(placeholder, rank6);
		}

		// The roll runs the stated range end to end, the way a weapon's min..max does in `ResolveMeleeDamage` — so a
		// cast is a number drawn from the spell's range rather than either endpoint pretending to be the whole thing.
		[Fact]
		public void TheRoll_RunsTheStatedRangeEndToEnd()
		{
			var effect = Assert.Single(Catalog(144315u));

			Assert.Equal(effect.For(0, 80), effect.Rolled(0, 80, 0));
			Assert.Equal(effect.MaxFor(0, 80), effect.Rolled(0, 80, 100));

			var mid = effect.Rolled(0, 80, 50);
			Assert.InRange(mid, effect.MaxFor(0, 80), effect.For(0, 80)); // negative: max is the LOWER bound
		}

		// ⚠️ The placeholder stays for a spell that states nothing, and that is most CASTS in practice — an ability
		// with no template row, an NPC cast. A stated hit REPLACES the level curve; it does not add to it.
		[Fact]
		public void ASpellThatStatesNothing_StillFallsToTheLevelPlaceholder()
		{
			Assert.Equal(20 + 80 * 5, CombatResolver.ResolveSpellDamage(0, 80));
			Assert.Equal(163, CombatResolver.ResolveSpellDamage(0, 80, 0, 0, 163));
		}

		// Magic Damage (#399) still adds flat on top of the stated hit, and the spell-line feat boost (#590) still
		// scales the finished number — the stated hit slots into the base term, it does not bypass the rest.
		[Fact]
		public void MagicDamageAndTheFeatBoost_StillApplyOverAStatedHit()
		{
			Assert.Equal(163 + 40, CombatResolver.ResolveSpellDamage(0, 80, 40, 0, 163));
			Assert.Equal((163 + 40) * 135 / 100, CombatResolver.ResolveSpellDamage(0, 80, 40, 3500, 163));
		}

		// ⚠️ A PERIODIC row is the DoT engine's, not this one's, and 726 shipped spells state BOTH — every one of
		// them was having its ticks applied as extra instant magnitude the moment it landed. `Flesh to Worms (Rank 2)`
		// is one: one instant element and two on a 2 s period, all three the same magnitude.
		[Fact]
		public void PeriodicRowsAreNotInstantEffects()
		{
			Assert.Equal(3u, Scalar("SELECT COUNT(*) FROM SpellStatEffects WHERE SpellId = 144674 AND StatId = 27"));
			Assert.Single(Catalog(144674u)); // …but only the instant one reaches the instant engine

			Assert.Empty(SpellPoolEffects.BuildCatalog([
				new SpellStatEffect
				{
					SpellId = 1,
					StatId = CharacterStatId.HealthCurrent,
					EffectKind = InterpolatedHit,
					Amount = 6750,
					PeriodCentiseconds = 100
				}
			]));
		}

		// The POPULATION, so a regression in the import cannot leave every one of the assertions above passing over
		// an empty catalog while the live server is back on the flat placeholder for everything.
		[Fact]
		public void TheShippedCatalog_StatesADamagingHitForThousandsOfSpells()
		{
			Assert.True(Scalar("SELECT COUNT(DISTINCT SpellId) FROM SpellStatEffects WHERE StatId = 27 AND Amount < 0") > 2000,
				"the damaging health rows are missing - they were 2,149 spells when #395 wired them to the cast path");
		}

		private static IReadOnlyList<SpellPoolEffects.PoolEffect> Catalog(uint spellId)
		{
			var catalog = SpellPoolEffects.BuildCatalog(RowsFor(spellId));

			return catalog.TryGetValue(spellId, out var effects) ? effects : [];
		}

		// The damage the cast path uses at `level`, mid-roll - through `CombatService.StatedHit` itself over the
		// shipped catalog, not a restatement of it, so a change to the rule fails here rather than passing twice.
		private static int Damage(uint spellId, uint level)
		{
			return CombatResolver.ResolveSpellDamage(spellId, (ushort)level, 0, 0, CombatService.StatedHit(Catalog(spellId), level, 50));
		}

		private static SpellStatEffect[] RowsFor(uint spellId)
		{
			using var connection = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT SpellId, Sequence, StatId, EffectKind, Amount, MaxAmount, HighAmount, MaxHighAmount, LowLevel, HighLevel, PeriodCentiseconds " +
			                      "FROM SpellStatEffects WHERE SpellId = $id";
			command.Parameters.AddWithValue("$id", spellId);

			var rows = new List<SpellStatEffect>();
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				rows.Add(new SpellStatEffect
				{
					SpellId = (uint)reader.GetInt64(0),
					Sequence = (uint)reader.GetInt64(1),
					StatId = (CharacterStatId)reader.GetInt64(2),
					EffectKind = (uint)reader.GetInt64(3),
					Amount = reader.GetInt32(4),
					MaxAmount = reader.IsDBNull(5) ? null : reader.GetInt32(5),
					HighAmount = reader.IsDBNull(6) ? null : reader.GetInt32(6),
					MaxHighAmount = reader.IsDBNull(7) ? null : reader.GetInt32(7),
					LowLevel = reader.IsDBNull(8) ? null : (uint)reader.GetInt64(8),
					HighLevel = reader.IsDBNull(9) ? null : (uint)reader.GetInt64(9),
					PeriodCentiseconds = (uint)reader.GetInt64(10)
				});
			}

			return [.. rows];
		}

		private static uint Scalar(string sql)
		{
			using var connection = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = sql;

			return (uint)(long)command.ExecuteScalar()!;
		}
	}
}
