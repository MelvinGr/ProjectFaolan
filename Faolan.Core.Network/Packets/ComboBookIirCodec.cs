namespace Faolan.Core.Network.Packets
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
		private const uint IirMarker = EntityClass.Character;
		private const ushort AddFlag = 0x0001;

		public static byte[] Build(uint instance, uint comboId)
		{
			var content = new FaolanStream();
			content.WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(instance).WriteUInt16(AddFlag).WriteUInt32(comboId);

			var body = content.ToArray();
			var s = new FaolanStream();
			s.WriteUInt32(body.Length);
			s.WriteArray(body);
			return s.ToArray();
		}
	}
}
