using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the 54-byte MagicBuffIIR data region as the NpcSpell columns store it:
	// Parse(captured) then Build reproduces the captured payload for every field shape seen in the captures. Samples
	// cover a normal buff (Rend), a debuff (Rooted), the slot3 != castingLevel case (Runed Flesh @29=14/@45=21) and a
	// big-value cast.
	//
	// There is one model for these 54 bytes: MagicBuffDataCodec reads and writes them, and `NpcSpellEffectColumns`
	// maps that record to the sub-byte columns the importer stores.
	public class NpcSpellEffectColumnsTests
	{
		[Theory, InlineData("0000000000000000000002BC000000000001000000000031644900000001000000000000000000000000000000010000000000541455"),
		 InlineData("00000000000000000000025800000000000100000000000337D200000004000000010000000000000000000000040000000004D44D7D"),
		 InlineData("000000000000000000000DAC00010000000100000000001191210000000E000000000000000000000000000000150000000000CACCE3"),
		 InlineData("0000000000000000000005DC000000000001000000000033A6C80000002A000000000000000000000000000000170000000014D3C4D4")]
		public void Codec_RoundTripsCapturedPayload(string hex)
		{
			var captured = Convert.FromHexString(hex);
			var effect = MagicBuffDataCodec.Parse(captured);

			Assert.Equal(hex, Convert.ToHexString(MagicBuffDataCodec.Build(effect)));
		}

		[Fact]
		public void Columns_ReadTheDecodedFields()
		{
			// Rend rank 1: duration 700 (0x2BC), spell id 0x31644900, slot3/castingLevel 1, flags 0/1/0.
			var effect = MagicBuffDataCodec.Parse(Convert.FromHexString("0000000000000000000002BC000000000001000000000031644900000001000000000000000000000000000000010000000000541455"));

			Assert.Equal(700, NpcSpellEffectColumns.Duration(effect));
			Assert.Equal(0x31644900u, NpcSpellEffectColumns.SpellId(effect));
			Assert.Equal(1, NpcSpellEffectColumns.DescriptorSlot3(effect));
			Assert.Equal(1, NpcSpellEffectColumns.CastingLevel(effect));
			Assert.Equal(1, NpcSpellEffectColumns.DescriptorSlot0(effect));
		}

		// The reverse map - the columns back to the wire - is what the live NPC-cast path sends, so it has to
		// reproduce the captured payload from the stored columns alone.
		//
		// Except the trailing u64 ExpiryStamp: `NpcSpell` has no column for it, so the live path sends a zero there.
		// Open work tracked in Gitea #480.
		[Theory, InlineData("0000000000000000000002BC000000000001000000000031644900000001000000000000000000000000000000010000000000541455"),
		 InlineData("000000000000000000000DAC00010000000100000000001191210000000E000000000000000000000000000000150000000000CACCE3")]
		public void Columns_RebuildTheCapturedPayload(string hex)
		{
			var effect = MagicBuffDataCodec.Parse(Convert.FromHexString(hex));
			var fromColumns = NpcSpellEffectColumns.ToBuffData(NpcSpellEffectColumns.SpellId(effect), NpcSpellEffectColumns.Duration(effect),
				NpcSpellEffectColumns.DescriptorSlot3(effect), NpcSpellEffectColumns.CastingLevel(effect), NpcSpellEffectColumns.FlagA(effect),
				NpcSpellEffectColumns.DescriptorSlot0(effect), NpcSpellEffectColumns.DescriptorSlot8(effect));

			Assert.Equal(hex[..^16], Convert.ToHexString(MagicBuffDataCodec.Build(fromColumns))[..^16]);
			Assert.Equal(0ul, fromColumns.ExpiryStamp);
		}
	}
}
