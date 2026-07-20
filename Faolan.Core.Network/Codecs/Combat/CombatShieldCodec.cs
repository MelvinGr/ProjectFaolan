using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Construct-side codecs for the server->client combat-state records (#216/#218, docs/re/iir-readers-combat-world.md):
	// the damage result, the combat shield, the threat level and the hate-list update. These are the records the
	// combat FSM broadcasts so onlookers see damage numbers, shields, and aggro. All share the IIR carrier
	// `[u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][reader]` and are big-endian. Golden tests in
	// CombatRecordCodecTests prove byte-exact construction against captured records. Each Build/Parse is a thin
	// entry point around a nested self-serializing Message : ICodec<Message>.

	// CombatShieldIIR_c (0xCFFD0519) - the directional-combat guard broadcast: an entity's blue shield-arc
	// distribution (#20 decode 2, FUN_0113e000). Semantics RE'd from the client's Activate (FUN_0113de90, vtable
	// 0x02041910 slot 2): the u32 is stored as the entity's STAT 0xA5 (165), which the shield UI (CombatRoseView_c /
	// ShieldDirection_n) reads to draw the arcs, and it is DECIMAL-PACKED per direction - hundreds = Left, tens =
	// Mid, units = Right (210 = 2 left, 1 mid, 0 right). The client clamps a digit sum above 3 back to 111.
	//
	// CAPTURE-CONFIRMED, not merely client-RE: all 86 captured records are 21 bytes
	// ([u32 len=17][subop][C350][handle][u8 env=0][u32 value]) and carry REAL distributions - 111, 102, 210, 21,
	// 12, 30, 120, 3 - every one of which decodes to three slices summing to exactly 3. (A now-deleted sibling
	// builder claimed "every captured instance carries value 0"; not one does.)
	public static class CombatShieldCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CombatShieldIIR;

		// The retail default the client's own overflow clamp resets stat 0xA5 to: one shield per arc. The most
		// common captured value (17 of 86).
		public const uint DefaultDistribution = 111;

		// Packs a per-direction guard distribution into the wire value. The digit sum must not exceed 3 or the
		// client clamps the whole thing back to DefaultDistribution.
		public static uint Pack(uint left, uint mid, uint right)
		{
			return left * 100 + mid * 10 + right;
		}

		public static byte[] Build(uint handle, uint value, byte env = 0x00)
		{
			return new Message(handle, env, value).ToArray();
		}

		public static Message Parse(byte[] body)
		{
			return Message.Read(new FaolanStream(body));
		}

		public sealed record Message(uint Handle, byte Env, uint Value) : ICodec<Message>
		{
			public static Message Read(FaolanStream s)
			{
				s.ReadUInt32();
				s.ReadUInt32();
				s.ReadUInt32(); // len, subop, marker
				var handle = s.ReadUInt32();
				var env = s.ReadByte();
				return new Message(handle, env, s.ReadUInt32());
			}

			public void Write(FaolanStream s)
			{
				s.WriteArrayUInt32Length(body => body.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env).WriteUInt32(Value));
			}
		}
	}
}
