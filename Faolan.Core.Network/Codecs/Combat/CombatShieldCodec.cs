using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Construct-side codecs for the server->client combat-state records (#216/#218, docs/re/iir-readers-combat-world.md):
	// the damage result, the combat shield, the threat level and the hate-list update. These are the records the
	// combat FSM broadcasts so onlookers see damage numbers, shields, and aggro. All share the IIR carrier
	// `[u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][reader]` and are big-endian. Golden tests in
	// CombatRecordCodecTests prove byte-exact construction against captured records. Each Build/Parse is a thin
	// entry point around a nested self-serializing Message : ICodec<Message>.

	// CombatShieldIIR_c (0xCFFD0519) - a single u32 shield value on the target (FUN_0113e000).
	public static class CombatShieldCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CombatShieldIIR;

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
