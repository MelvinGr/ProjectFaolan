using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Golden decode of the item stat-modifier region (docs/rdb/item-template-stats.md, #3). Ground truth is the
	// in-client-verified Royal Signet tooltip (template 3774542, verified 2026-06-07 in docs/re/spawn-dissection.md
	// STEP (b)): +30 Constitution / +52 Magic Damage / +15 Hit Rating. The bytes below are that item's real decrypted
	// RDB template BODY (the span after the 12-byte RDB-Id header), so this is NOT self-referential - it proves the
	// parser reproduces an independently-observed tooltip.
	public class ItemTemplateStatModifierTests
	{
		private const string RoyalSignetBodyBase64 = "RXUAAAQAAAAPAAAAFwAAAEwvAABMAAAAAAAAAB4AAAAFAAAAAAAAAAEAAAUqAQAAAAYAAFgAAAAJAAAA8wEAAAMAAADwAQAArA0AAGgB" +
		                                             "AABkAAAASgAAAORxAAA2AAAASAAAAAwAAAAGkjEAFQAAACEAAAAMAAAAUm95YWwgU2lnbmV0AgAAAA4AAADEDwAANc8AAAAAAAAEAAAA" +
		                                             "AAAAAAEAAAAAAAAAAgAAAAkAAAA2AQAADwAAADXPAAAAAAAABAAAAAAAAAABAAAAAAAAAAIAAAAJAAAAJgMAACwBAAA1zwAAAAAAAAQA" +
		                                             "AAAAAAAAAQAAAAAAAAACAAAACQAAAF0DAABQFAAAFgAAACQAAADiBwAABgAAAOIHAAAAAAAApB8AAF0AAAA=";

		// #441: the region is NOT 4-byte aligned to the body in most templates, and a word-aligned scan cannot see an
		// unaligned record at all - it silently returns ZERO, which is why 2873 of 3248 templates read as "no stats" while
		// the client applied a full list from the same bytes. This builds the exact 10-word record the parser anchors on,
		// at every possible byte phase, and requires it to be found in all four.
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void FindsARecordAtEveryByteAlignment(int phase)
		{
			const uint kind = 53045, statId = (uint)CharacterStatId.Constitution, amount = 580;
			uint[] record = [0, kind, 0, 4, 0, 1, 0, 2, 9, statId, amount];

			var body = new byte[phase + record.Length * 4 + 8];
			for (var i = 0; i < record.Length; i++)
				body.WriteU32Le(phase + i * 4, record[i]);

			var mods = ItemTemplateStatModifiers.Parse(body);

			var mod = Assert.Single(mods);
			Assert.Equal(statId, mod.StatId);
			Assert.Equal(amount, mod.WireAmount);
			Assert.Equal(kind, mod.Kind);
		}

		[Fact]
		public void IgnoresGarbageAndResourceRefSignatureHits()
		{
			// A statId outside the real range (an RDB resource type id / garbage) must be dropped even if the 6-word
			// signature matches. Records: [marker 0][4][0][1][0][2][9][statId][amount].
			uint[] words =
			[
				0xCF35, 0, 4, 0, 1, 0, 2, 9, 0x0001, 6600, // valid: +66 HealthMax
				0xCF35, 0, 4, 0, 1, 0, 2, 9, 1000017, 42 // resource-ref statId -> dropped
			];
			var body = new byte[words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				BitConverter.GetBytes(words[i]).CopyTo(body, i * 4);

			var mods = ItemTemplateStatModifiers.Parse(body);

			Assert.Single(mods);
			Assert.Equal((uint)CharacterStatId.HealthMax, mods[0].StatId);
			Assert.Equal(6600u, mods[0].WireAmount);
		}

		[Fact]
		public void RoyalSignet_DecodesToItsInClientTooltip()
		{
			var body = Convert.FromBase64String(RoyalSignetBodyBase64);

			var mods = ItemTemplateStatModifiers.Parse(body);

			// Exactly the three tooltip lines, in file order (Hit Rating, Constitution, Magic Damage).
			Assert.Equal(3, mods.Count);

			Assert.Equal(0x136u, mods[0].StatId); // Hit Rating
			Assert.Equal(15u, mods[0].WireAmount);

			Assert.Equal((uint)CharacterStatId.Constitution, mods[1].StatId);
			Assert.Equal(300u, mods[1].WireAmount);

			Assert.Equal(0x35du, mods[2].StatId); // Magic Damage
			Assert.Equal(5200u, mods[2].WireAmount);

			Assert.Equal(15, Display(0x136)); // +15 Hit Rating (×1)
			Assert.Equal(30, Display((uint)CharacterStatId.Constitution)); // +30 Constitution (×10)
			Assert.Equal(52, Display(0x35d)); // +52 Magic Damage (×100)
			return;

			// ...and each divided by its wire scale reproduces the displayed tooltip value.
			int Display(uint statId)
			{
				return (int)(mods.First(m => m.StatId == statId).WireAmount / ((CharacterStatId)statId).GetScale());
			}
		}
	}
}
