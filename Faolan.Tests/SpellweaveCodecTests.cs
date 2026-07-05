using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the spellweaving activate-response + fire records vs the captured bytes in
	// NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (docs/combos.md,
	// Captures/decode_spellweave.py). caster = 0x0802E5D4 throughout; these are the record bytes AFTER the 0x2000
	// carrier's u32 length prefix (the golden hex is the frame body with that prefix stripped).
	public class SpellweaveCodecTests
	{
		[Fact]
		public void BuildReactiveList_Necro_MatchesCapture()
		{
			// frame#1647: the activate-response element list - all 5 necro weave elements available.
			var elements = SpellweaveCatalog.ForClass(Faolan.Core.Enums.Class.Necromancer).WeaveElements;
			var bytes = SpellweaveCodec.BuildReactiveList(0x0802E5D4, elements);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9003969d80037e6d900396a2200396a3d",
				System.Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_ConsumedElementZeroed_MatchesCapture()
		{
			// frame#1774: the per-add re-send after weaving element[1] (Arcane Surge) - that slot is zeroed.
			var bytes = SpellweaveCodec.BuildReactiveList(0x0802E5D4,
				[0x396AE9, 0x000000, 0x37E6D9, 0x396A22, 0x396A3D]);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9000000000037e6d900396a2200396a3d",
				System.Convert.ToHexStringLower(bytes));
		}

		[Theory]
		[InlineData(0x398u, "f98e10b30000c3500802e5d4010000039800007bd4")] // frame#1843
		[InlineData(0x39Bu, "f98e10b30000c3500802e5d4010000039b00007bd4")] // frame#1844
		[InlineData(0x39Au, "f98e10b30000c3500802e5d4010000039a00007bd4")] // frame#1845
		[InlineData(0x39Du, "f98e10b30000c3500802e5d4010000039d00007bd4")] // frame#1846
		public void BuildFireStage_Necro_MatchesCapture(uint fireId, string expected)
		{
			var bytes = SpellweaveCodec.BuildFireStage(0x0802E5D4, fireId);
			Assert.Equal(expected, System.Convert.ToHexStringLower(bytes));
		}
	}
}
