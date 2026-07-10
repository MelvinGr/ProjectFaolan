using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// PlayfieldFIIR (MapChange, 0x5D85BFC7) - tells the client which playfield/instance it is entering. The body is
	// built by CreateEditorRecordBuilder.BuildPlayfieldFiir (the create-flow builds the SAME body); this codec owns
	// the IIR subop + carrier. See GameServerListener.BuildMapChange / CreateFlowService (#189/#308).
	public static class MapChangeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR;

		// Prepend the IIR subop to a PlayfieldFiir body (from CreateEditorRecordBuilder.BuildPlayfieldFiir).
		public static byte[] BuildIir(byte[] playfieldFiirBody)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteArray(playfieldFiirBody).ToArray();
		}
	}
}
