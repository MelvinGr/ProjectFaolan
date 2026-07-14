using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-layout proof for the ChangeRecastTime GameAction override (#271). There is no capture of a recast
	// GameAction (retail sends none on a normal cast - base cooldowns are client-side), so this asserts the element
	// against the RE'd POSITIONAL grammar (FUN_00899a60 + Captures/re/gameaction_types.json), not a golden capture.
	public class SpellRecastActionBuilderTests
	{
		private static byte[] Be(params uint[] words)
		{
			var b = new byte[words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4, 4), words[i]);
			return b;
		}

		[Fact]
		public void ChangeRecastTimeElement_MatchesPositionalGrammar()
		{
			const uint spellId = 13479;
			const uint recastCs = 3000; // 30.00 s (Life Leech), centiseconds

			var element = SpellRecastActionBuilder.BuildChangeRecastTimeElement(spellId, recastCs);

			// [typeId 0xCF81][field 0][version 4][subCount 0][0x2d5=1][0x2d6=0][0x2f2=1][0x2f5=0][spell][recast]
			var expected = Be(0xCF81, 0, 4, 0, 1, 0, 1, 0, spellId, recastCs);
			Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(element));
		}

		[Fact]
		public void ChangeRecastTimeBody_RoundTripsThroughGameActionListCodec()
		{
			const uint frame = 0x00035021;
			const uint spellId = 42;
			const uint recastCs = 1200; // 12.00 s

			var body = SpellRecastActionBuilder.BuildChangeRecastTimeBody(frame, spellId, recastCs);
			var parsed = GameActionListCodec.Parse(body);

			Assert.Equal(frame, parsed.FrameHandle);
			Assert.Equal(SpellRecastActionBuilder.ChangeRecastTimeType, parsed.DynelHandle); // the codec's "dynelHandle" is the element typeId
			Assert.Equal(0u, parsed.Field3);

			// The codec bakes [version 4][subCount 0][0x2d5 = 1]; the tail is the element from 0x2d6 onward. Rebuild the
			// full element from the parsed pieces and confirm it equals the standalone element builder - the two
			// construction paths agree.
			var rebuilt = Be(parsed.DynelHandle, parsed.Field3, 4, 0, 1)
				.Concat(parsed.ActionTail)
				.ToArray();
			Assert.Equal(
				Convert.ToHexString(SpellRecastActionBuilder.BuildChangeRecastTimeElement(spellId, recastCs)),
				Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void BuildElement_IsGeneric_AcrossKeyValueCounts()
		{
			// SpellStatModifier (0xcfdd) value order [0x463 spell, 0x2d2, 0x2f9 recast, 0x4b7] - four keyList values.
			var el = SpellRecastActionBuilder.BuildElement(0xCFDD, field: 0, subCount: 0, keyValues: [7u, 0u, 500u, 0u]);
			var expected = Be(0xCFDD, 0, 4, 0, 1, 0, 1, 0, 7, 0, 500, 0);
			Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(el));
		}
	}
}
