using System.Collections.Generic;
using System.Linq;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// A combo's name states what it does. The client's catalog names a player combo
	// `<Class>_<level>[_<expansion>]_<category>_<Name>`, and the category token is a small closed vocabulary -
	// `knockback` (30), `snare` (13), `stun` (6), `manaburn` (7), `armorbreak` (7), the taunt and dot families.
	//
	// It matters because a combo's own spell says nothing: every stagger combo resolves to a `Stagger I..V` /
	// `Staggering Blow I..IV` record with a zero duration and no stat effects, so the name is the only place the
	// effect family is stated at all.
	public class ComboEffectTests
	{
		// The six classes whose combos knock back, by their real names.
		[Theory]
		[InlineData("Guardian_20b_knockback_Shield_Sweep_II")]
		[InlineData("Barbarian_15_knockback_Clobber_I")]
		[InlineData("BearShaman_76_knockback_Ferocious_Smack_IV")]
		[InlineData("Conqueror_17_knockback_Batter_Aside_I")]
		[InlineData("Halberdier_15_knockback_Stagger_I")]
		[InlineData("Ranger_80c_knockback_Volley_Backwards")]
		public void AKnockbackComboIsRecognisedByItsName(string name)
		{
			Assert.True(ComboEffect.IsKnockback(name));
		}

		// The category is not at a fixed position: the Khitai combos carry an expansion token that pushes it one
		// slot along. Indexing `parts[2]` would read `khitai` and miss the knockback entirely.
		[Fact]
		public void AnExpansionTokenDoesNotHideTheCategory()
		{
			Assert.True(ComboEffect.IsKnockback("Halberdier_80_khitai_knockback_Stagger_V"));
			Assert.Equal("knockback", ComboEffect.Category("Halberdier_80_khitai_knockback_Stagger_V"));
			Assert.Equal("knockback", ComboEffect.Category("Halberdier_15_knockback_Stagger_I"));
		}

		// A combo that is not one must not be swept up - `evadedebuff` sits in the same slot on the Barbarian's
		// `Staggering Blow`, whose name contains "Stagger" and which is emphatically not a knockback.
		[Theory]
		[InlineData("Barbarian_17_evadedebuff_Staggering_Blow_I")]
		[InlineData("Assassin_20b_dot_Grim_Corruption_II")]
		[InlineData("2h_Sword_Wheel_of_Death")]
		[InlineData("combo_npc_guardian_polearm_1_sweep")]
		[InlineData(null)]
		public void EverythingElseIsNotAKnockback(string? name)
		{
			Assert.False(ComboEffect.IsKnockback(name));
		}

		// The weapon specials and the NPC rows are a different grammar entirely, so they have no category rather
		// than a wrong one.
		[Theory]
		[InlineData("2h_Sword_Flashing_Steel")]
		[InlineData("Oar_Special")]
		[InlineData("combo_npc_generic_polearm")]
		public void ADifferentGrammarHasNoCategory(string name)
		{
			Assert.Null(ComboEffect.Category(name));
		}

		// The shipped catalog: the population this is for. The classifier above is happy with zero rows, so without
		// this a catalog that stopped naming its combos would fail nothing.
		[Fact]
		public void TheShippedCatalog_CarriesTheKnockbackCombos()
		{
			var names = Query("SELECT Name FROM Combos");
			var knockbacks = names.Where(ComboEffect.IsKnockback).ToList();

			// 30, and the five it would otherwise miss are the point: a census that read `parts[2]` finds 25 and
			// drops the rank-V Khitai combo of five of the six ladders - the top ability of each. This gate exists
			// because 25 is the count I first measured, by exactly that mistake.
			Assert.Equal(30, knockbacks.Count);
			Assert.Contains(knockbacks, n => n.Contains("Shield_Sweep"));
			Assert.Contains(knockbacks, n => n.Contains("Batter_Aside"));
			Assert.Equal(5, knockbacks.Count(n => n.Contains("khitai")));

			// …and it is a slice, not the whole catalog: most combos are not knockbacks.
			Assert.True(knockbacks.Count < names.Count / 10, $"{knockbacks.Count} of {names.Count} combos read as knockbacks - the token match is too loose");
		}

		private static List<string> Query(string sql)
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;
			using var r = cmd.ExecuteReader();
			var rows = new List<string>();
			while (r.Read())
				rows.Add(r.GetString(0));

			return rows;
		}
	}
}
