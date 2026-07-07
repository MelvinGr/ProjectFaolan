using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Opcodes;

namespace Faolan.Core.Network.Codecs
{
	// The directional-combat guard broadcast (#20 decode 2): CombatShieldIIR (0xCFFD0519) sets an entity's blue
	// shield-arc distribution. Wire: [subop][C350][entity handle][u32 value]. Semantics RE'd from the client's
	// Activate (FUN_0113de90, vtable 0x02041910 slot 2):
	//   - the u32 is stored as the entity's STAT 0xA5 (165), which the shield UI (CombatRoseView_c /
	//     ShieldDirection_n signals) reads to draw the arcs;
	//   - the value is a DECIMAL-PACKED per-direction count: hundreds = Left, tens = Mid, units = Right
	//     (e.g. 210 = 2 shields left, 1 mid, 0 right);
	//   - the client clamps: if the digit sum exceeds 3 it resets the stat to 111 (one shield per arc - the
	//     retail default distribution). So the total guard pool is 3 slices across 3 arcs.
	// Every captured instance carries value 0 (Tortage beach mobs raise no guard), which is the golden anchor
	// (CombatShieldBuilderTests); non-zero distributions are client-RE-derived and await in-client verification
	// via the .shield GM command.
	public static class CombatShieldBuilder
	{
		// The retail default distribution the client's own clamp resets to: one shield on each arc.
		public const uint DefaultDistribution = 111;

		public static byte[] Build(uint entityHandle, uint left, uint mid, uint right)
		{
			return Build(entityHandle, left * 100 + mid * 10 + right);
		}

		public static byte[] Build(uint entityHandle, uint packedValue)
		{
			return new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.CombatShieldIIR).WriteUInt32(EntityClass.Character).WriteUInt32(entityHandle).WriteUInt32(packedValue).ToArray();
		}
	}
}
