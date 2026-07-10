using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// CoreClaimIIR (the post-ready-burst "core claim" that opens the world session for the player). Body =
	// [subop][0xC350][handle][0x00 0x09]. Emitted once by GameServerListener.SendPostReadyBurst.
	public static class CoreClaimCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CoreClaimIIR;

		public static byte[] Build(uint handle)
		{
			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteArray(0x00, 0x09)
				.ToArray();
		}
	}
}
