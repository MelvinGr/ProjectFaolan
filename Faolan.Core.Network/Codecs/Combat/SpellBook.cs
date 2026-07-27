using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// SpellBookIIR_t (0x6BA10936). ReadSubClass (FUN_00795ba0) triggers a spellbook refresh on the carrier handle;
	// the body carries a single u32 = a 0x3F1 list-count marker (0x3F1 = empty list, per the GameActionList list
	// framing). Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record SpellBook(uint Handle, byte Env, uint ListMarker) : ICodec<SpellBook>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SpellBookIIR;

		public static SpellBook Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // EntityClass.Character
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			return new SpellBook(handle, env, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(b => b.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env).WriteUInt32(ListMarker));
		}

		public static byte[] Build(uint handle, uint listMarker = ListFrame.Empty, byte env = 0x00)
		{
			return new SpellBook(handle, env, listMarker).ToArray();
		}

		public static SpellBook Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
