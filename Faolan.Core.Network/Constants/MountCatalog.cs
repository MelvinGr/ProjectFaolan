using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Core.Network.Constants
{
	// The MOUNT PROTOCOL constants (#27). The player triggers a mount with a BehaviorControlCenter SummonMount
	// action (BehaviorId 0xA, Mount) whose `from` is the mount id; the server answers with a channelled cast, the
	// ride buff and the mounted-state StatUpd. Capture-grounded from the NecroL80 mount/dismount captures.
	//
	// The mount LIST is not here any more (#627). It is a real table, `Mounts`, lifted whole from the client's own
	// catalog - RDB spell field 186 names 155 mount summons - so a mount is a row (id, creature, quality), reached
	// through IDatabaseRepository.GetMount. What is left here is exactly what that record does NOT state: the
	// channel duration, the two ride-buff spell ids and the buff payload's shape, all of which are capture-derived
	// and have no field in the RDB.
	public static class MountCatalog
	{
		// The summon CHANNEL: retail casts the summon spell, then applies the ride buff ~545ms later (mount capture
		// SummonMount frame 498 -> ride buff frame 520 = 0.545s), during which the client plays the horse-runs-up
		// animation. Applying the ride buff instantly (as we did) mounts with NO visible run-up. (The DB spell 0x3BAD7D
		// is mislabelled "Stagger" with CastTime 0, so this comes from the capture, not the Spell row.)
		public const int SummonChannelMs = 545;

		// The DISMOUNT: the ride buff's end twin (rank 01, empty payload) - removes the mounted state. Pressing the
		// mount button while mounted toggles it; retail runs the SAME channel (cast bar + horse-runs-away animation)
		// then applies this end-twin + a StatUpd clearing the mount stat (0xBA=0). Mount-dismount capture frame 1449.
		public const uint DismountSpell = 0x31EB1501u;

		// The RIDE buff (#26): after the horse spawns, retail applies a second SELF ApplySpell - spell base 0x31EB15,
		// apply rank 00 - the mounted/riding state; the dismount is its end twin 0x31EB1501 with an empty payload.
		// Fully captured in NecroL80_..._login_mount_move_dismount (apply frame 520 / end frame 1449). The summon alone
		// spawns the horse but never seats the player (proven in-client 2026-06-17).
		public const uint RideSpell = 0x31EB1500u;

		// The captured ride-buff fact (the DB Spell row for 0x31EB15 is mislabelled, so this comes from the mount
		// capture, frame 520): a 24 h duration. The payload itself is the 54-byte MagicBuffIIR shape SpellweaveCodec
		// builds (the client wants that shape here, not the 50-byte self-buff form).
		//
		// What is NOT a catalog constant is the payload's @46 expiry stamp. That capture's 0x1560F9BB reads as an
		// "effect nonce" only until you put it on the right clock: it is 358,846,395 ms = 4.15 days of that retail
		// server's UPTIME, i.e. the moment the 24 h ride buff would have ended (#480). It is session state and is
		// sourced per send, like every other absolute stamp.
		private const uint RideBuffDurationCentiseconds = 8_640_000; // 24 h

		// The mount SPEED the server pushes alongside the ride buff (stat 0x0BB, mount capture frame 520). Flat,
		// because the client's own mount record has no speed field: the ONLY field separating a "Swift <horse>" from
		// a "<horse>" is the quality tier (Mount.Quality), and deriving a speed from a rarity ladder would be an
		// invention. The one mount a capture pins is a tierless horse and it is already at 100 (#627).
		public const uint MountSpeed = 100;

		// The mount RIDE buff's MagicBuffIIR data region (the generic APPLY form), CONSTRUCTED from its base spell id +
		// captured duration, with the expiry stamp sourced from the live server clock (byte-identical to the
		// mount-capture frame 520 when that capture's own stamp is supplied - proven by MountCatalogTests).
		// `riderLevel` fills the payload's @26/@42 words - the mount capture is a level-80 character, which is where the
		// old hardcoded 80 came from (#438). Returns the VALUE; MagicBuffCodec composes it (#539).
		public static MagicBuffData RideBuffPayload(uint riderLevel)
		{
			return MagicBuffDatas.ApplyNow(RideSpell >> 8, RideBuffDurationCentiseconds, riderLevel);
		}

		// The same payload with the expiry stamp supplied - for the golden test, which reproduces one captured send and
		// therefore has to pin that session's stamp rather than this process's uptime.
		public static MagicBuffData RideBuffPayload(uint riderLevel, ulong expiryStamp)
		{
			return MagicBuffDatas.Apply(RideSpell >> 8, RideBuffDurationCentiseconds, expiryStamp, riderLevel);
		}
	}
}
