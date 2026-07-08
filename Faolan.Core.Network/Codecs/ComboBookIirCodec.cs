using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs
{
	// Builds the ComboBookIIR (sub-opcode 0xA8BBEB7F) body - grants ONE combo into the client's combo book. Sent as
	// part of the create-flow world-prep burst (the starting combos) AND on a level-up ding (the combos unlocked at
	// the new level - #209). The level-up combos are level-keyed and class-INDEPENDENT (Guardian/Conqueror/Assassin/
	// Mage all get the same set at L2), captured in LevelComboCatalog.
	//
	// Carried as an IIR on the WORLD conversation (opcode 0x2000; receiver = the player's GameInterface). Wire grammar
	// (big-endian), decoded from the create + combat-levelup captures (golden-tested in ComboBookIirCodecTests):
	//   [u32 len]          byte length of everything after this field (always 0x12 = 18)
	//   [u32 0xA8BBEB7F]   ComboBookIIR sub-opcode
	//   [u32 0x0000C350]   Character marker (EntityClass.Character)
	//   [u32 instance]     the target entity object (= the receiver endpoint instance / ClientInstance)
	//   [u16 0x0001]       constant (an add/type flag)
	//   [u32 comboId]      the combo id granted
	public static class ComboBookIirCodec
	{
		public const uint SubOpcode = 0xA8BBEB7F;
		private const ushort AddFlag = 0x0001;

		public static byte[] Build(uint instance, uint comboId) => new Message(instance, comboId).ToArray();

		// The ComboBookIIR body as a self-serializing codec (ICodec): Write frames the whole
		// [len][subop][marker][instance][addFlag][comboId]; Read recovers the (instance, comboId).
		public sealed record Message(uint Instance, uint ComboId) : ICodec<Message>
		{

			public void Write(FaolanStream s)
			{
				s.WriteArrayUInt32Length(content =>
					content.WriteUInt32(SubOpcode).WriteUInt32(EntityClass.Character).WriteUInt32(Instance).WriteUInt16(AddFlag).WriteUInt32(ComboId));
			}

			public static Message Read(FaolanStream s)
			{
				s.ReadUInt32(); // len
				s.ReadUInt32(); // subop (0xA8BBEB7F)
				s.ReadUInt32(); // 0xC350 marker
				var instance = s.ReadUInt32();
				s.ReadUInt16(); // add flag 0x0001
				var comboId = s.ReadUInt32();
				return new Message(instance, comboId);
			}
		}
	}
}
