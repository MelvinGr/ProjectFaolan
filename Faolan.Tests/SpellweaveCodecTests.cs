using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the spellweaving activate-response + fire records vs the captured bytes in
	// NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (docs/subsystems/combos.md,
	// tools/capture/decode_spellweave.py). caster = 0x0802E5D4 throughout; these are the record bytes AFTER the 0x2000
	// carrier's u32 length prefix (the golden hex is the frame body with that prefix stripped).
	public class SpellweaveCodecTests
	{
		// The universal ENERGY (yellow sprint-bar) maxima retail pushes on a FULL bar (0x200E body [u32 energy][u8 01]).
		// Locked because both were wrong by one observation until 2026-08-04: 8910 is 9000 minus a single 90-point weave
		// drain tick, read off the first packet of a weave rather than an idle full push, and 13984 is 14000 minus 16.
		// The round numbers ARE the evidence - across the corpus a capture that never weaves carries exactly one
		// distinct value for the whole session, and it is 9000 or 14000.
		//
		// ⚠️ Re-deriving these from a dump needs the BODY LENGTH as well as the opcode: `0x200E` is the lossy wire
		// rendering of a hash-registered method (#615), so filtering on the dump's `Opcode` alone also catches
		// SimpleCharFullUpdate bodies and reads a "max" of 57001 out of the bytes of a character name.
		[Theory]
		[InlineData(ConanClass.Necromancer, 9000u)] // 4 captures, 3 characters
		[InlineData(ConanClass.Demonologist, 9000u)] // DemoL80_mage-demonologist_login_randomActions-very_long_loading_screen
		[InlineData(ConanClass.PriestOfMitra, 9000u)] // unobserved - grouped with the casters
		[InlineData(ConanClass.TempestOfSet, 9000u)] // unobserved - grouped with the casters
		[InlineData(ConanClass.HeraldOfXotli, 14000u)] // 3 captures, 2 characters (levels 24 and 80 - it does not scale)
		[InlineData(ConanClass.Assassin, 14000u)] // Strie_rogue-assassin_login_3 / _randomActions
		[InlineData(ConanClass.Barbarian, 14000u)] // unobserved - the non-caster default
		public void EnergyMaxForClass_MatchesTheCapturedFullBar(ConanClass characterClass, uint expected)
		{
			Assert.Equal(expected, SpellweaveCatalog.EnergyMaxForClass(characterClass));
		}

		// ⚠️ These four records are NOT part of the weave (#685) - they were modelled as its "fire stages" because this
		// capture happens to hold them near a deactivate. Across the corpus they appear in 31 captures and 20 of those
		// contain no spellweaving at all, a Herald of Xotli among them, which cannot weave. They are the level-scaled
		// rating quad, and the capture's "constant" 0x7BD4 = 31,700 is this level-80 necromancer's own value.
		//
		// The bytes stay as a golden because they are real retail bytes - but built the way they are now DERIVED, so
		// this doubles as the end-to-end proof that `400 x level - 300` reproduces what retail sent.
		[Theory, InlineData(CharacterStatId.AttackRating, "f98e10b30000c3500802e5d4010000039800007bd4"),
		 InlineData(CharacterStatId.MagicRating, "f98e10b30000c3500802e5d4010000039b00007bd4"),
		 InlineData(CharacterStatId.DefenseRating, "f98e10b30000c3500802e5d4010000039a00007bd4"),
		 InlineData(CharacterStatId.MagicalResist, "f98e10b30000c3500802e5d4010000039d00007bd4")]
		// frames #1843-#1846
		public void BuildRatingQuad_AtLevel80_MatchesCapture(CharacterStatId stat, string expected)
		{
			Assert.Contains(stat, CombatRatingCurve.Stats);

			var bytes = StatCodec.Build(StatIirs.ForCharacter(0x0802E5D4, (uint)stat, CombatRatingCurve.For(80)));
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_ConsumedElementZeroed_MatchesCapture()
		{
			// frame#1774: the per-add re-send after weaving element[1] (Arcane Surge) - that slot is zeroed.
			var bytes = CombatReactiveCodec.Build(0x0802E5D4, [0x396AE9, 0x000000, 0x37E6D9, 0x396A22, 0x396A3D]);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9000000000037e6d900396a2200396a3d", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_Necro_MatchesCapture()
		{
			// frame#1647: the activate-response element list - all 5 necro weave elements available.
			var elements = SpellweaveCatalog.ForClass(ConanClass.Necromancer)!.WeaveElements;
			var bytes = CombatReactiveCodec.Build(0x0802E5D4, elements);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9003969d80037e6d900396a2200396a3d", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildStatGauge_HealthDot_MatchesCapture()
		{
			// the "Spellweaving" health DoT gauge that hits when stamina empties (token 602395, delta -31705, scale 0x1B).
			var bytes = StatDamageCodec.Build(StatDamages.SpellweaveGauge(0x0802E5D4, 602395, -31705, StatDamages.SpellweaveDotGauge, StatDamages.SpellweaveDotScale));
			Assert.Equal("e9f241840000c3500802e5d4000009311b0000000000000000000000000000001b00000002ffff8427000000000000c3500802e5d400000001000000000037e69000000050000000000000000000000000",
				Convert.ToHexStringLower(bytes));
		}

		// The spellweave toggle selector is PER CLASS (docs/subsystems/player-actions.md): necromancer 0xFC,
		// demonologist 0x10C. It used to be a single 0xFC constant, which meant a demonologist could never weave -
		// their 0x10C fell through the handler's switch and was only logged.
		//
		// The other half of this is what must NOT be in the map. 0x205A is the generic player-action channel, so a
		// selector wrongly mapped here starts a trance on an unrelated keypress - which has happened: routing every
		// 0x205A to spellweave made 0xFF (open the crafting recipe book) begin a weave on a necromancer. 0xFF and the
		// four melee combo inputs are the selectors that regression is made of, so they are asserted absent.
		[Fact]
		public void SpellweaveToggleAction_IsPerClass_AndClaimsNoOtherSelector()
		{
			Assert.Equal(0xFCu, StartingData.SpellweaveToggleAction[ConanClass.Necromancer]);
			Assert.Equal(0x10Cu, StartingData.SpellweaveToggleAction[ConanClass.Demonologist]);

			// A shared id would silently re-create the original bug for one of the two classes.
			Assert.Equal(StartingData.SpellweaveToggleAction.Count, StartingData.SpellweaveToggleAction.Values.Distinct().Count());

			// Non-weaving selectors must never resolve to a trance for ANY class.
			uint[] notWeaves = [0xFF, 0x6C, 0x6D, 0x70, 0xF0, 0x64, 0x68, 0x6A, 0x4E1, 0x4E2, 0x4E4, 0x4E6];
			var collisions = StartingData.SpellweaveToggleAction.Where(kv => notWeaves.Contains(kv.Value))
				.Select(kv => $"{kv.Key} -> 0x{kv.Value:X}").ToList();
			Assert.True(collisions.Count == 0, $"Spellweave selectors collide with non-weave 0x205A actions: {string.Join(", ", collisions)}");

			// Priest of Mitra and Tempest of Set weave in retail but no capture shows either doing it - deliberately
			// unmapped rather than guessed. If a capture ever lands, add the row and delete these two lines.
			Assert.False(StartingData.SpellweaveToggleAction.ContainsKey(ConanClass.PriestOfMitra));
			Assert.False(StartingData.SpellweaveToggleAction.ContainsKey(ConanClass.TempestOfSet));
		}

		[Fact]
		public void BuildStatGauge_Stamina_MatchesCapture()
		{
			// frame#1654: the first "Spellweaving Cost" (stamina) gauge tick (token 0x22CE, delta -90).
			var bytes = StatDamageCodec.Build(StatDamages.SpellweaveGauge(0x0802E5D4, 0x22CE, -90, StatDamages.SpellweaveStaminaGauge, StatDamages.SpellweaveStaminaScale));
			Assert.Equal("e9f241840000c3500802e5d400000022ce000000000000000000000000000f425c00000002ffffffa6000000000000c3500802e5d400000001000000000037e68c00000050000000000000000000000000",
				Convert.ToHexStringLower(bytes));
		}
	}
}
