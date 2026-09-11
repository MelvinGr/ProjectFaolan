using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The two records that tell a client it is being attacked. Both are addressed to the player - it is the
	// player's threat list that gains an entry, not the creature's - and building them the other way round is
	// invisible on the server and total on the client: the records are dropped, the client never re-polls
	// RequestAggroStatus, and a creature that has turned hostile keeps drawing green and refuses to be attacked back
	// while it beats on the player.
	//
	// Bytes are retail's own, from the Saddur fight in
	// `xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city` (frames 8480), player 0x01317cf8 vs Saddur
	// 0x0005e96f. The u32 carrier length belongs to the sender, so it is not part of the body.
	public class NpcAggroRecordTests
	{
		private const uint Player = 0x01317CF8;
		private const uint Saddur = 0x0005E96F;

		[Fact]
		public void HateListUpdate_IsAddressedToThePlayer_AndNamesTheCreature()
		{
			var expected = Hex("8a01e5a1" + "0000c350" + "01317cf8" + "00" + "00" + "0000c350" + "0005e96f" + "00000001");

			Assert.Equal(expected, HateListUpdateCodec.Build(Player, Saddur));
		}

		// The corpus is unanimous on which way round it goes: 270 of 270 captured HateListUpdates carry the session
		// player as the carrier. Asserting the swapped form is not what we build keeps the regression explicit rather
		// than relying on the reader to spot two handles in the right order.
		[Fact]
		public void HateListUpdate_IsNotBuiltCreatureFirst()
		{
			Assert.NotEqual(HateListUpdateCodec.Build(Saddur, Player), HateListUpdateCodec.Build(Player, Saddur));
		}

		[Fact]
		public void ThreatLevel_IsAddressedToThePlayer()
		{
			// Retail opens the fight with threat 0 and then threat 1, both on the player, flag 1.
			Assert.Equal(Hex("1745ef6c" + "0000c350" + "01317cf8" + "00" + "00000000" + "01"), ThreatLevelCodec.Build(Player, 0, 1));
			Assert.Equal(Hex("1745ef6c" + "0000c350" + "01317cf8" + "00" + "00000001" + "01"), ThreatLevelCodec.Build(Player, 1, 1));
		}

		private static byte[] Hex(string hex)
		{
			return Convert.FromHexString(hex);
		}
	}
}
