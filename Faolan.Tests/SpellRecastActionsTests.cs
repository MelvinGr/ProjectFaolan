using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-layout proof for the recast GameAction elements (#271/#767). There is no capture of one - retail sends no
	// recast GameAction at all (0 across all 132 dumps, sub-records included) - so these assert the element against
	// the RE'd POSITIONAL grammar (FUN_00899a60) plus the type/key catalogs in docs/re/gameaction-type-ids.tsv and
	// docs/re/gameaction-keylists.tsv, not against a golden capture.
	public class SpellRecastActionsTests
	{
		// ⚠️ The regression that matters most here. The type ids MUST come from the contiguous enum
		// (gameaction-type-ids.tsv), not from gameaction_types.json, whose `typeId` column is a step-4 sequence that
		// is not the wire value. Under the json, ChangeRecastTime read 0xCF81 - which the real catalog names
		// `Feared`. Pinning the four ids here is what stops a recast turning back into a fear on the wire.
		[Fact]
		public void RecastTypeIds_AreTheWireEnum_NotTheStepFourSequence()
		{
			Assert.Equal(0xCFEAu, SpellRecastActions.SetSpellRecastTimeType);     // 53226
			Assert.Equal(0xCF55u, SpellRecastActions.SetSpellLineRecastTimeType); // 53077
			Assert.Equal(0xCF27u, SpellRecastActions.ChangeRecastTimeType);       // 53031
			Assert.Equal(0xCF4Du, SpellRecastActions.ChangeLineRecastTimeType);   // 53069

			// 0xCF81 is `Feared`. No recast builder may ever produce it again.
			Assert.DoesNotContain(0xCF81u, new[]
			{
				SpellRecastActions.SetSpellRecastTimeType, SpellRecastActions.SetSpellLineRecastTimeType,
				SpellRecastActions.ChangeRecastTimeType, SpellRecastActions.ChangeLineRecastTimeType
			});
		}

		// The two SETTERS, in their own keyList order: SetSpellRecastTime is keyed on the SPELL (0x463), its line twin
		// on the LINE (0x2d3). Both put the recast centiseconds in 0x2f9, second.
		[Fact]
		public void SetSpellRecastTimeElement_MatchesPositionalGrammar()
		{
			const uint spellId = 220979;  // Life Leech
			const uint recastCs = 3000;   // 30.00 s - the value its retail tooltip shows

			var element = GameActionDataCodec.Build(SpellRecastActions.SetSpellRecastTime(spellId, recastCs));

			// [typeId 0xCFEA][field 0][version 4][subCount 0][0x2d5=1][0x2d6=0][0x2f2=1][0x2f5=0][spell][recast]
			var expected = Be(0xCFEA, 0, 4, 0, 1, 0, 1, 0, spellId, recastCs);
			Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(element));
		}

		[Fact]
		public void SetSpellLineRecastTimeElement_MatchesPositionalGrammar()
		{
			const uint lineId = 41026;   // Life Leech's GUI stacking line
			const uint recastCs = 3000;

			var element = GameActionDataCodec.Build(SpellRecastActions.SetSpellLineRecastTime(lineId, recastCs));

			var expected = Be(0xCF55, 0, 4, 0, 1, 0, 1, 0, lineId, recastCs);
			Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(element));
		}

		// What actually goes on the wire: the element wrapped in a GameActionListIIR, WITH the ReadSubClass trailer.
		// The codec's `trailer` defaults to false for the construct-only builder that predates this, so a sender that
		// forgets it emits a body the client reads past the end of.
		[Fact]
		public void TheArmedBody_CarriesTheReadSubClassTrailer()
		{
			var action = SpellRecastActions.SetSpellLineRecastTime(41026, 3000);
			var withTrailer = GameActionListCodec.Build(0x00035021, action, trailer: true);
			var without = GameActionListCodec.Build(0x00035021, action);

			Assert.Equal(without.Length + GameActionListCodec.ReadSubClassTrailer.Length, withTrailer.Length);
			Assert.Equal(GameActionListCodec.ReadSubClassTrailer, withTrailer[^GameActionListCodec.ReadSubClassTrailer.Length..]);
		}

		[Fact]
		public void BuildElement_IsGeneric_AcrossKeyValueCounts()
		{
			// SpellStatModifier (0xcfdd) value order [0x463 spell, 0x2d2, 0x2f9 recast, 0x4b7] - four keyList values.
			var el = GameActionDataCodec.Build(SpellRecastActions.Element(0xCFDD, 0, 0, [7u, 0u, 500u, 0u]));
			var expected = Be(0xCFDD, 0, 4, 0, 1, 0, 1, 0, 7, 0, 500, 0);
			Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(el));
		}

		[Fact]
		public void ChangeRecastTimeBody_RoundTripsThroughGameActionListCodec()
		{
			const uint frame = 0x00035021;
			const uint spellId = 42;
			const uint recastCs = 1200; // 12.00 s

			var body = GameActionListCodec.Build(frame, SpellRecastActions.ChangeRecastTime(spellId, recastCs));
			var parsed = GameActionListCodec.Parse(body);

			Assert.Equal(frame, parsed.FrameHandle);
			Assert.Equal(SpellRecastActions.ChangeRecastTimeType, parsed.DynelHandle); // the codec's "dynelHandle" is the element typeId
			Assert.Equal(0u, parsed.Field3);

			// The codec bakes [version 4][subCount 0][0x2d5 = 1]; the tail is the element from 0x2d6 onward. Rebuild the
			// full element from the parsed pieces and confirm it equals the standalone element builder - the two
			// construction paths agree.
			var rebuilt = Be(parsed.DynelHandle, parsed.Field3, 4, 0, 1).Concat(parsed.ActionTail).ToArray();
			Assert.Equal(Convert.ToHexString(GameActionDataCodec.Build(SpellRecastActions.ChangeRecastTime(spellId, recastCs))), Convert.ToHexString(rebuilt));
		}

		// The MODIFIER twin - same grammar, different type id, and its 0x2f9 is a delta rather than the value.
		[Fact]
		public void ChangeRecastTimeElement_MatchesPositionalGrammar()
		{
			const uint spellId = 13479;
			const uint deltaCs = 3000;

			var element = GameActionDataCodec.Build(SpellRecastActions.ChangeRecastTime(spellId, deltaCs));

			// [typeId 0xCF27][field 0][version 4][subCount 0][0x2d5=1][0x2d6=0][0x2f2=1][0x2f5=0][spell][delta]
			var expected = Be(0xCF27, 0, 4, 0, 1, 0, 1, 0, spellId, deltaCs);
			Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(element));
		}

		private static byte[] Be(params uint[] words)
		{
			var b = new byte[words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				b.WriteU32Be(i * 4, words[i]);
			return b;
		}
	}
}
