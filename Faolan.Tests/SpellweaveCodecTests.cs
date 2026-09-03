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

		// ⭐ The per-class weave table, recovered from the CLIENT for #141 (`--report spellweave`). This is the
		// gate on the shape, not on the ids: the ids themselves are re-derivable in one command and a test in a
		// plain clone has no RDB to check them against, so what is asserted here is every invariant the recovery
		// established - which is exactly what a transcription error would break.
		[Fact]
		public void EveryWeavingClass_HasAProfile()
		{
			ConanClass[] weavers = [ConanClass.Necromancer, ConanClass.Demonologist, ConanClass.TempestOfSet, ConanClass.PriestOfMitra];
			foreach (var weaver in weavers)
				Assert.NotNull(SpellweaveCatalog.ForClass(weaver));

			// The four PURE casters weave and nobody else does - a class that weaves melee-combos instead must not
			// pick up a profile, or its toggle opens a trance it has no elements for.
			ConanClass[] nonWeavers = [ConanClass.HeraldOfXotli, ConanClass.BearShaman, ConanClass.Assassin, ConanClass.Guardian];
			foreach (var nonWeaver in nonWeavers)
				Assert.Null(SpellweaveCatalog.ForClass(nonWeaver));
		}

		// The two UNIVERSAL elements. Every class's list opens with Arcane Renewal then Arcane Surge and differs
		// only in the last three, which is the regularity the four rows were read off - a transcription that
		// broke it would be reading the wrong record.
		[Theory]
		[InlineData(ConanClass.Necromancer)]
		[InlineData(ConanClass.Demonologist)]
		[InlineData(ConanClass.TempestOfSet)]
		[InlineData(ConanClass.PriestOfMitra)]
		public void EveryProfile_HasFiveElementsAndSixRungs(ConanClass characterClass)
		{
			var profile = SpellweaveCatalog.ForClass(characterClass)!;

			Assert.Equal(5, profile.WeaveElements.Length);
			Assert.Equal(3762921u, profile.WeaveElements[0]); // Arcane Renewal
			Assert.Equal(3762648u, profile.WeaveElements[1]); // Arcane Surge
			Assert.Equal(SpellweaveCatalog.MaxRung, profile.StackLadder.Length);

			// A rung repeated is a copy-paste; a rung shared with another class is the same mistake across rows.
			Assert.Equal(profile.StackLadder.Length, profile.StackLadder.Distinct().Count());
			Assert.Equal(profile.WeaveElements.Length, profile.WeaveElements.Distinct().Count());
		}

		// ⚠️ The stack is a LADDER, not a base plus an offset. Only the necromancer's six rungs are contiguous,
		// which is why the arithmetic model survived as long as it did; asserting the other three are NOT
		// contiguous is what stops anyone collapsing it back to `base + n`.
		[Fact]
		public void TheStackLadder_IsNotArithmetic()
		{
			var necro = SpellweaveCatalog.ForClass(ConanClass.Necromancer)!.StackLadder;
			Assert.Equal([3663272u, 3663273u, 3663274u, 3663275u, 3663276u, 3663277u], necro);

			// The captured stack walk 0x37E5A8 -> A9 -> AA IS rungs 1, 2 and 3 of that ladder.
			Assert.Equal(0x37E5A8u, SpellweaveCatalog.RungBuff(SpellweaveCatalog.ForClass(ConanClass.Necromancer)!, 0));
			Assert.Equal(0x37E5AAu, SpellweaveCatalog.RungBuff(SpellweaveCatalog.ForClass(ConanClass.Necromancer)!, 2));

			var demonologist = SpellweaveCatalog.ForClass(ConanClass.Demonologist)!.StackLadder;
			Assert.True(demonologist[0] > demonologist[^1], "the Demonologist ladder runs DESCENDING");

			var mitra = SpellweaveCatalog.ForClass(ConanClass.PriestOfMitra)!.StackLadder;
			Assert.NotEqual(mitra[0] + 1, mitra[1]);
		}

		// A weave longer than the ladder saturates on the top rung rather than walking off the end - rung 6's
		// own record carries the damage ramp for everything above it, so there is no seventh buff to reach for.
		[Fact]
		public void RungBuff_SaturatesAtTheTop()
		{
			var profile = SpellweaveCatalog.ForClass(ConanClass.TempestOfSet)!;
			var top = profile.StackLadder[^1];

			Assert.Equal(top, SpellweaveCatalog.RungBuff(profile, SpellweaveCatalog.MaxRung - 1));
			Assert.Equal(top, SpellweaveCatalog.RungBuff(profile, 25));
			Assert.Equal(profile.StackLadder[0], SpellweaveCatalog.RungBuff(profile, -1));
		}

		// ⭐ The backlash is ONE roll partitioned six ways, so every draw must land on at most one outcome. Read
		// as six independent chances it would stack several backlashes on a single component.
		[Theory]
		[InlineData(ConanClass.Necromancer)]
		[InlineData(ConanClass.Demonologist)]
		[InlineData(ConanClass.TempestOfSet)]
		[InlineData(ConanClass.PriestOfMitra)]
		public void TheBacklashPartition_CoversEachRollOnce(ConanClass characterClass)
		{
			var profile = SpellweaveCatalog.ForClass(characterClass)!;

			// The escape band SHRINKS every rung: 24% at rung 1 down to nothing at rung 6, which is the
			// escalating risk the whole mechanic is built on. A partition that did not shrink would make a long
			// weave free.
			int[] expectedEscapes = [24, 19, 14, 9, 4, 0];
			for (var rung = 0; rung < SpellweaveCatalog.MaxRung; rung++)
			{
				var escapes = Enumerable.Range(0, 100).Count(roll => SpellweaveCatalog.RollBacklash(profile, rung, roll) == null);
				Assert.Equal(expectedEscapes[rung], escapes);
			}

			// The class's own two outcomes must be the class's own - sharing one across classes is the failure a
			// copy-pasted row produces, and it would read as working.
			Assert.NotEqual(profile.BacklashCritical, profile.BacklashSignature);
			Assert.Equal(profile.BacklashCritical, SpellweaveCatalog.RollBacklash(profile, 0, 0));
			Assert.Equal(profile.BacklashCritical, SpellweaveCatalog.RollBacklash(profile, 0, 5));
			Assert.Equal(profile.BacklashSignature, SpellweaveCatalog.RollBacklash(profile, 0, 6));
		}

		// The capture's third "trance buff" is a backlash ROLL OUTCOME, and this is the roll that produces it.
		// Arcane Drought sits in band 26-37 of rung 1, which is why NecroL80's activate carried it - and why
		// hardcoding it made every weave open with the same debuff and the other five impossible.
		[Fact]
		public void ArcaneDrought_IsARungOneBacklashOutcome_NotATranceBuff()
		{
			var necro = SpellweaveCatalog.ForClass(ConanClass.Necromancer)!;

			Assert.Equal(3762640u, SpellweaveCatalog.RollBacklash(necro, 0, 26));
			Assert.Equal(3762640u, SpellweaveCatalog.RollBacklash(necro, 0, 37));
			Assert.NotEqual(3762640u, SpellweaveCatalog.RollBacklash(necro, 0, 38));

			// It is still weave state and must not survive a relog (#690) - see IsTranceBuff.
			Assert.True(SpellweaveCatalog.IsTranceBuff(3762640u << 8));
			Assert.NotEqual(3762640u, necro.TranceBuff);
		}

		// Every class's element list must round-trip through the reactive-list codec that carries it to the
		// client - the same builder the captured necro list is byte-exact against, asked of all four.
		[Theory]
		[InlineData(ConanClass.Necromancer)]
		[InlineData(ConanClass.Demonologist)]
		[InlineData(ConanClass.TempestOfSet)]
		[InlineData(ConanClass.PriestOfMitra)]
		public void BuildReactiveList_ForEveryWeavingClass_Builds(ConanClass characterClass)
		{
			var elements = SpellweaveCatalog.ForClass(characterClass)!.WeaveElements;
			var bytes = CombatReactiveCodec.Build(0x0802E5D4, elements);

			// The captured necro activate-response list, whose length the element COUNT sets - so every class
			// building to the same width is the check that all four lists really are five elements of the same
			// shape, and not e.g. one carrying an id too wide for its slot.
			var captured = Convert.FromHexString("54c020890000c3500802e5d4000000000100396ae9003969d80037e6d900396a2200396a3d");
			Assert.Equal(captured.Length, bytes.Length);
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
