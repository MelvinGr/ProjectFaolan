using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Core.Network.Constants
{
	// What retail applies to a player it is moving on a traveller's trip (#636) - the *Racer* buff that rides between
	// the trip's own GameActionList and the teleport pair.
	//
	// The whole departure, as the captures spell it out (NecroL80_..._chat-annotated-4 fr9788-9795, and identically at
	// fr11534-11541, NecroL80_..._combat fr6157-6170 and Hakotep_..._gameplay0 fr809-814):
	//
	//   c2s KnubotAnswer (the destination pick)
	//   s2c StopIIR -> Bcc -> KnubotCloseChatWindow -> GameActionList(0xD00D) -> MagicBuff(Racer) -> Bcc
	//       -> 0x201B -> IntendedTeleport -> N3Teleport
	//
	// ⚠️ Only the wagoner/list travellers get it. The three `{teleportplayer}` dialogue-directive travellers (#635 -
	// the Tortage Smuggler, Wagoner Polus, Sigurd) send neither the action nor this buff: the corpus has exactly five
	// 0xD00D actions in 120 captures and none of them is in a directive traveller's capture.
	public static class TravelCatalog
	{
		// The WIRE spell id: base 0x4CBD29 (`Spells` row 5029161, "Racer") with form 0 = APPLY. Identical on all four
		// captured trips, across two characters and three game servers.
		public const uint RacerSpell = 0x4CBD2900;

		// 50 centiseconds - the duration retail states, byte-identical on all four. Half a second is about as long as
		// the relocate takes, which is what the buff appears to cover.
		public const uint RacerDurationCentiseconds = 50;

		// The Racer buff's MagicBuffIIR data region. It is a CASTERLESS apply - retail writes a zero entity-class word
		// where the caster reference would be - so it takes the no-caster form rather than the generic one; see
		// `MagicBuffDatas.ApplyNowNoCaster` for what the two slots that differ are and why.
		//
		// `level` is the travelling player's own level, which is the only thing that moves between the four captured
		// sends: 24 on Hakotep's ride, 80 on all three of NecroL80's.
		public static MagicBuffData RacerPayload(uint level)
		{
			return MagicBuffDatas.ApplyNowNoCaster(RacerSpell >> 8, RacerDurationCentiseconds, level);
		}

		// The same payload with the expiry stamp supplied - for the golden test, which reproduces one captured send and
		// therefore has to pin that session's stamp rather than this process's uptime.
		public static MagicBuffData RacerPayload(uint level, ulong expiryStamp)
		{
			return MagicBuffDatas.ApplyNoCaster(RacerSpell >> 8, RacerDurationCentiseconds, expiryStamp, level);
		}
	}
}
