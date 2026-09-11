using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The mount protocol constants. The player triggers a mount with a BehaviorControlCenter SummonMount action
	///     (BehaviorId 0xA, Mount) whose <c>from</c> is the mount id; the server answers with a channelled cast, the
	///     ride buff and the mounted-state StatUpd. Capture-grounded from the NecroL80 mount/dismount captures.
	/// </summary>
	/// <remarks>
	///     The mount list itself is the <c>Mounts</c> table, lifted from the client's own catalog (RDB spell field 186
	///     names 155 mount summons) and reached through <c>IDatabaseRepository.GetMount</c>. What lives here is what
	///     that row does not state: the channel durations, the two ride-buff spell ids and the buff payload's shape,
	///     all capture-derived with no field in the RDB.
	/// </remarks>
	public static class MountCatalog
	{
		// The summon channel: retail casts the summon spell, then applies the ride buff ~545ms later (mount capture
		// SummonMount frame 498 -> ride buff frame 520 = 0.545s), during which the client plays the horse-runs-up
		// animation. Applying the ride buff instantly mounts with no visible run-up. The DB spell 0x3BAD7D is
		// mislabelled "Stagger" with CastTime 0, so this figure comes from the capture, not the Spell row.
		public const int SummonChannelMs = 545;

		// The dismount channel. Retail acks the press at once and then holds the player mounted for the horse-runs-away
		// animation: mount capture press frame 1231 (t+19.609) -> server echo-ack frame 1253 (t+19.968) -> end-twin
		// frame 1449 (t+24.759). Measured from the server's own ack rather than the press so it carries no client RTT:
		// 4.791s. Every player-initiated dismount in the corpus lands 5.0-5.7s after the press, so this is one
		// duration and not per-mount.
		public const int DismountChannelMs = 4791;

		// The dismount: the ride buff's end twin (rank 01, empty payload), which removes the mounted state. Pressing
		// the mount button while mounted toggles it; retail runs the same channel (cast bar + horse-runs-away
		// animation) then applies this end twin plus a StatUpd clearing the mount stat (0xBA=0). Mount-dismount
		// capture frame 1449.
		public const uint DismountSpell = 0x31EB1501u;

		// Placeholder ceiling. Retail does throw a rider off under fire - one server-initiated dismount in the corpus
		// is a bare end twin with no preceding client press, landing in the same frame as a hit
		// (NecroL80_..._gameplay_chat-annotated, frame 598 hit -> frame 599 end twin) - but the corpus cannot pin the
		// rule: across 27 mounted windows the rider takes 58 hits and is thrown exactly once, and that single throw
		// defeats both obvious models. A per-hit threshold fails (the throwing hit is 10,944, while 24,744 and
		// 200,496 hits elsewhere leave the rider seated) and so does a cumulative one (thrown at 157,669 accumulated,
		// seated at 742,185). It is therefore modelled as a damage meter with a placeholder ceiling, in wire damage
		// (100 display x the /100 display scale). Open work tracked in Gitea #653.
		public const int ThrowOffDamage = 10_000;

		// The ride buff: after the horse spawns, retail applies a second self ApplySpell (spell base 0x31EB15, apply
		// rank 00) which is the mounted/riding state; the dismount is its end twin 0x31EB1501 with an empty payload.
		// Captured in NecroL80_..._login_mount_move_dismount (apply frame 520 / end frame 1449). The summon alone
		// spawns the horse but never seats the player.
		public const uint RideSpell = 0x31EB1500u;

		// The ride buff's duration, 24 h, from the mount capture frame 520 - the DB Spell row for 0x31EB15 is
		// mislabelled, so the capture is the source. The payload is the 54-byte MagicBuffIIR shape SpellweaveCodec
		// builds; the client wants that shape here, not the 50-byte self-buff form.
		//
		// The payload's @46 expiry stamp is not a catalog constant: it is an absolute stamp on the server's uptime
		// clock (the capture's 0x1560F9BB is 358,846,395 ms = 4.15 days of that retail server's uptime, the moment
		// its 24 h ride buff would have ended), so it is sourced per send.
		private const uint RideBuffDurationCentiseconds = 8_640_000; // 24 h

		// The mount speed the server pushes alongside the ride buff (stat 0x0BB, mount capture frame 520). Flat,
		// because the client's own mount record has no speed field: the only field separating a "Swift <horse>" from
		// a "<horse>" is the quality tier (Mount.Quality), and deriving a speed from a rarity ladder would be an
		// invention. The one mount a capture pins is a tierless horse and it is already at 100.
		public const uint MountSpeed = 100;

		/// <summary>
		///     The ride buff's MagicBuffIIR data region in the generic apply form, constructed from its base spell id
		///     and captured duration, with the expiry stamp sourced from the live server clock.
		/// </summary>
		/// <param name="riderLevel">Fills the payload's @26/@42 words.</param>
		/// <remarks>
		///     Byte-identical to the mount capture's frame 520 when that capture's own stamp is supplied, which is what
		///     <c>MountCatalogTests</c> pins. Returns the value; <c>MagicBuffCodec</c> composes it.
		/// </remarks>
		public static MagicBuffData RideBuffPayload(uint riderLevel)
		{
			// The rider is the player, so the payload's caster-is-a-player bit is set.
			return MagicBuffDatas.ApplyNow(RideSpell >> 8, RideBuffDurationCentiseconds, riderLevel, casterIsPlayer: true);
		}

		/// <summary>
		///     The same payload with the expiry stamp supplied, for the golden test: it reproduces one captured send
		///     and therefore has to pin that session's stamp rather than this process's uptime.
		/// </summary>
		public static MagicBuffData RideBuffPayload(uint riderLevel, ulong expiryStamp)
		{
			return MagicBuffDatas.Apply(RideSpell >> 8, RideBuffDurationCentiseconds, expiryStamp, riderLevel, casterIsPlayer: true);
		}
	}
}
