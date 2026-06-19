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
	}
}
