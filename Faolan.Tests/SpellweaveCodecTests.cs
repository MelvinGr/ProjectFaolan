using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the spellweaving activate-response + fire records vs the captured bytes in
	// NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (docs/subsystems/combos.md,
	// tools/capture/decode_spellweave.py). caster = 0x0802E5D4 throughout; these are the record bytes AFTER the 0x2000
	// carrier's u32 length prefix (the golden hex is the frame body with that prefix stripped).
	public class SpellweaveCodecTests
	{
		[Theory, InlineData(0x398u, "f98e10b30000c3500802e5d4010000039800007bd4"), InlineData(0x39Bu, "f98e10b30000c3500802e5d4010000039b00007bd4"),
		 InlineData(0x39Au, "f98e10b30000c3500802e5d4010000039a00007bd4"), InlineData(0x39Du, "f98e10b30000c3500802e5d4010000039d00007bd4")]
		// frame#1843
		// frame#1844
		// frame#1845
		// frame#1846
		public void BuildFireStage_Necro_MatchesCapture(uint fireId, string expected)
		{
			var bytes = SpellweaveCodec.BuildFireStage(0x0802E5D4, fireId);
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_ConsumedElementZeroed_MatchesCapture()
		{
			// frame#1774: the per-add re-send after weaving element[1] (Arcane Surge) - that slot is zeroed.
			var bytes = SpellweaveCodec.BuildReactiveList(0x0802E5D4, [0x396AE9, 0x000000, 0x37E6D9, 0x396A22, 0x396A3D]);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9000000000037e6d900396a2200396a3d", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_Necro_MatchesCapture()
		{
			// frame#1647: the activate-response element list - all 5 necro weave elements available.
			var elements = SpellweaveCatalog.ForClass(ConanClass.Necromancer)!.WeaveElements;
			var bytes = SpellweaveCodec.BuildReactiveList(0x0802E5D4, elements);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9003969d80037e6d900396a2200396a3d", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildStatGauge_HealthDot_MatchesCapture()
		{
			// the "Spellweaving" health DoT gauge that hits when stamina empties (token 602395, delta -31705, scale 0x1B).
			var bytes = SpellweaveCodec.BuildStatGauge(0x0802E5D4, 602395, -31705, SpellweaveCodec.DotGauge, SpellweaveCodec.DotScale);
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
			var bytes = SpellweaveCodec.BuildStatGauge(0x0802E5D4, 0x22CE, -90, SpellweaveCodec.StaminaGauge, SpellweaveCodec.StaminaScale);
			Assert.Equal("e9f241840000c3500802e5d400000022ce000000000000000000000000000f425c00000002ffffffa6000000000000c3500802e5d400000001000000000037e68c00000050000000000000000000000000",
				Convert.ToHexStringLower(bytes));
		}
	}
}
