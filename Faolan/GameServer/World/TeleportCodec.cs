using System.Numerics;
using Faolan.Core.Data;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// The change-map / teleport prep records the retail server sends before the 0x2002 handoff (#219), decoded from
	// the changemap captures (frames 4200/4212 + 4417/4420) and named against the IIR readers recovered in #233:
	//   - IntendedTeleportIIR_c (0x10D27BC0, "begin teleport to map N")  -> reader FUN_005bfce0 -> PlayfieldProxy
	//   - n3TeleportIIR_t       (0x5A32F0D7, "destination map + position") -> reader FUN_005d1ae0
	// Both embed a PlayfieldProxy stream (FUN_00a6a600): `[u8 version='b'(0x62)][u32 x8][u8 bool]`, whose u32[0] is a
	// constant playfield-class word (0xC79C) and u32[1] is the destination map id. The "begin" body is just the proxy;
	// the "destination" body adds the destination's playfield TOKEN (a per-zone server value, = the destination
	// Zone.PlayfieldToken; 0x00503570 for map 2010, 0x00503610 for map 2000 in the captures) plus the spawn position.
	// Byte layout preserved exactly from the captures (TeleportCodecTests); the field NAMES come from the decode.
	public static class TeleportCodec
	{
		public const uint IntendedTeleportSubOpcode = (uint)GameServer0X2000RespondsOpcodes.IntendedTeleportIIR; // 0x10D27BC0
		public const uint N3TeleportSubOpcode = (uint)GameServer0X2000RespondsOpcodes.N3TeleportIIR;            // 0x5A32F0D7

		private const uint ReceiverClass = 0x0000C350;     // Character class tag
		private const byte PlayfieldProxyVersion = 0x62;   // 'b' - the PlayfieldProxy stream version (FUN_00a6a600)
		private const uint PlayfieldClassWord = 0x0000C79C; // PlayfieldProxy u32[0] (constant in the captures)

		// 0x10D27BC0 "begin teleport to map N": carrier + env + a PlayfieldProxy whose only non-zero fields are the
		// class word and the destination map id.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=01][u8 'b'][u32 0xC79C][u32 destMap][26x 00]
		public static byte[] BuildBegin(uint clientInstance, uint destMapId)
		{
			return new FaolanStream()
				.WriteUInt32(IntendedTeleportSubOpcode)
				.WriteUInt32(ReceiverClass)
				.WriteUInt32(clientInstance)
				.WriteByte(0x01)                     // env
				.WriteByte(PlayfieldProxyVersion)    // PlayfieldProxy version 'b'
				.WriteUInt32(PlayfieldClassWord)     // PlayfieldProxy u32[0]
				.WriteUInt32(destMapId)              // PlayfieldProxy u32[1] = destination map
				.WriteArray(new byte[26])            // PlayfieldProxy u32[2..7]=0 + its bool + the trailing bool
				.ToArray();
		}

		// 0x5A32F0D7 destination announce: carrier + 7 leading words (the last = 1.0f) + the destination PlayfieldProxy
		// + the destination playfield token + the spawn position + a descriptor word.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=00][24x 00][f32 1.0][u8 'b'][u32 0xC79C][u32 destMap]
		//   [15x 00][01][u32 0x9C50][u32 destPlayfieldToken][12x 00][14][u32 0x00018704][pos x/y/z f32 BE]
		//   [u32 destDescriptor][00]
		// destPlayfieldToken is the destination zone's per-instance playfield token - the LIVE change-map path sources
		// it from the destination map's burst (WorldBroadcaster.ResolveMapPlayfieldToken) so it matches the token the
		// reconnect's world-entry stamps (#211); it is NOT a per-map constant (the captured 0x00503570 was that
		// session's instance). destDescriptor (0x14 / 0xBE) is a per-zone descriptor not yet derivable from the data.
		// Both default to the map-2010 capture values for the golden test / a burst-less map.
		public static byte[] BuildDestination(uint clientInstance, uint destMapId, Vector3 position,
			uint destPlayfieldToken = 0x00503570u, uint destDescriptor = 0x00000014u)
		{
			return new FaolanStream()
				.WriteUInt32(N3TeleportSubOpcode)
				.WriteUInt32(ReceiverClass)
				.WriteUInt32(clientInstance)
				.WriteByte(0x00)                     // env
				.WriteArray(new byte[24])            // leading words u32[0..5] = 0
				.WriteFloatBigEndian(1.0f)           // leading word u32[6] = 1.0
				.WriteByte(PlayfieldProxyVersion)    // PlayfieldProxy version 'b'
				.WriteUInt32(PlayfieldClassWord)     // PlayfieldProxy u32[0]
				.WriteUInt32(destMapId)              // PlayfieldProxy u32[1] = destination map
				.WriteArray(new byte[15])            // PlayfieldProxy remainder
				.WriteByte(0x01)
				.WriteUInt32(0x00009C50u)            // constant
				.WriteUInt32(destPlayfieldToken)     // destination zone playfield token
				.WriteArray(new byte[12])
				.WriteByte(0x14)
				.WriteUInt32(0x00018704u)            // constant
				.WriteVector3BigEndian(position)     // spawn position
				.WriteUInt32(destDescriptor)         // destination descriptor (per-zone)
				.WriteByte(0x00)
				.ToArray();
		}
	}
}
