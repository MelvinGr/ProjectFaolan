using System.Numerics;
using Faolan.Core.Data;

namespace Faolan.GameServer.World
{
	// SimpleCharKilledByUpdateIIR (0xB066AFC2) - the explicit "this entity died" trigger. A health=0 StatUpdate
	// alone does NOT drive the client's death animation / dead (release-screen) state (docs/combat.md, #204/#214);
	// the client only reacts when it also gets this kill record. Decoded from AssassinL23_..._resurect: the player-death
	// sequence right before the 0x2024 release screen carries this for the dying handle.
	//
	// Record (the IIR carrier prepends a u32 length): [subop][0000C350][dyingHandle][00][01][0000C350][killerEndpoint].
	// The trailing [00][01] + 0000C350 are constant; killerEndpoint is the slayer (ProtoEndpoint-style type<<16|instance);
	// 0 when unknown/self.
	public static class WorldDeath
	{
		public const uint SimpleCharKilledBySubOpcode = 0xB066AFC2;

		// The self-applied "you are dead" effect retail sends right after the 0x2024 release screen (ApplySpell
		// MagicBuff 0x6B87E316, spell 0x0009F000, applyTo == applyFrom == the dying player). SimpleCharKilledBy +
		// the resurrection screen alone left the client alive in-client (#214); this effect is the piece that drives
		// the dead/release visual state. Decoded from AssassinL23_..._resurect pkt#4051: applyTo/applyFrom rewritten to
		// the live handle, payload kept verbatim (capture-derived interim, like the starting shield buff - the
		// effect's magnitude/duration fields aren't decoded yet).
		private const uint DeathEffectSubOpcode = 0x6B87E316; // ApplySpell / MagicBuff
		public const uint DeathEffectSpellId = 0x0009F000;

		private static readonly byte[] DeathEffectPayload =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xBF, 0x20, 0x00, 0x01, 0x00, 0x00,
			0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0xF0, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00,
			0x00, 0x00, 0x14, 0xEF, 0x17, 0x7D,
		];

		public static byte[] BuildKilledBy(uint dyingHandle, uint killerEndpoint) =>
			new FaolanStream()
				.WriteUInt32(SimpleCharKilledBySubOpcode)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(dyingHandle)
				.WriteByte(0x00)
				.WriteByte(0x01)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(killerEndpoint)
				.ToArray();

		public static byte[] BuildDeathEffect(uint dyingHandle) =>
			new FaolanStream()
				.WriteUInt32(DeathEffectSubOpcode)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(dyingHandle) // applyTo = the dying player
				.WriteUInt16(0x0000)
				.WriteUInt32(DeathEffectSpellId)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(dyingHandle) // applyFrom = self
				.WriteArray(DeathEffectPayload)
				.ToArray();

		// The death-pose MoveTask retail forces onto the dying PLAYER's own handle right before the kill record
		// (AssassinL23_..._resurect pkt#4048): a minimal MoveTask (0x96C46740) with movement-type 0x06 and NO waypoint/
		// speed payload - just [class][handle][u16 0][pos X,Y,Z big-endian][0x06] - at the death position. Normal
		// movement is type 0x07 followed by a full waypoint list; 0x06 is the "stopped/incapacitated" dead state. The
		// SimpleCharKilledBy + 0x2024 + death effect alone left the avatar standing and the release UI closed (#214).
		private const uint MoveTaskSubOpcode = 0x96C46740;
		private const byte DeadMovementType = 0x06;

		public static byte[] BuildDeathPose(uint dyingHandle, Vector3 position) =>
			new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(dyingHandle)
				.WriteUInt16(0x0000)
				.WriteFloatBigEndian(position.X)
				.WriteFloatBigEndian(position.Y)
				.WriteFloatBigEndian(position.Z)
				.WriteByte(DeadMovementType)
				.ToArray();
	}
}
