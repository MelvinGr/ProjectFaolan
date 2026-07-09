using System.Numerics;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The change-map / teleport prep records the retail server sends before the 0x2002 handoff (#219), decoded
	// byte-for-byte from the changemap captures (frames 4200/4212 map 2010, 4417/4420 map 2000) against the IIR
	// readers recovered in #233 (full reader walk in docs/subsystems/teleport-changemap.md):
	//   - IntendedTeleportIIR_c (0x10D27BC0, "begin teleport to map N")  -> reader FUN_005bfce0
	//   - n3TeleportIIR_t       (0x5A32F0D7, "destination map + position") -> reader FUN_005d1ae0
	// Both embed a PlayfieldProxy stream (FUN_00a6a600): `[u8 version='b'(0x62)][u32 x8][u8 bool]`, where u32[0] is
	// a constant playfield-class word (0xC79C) and u32[1] the destination map. The "begin" proxy carries only those
	// two (the destination instance isn't pinned yet); the dest-announce proxy is fully specified - u32[5]=1 (a
	// valid/ready flag), u32[6]=0x9C50, u32[7]=the destination's playfield-INSTANCE token. The dest body then carries
	// the spawn position inside a length-prefixed blob (FUN_00cbf5d0). Every byte is reader-accounted (TeleportHandoffTests).
	public static class TeleportCodec
	{
		// The IIR types this codec constructs or parses (#317 convention; read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.IntendedTeleportIIR,
			GameServer0X2000Opcodes.N3TeleportIIR,
		];

		private const byte PlayfieldProxyVersion = 0x62; // 'b' - the PlayfieldProxy stream version (FUN_00a6a600)
		private const uint PlayfieldClassWord = 0x0000C79C; // PlayfieldProxy u32[0] (constant in the captures)
		private const uint PositionBlobTag = 0x00018704; // first word of the length-prefixed spawn-position blob
		private const uint ProxyInstanceWord = 0x00009C50; // dest proxy u32[6] (constant in the captures)
		private const uint ProxyReadyFlag = 0x00000001; // dest proxy u32[5] - instance valid/ready flag

		private const uint ReceiverClass = 0x0000C350; // Character class tag

		// 0x10D27BC0 "begin teleport to map N": carrier + env + a PlayfieldProxy carrying only the class word and the
		// destination map (instance slots zero) + a trailing bool.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=01][PlayfieldProxy][u8 bool=0]
		public static byte[] BuildBegin(uint clientInstance, uint destMapId)
		{
			var s = new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.IntendedTeleportIIR).WriteUInt32(ReceiverClass).WriteUInt32(clientInstance).WriteByte(0x01); // env
			WritePlayfieldProxy(s, destMapId, 0, 0, 0);
			return s.WriteByte(0x00).ToArray(); // IntendedTeleportIIR trailing bool (-> obj+0x40)
		}

		// 0x5A32F0D7 destination announce: carrier + 7 leading words (the last = 1.0f) + the fully-specified
		// destination PlayfieldProxy + 2 words + a length-prefixed spawn-position blob + a trailing bool.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=00]
		//   [6x u32 0][f32 1.0]                                  # 7 leading words (-> obj+0x2c..)
		//   [PlayfieldProxy with slot5=1, slot6=0x9C50, slot7=destPlayfieldToken]
		//   [u32 0][u32 0]                                       # -> obj+0x6c / +0x70
		//   [u32 len=20][u32 0x18704][pos x/y/z f32 BE][u32 destDescriptor]   # the position blob (FUN_00cbf5d0)
		//   [u8 bool=0]                                          # -> obj+0x90
		// destPlayfieldToken is the destination playfield-INSTANCE token (NOT a per-map constant - the captured
		// 0x00503570 was that session's instance); the live change-map path sources it from the destination map's
		// burst via WorldBroadcaster.ResolveMapPlayfieldToken so it matches the reconnect's world-entry stamp (#211).
		// destDescriptor (0x14 map 2010 / 0xBE map 2000) is the position blob's trailing word - a per-zone descriptor
		// not yet derivable from the data. Both default to the map-2010 capture values for the golden / a burst-less map.
		public static byte[] BuildDestination(uint clientInstance, uint destMapId, Vector3 position, uint destPlayfieldToken = 0x00503570u, uint destDescriptor = 0x00000014u)
		{
			// The 20-byte spawn-position blob the dest reader pulls via the length-prefixed sub-stream.
			var positionBlob = new FaolanStream().WriteUInt32(PositionBlobTag).WriteVector3(position).WriteUInt32(destDescriptor).ToArray();

			var s = new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.N3TeleportIIR)
				.WriteUInt32(ReceiverClass)
				.WriteUInt32(clientInstance)
				.WriteByte(0x00) // env
				.WriteArray(new byte[24]) // 6 leading words = 0
				.WriteFloat(1.0f); // 7th leading word = 1.0
			WritePlayfieldProxy(s, destMapId, ProxyReadyFlag, ProxyInstanceWord, destPlayfieldToken);
			return s
				.WriteUInt32(0)
				.WriteUInt32(0) // obj+0x6c / +0x70
				.WriteUInt32(positionBlob.Length)
				.WriteArray(positionBlob)
				.WriteByte(0x00) // n3TeleportIIR trailing bool
				.ToArray();
		}

		// The same-map REVIVE destination announce (#214): the n3Teleport (0x5A32F0D7) retail sends right after the
		// revive IntendedTeleport - THIS is the packet that actually relocates the player to the res point (no loading
		// screen, same map). Its grammar DIFFERS from the cross-map BuildDestination: the spawn position is right after
		// env (not in a trailing length-prefixed blob), the proxy carries 0xC7A0 (not 0xC79C) + map + player handle
		// with the instance slots ZERO, and there's no leading 6-zero/1.0f block. Decoded byte-for-byte from the beach
		// resurrect (Stihetfv #1277, 92 bytes, map 0xFAA):
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=0]
		//   [f32 X][f32 Y][f32 Z]                         # res point position
		//   [u32 0][f32 facingX][u32 0][f32 facingZ]      # 2D heading
		//   [u8 0x62][u32 0xC7A0][u32 map][u32 0][u32 playerHandle][u32 0 x4]   # PlayfieldProxy (instance slots 0)
		//   [u32 0][u32 0][u32 0][u8 0][u8 1]             # trailing
		public static byte[] BuildReviveDestination(uint clientInstance, uint mapId, uint playerHandle, Vector3 position, float facingX, float facingZ)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.N3TeleportIIR)
				.WriteUInt32(ReceiverClass)
				.WriteUInt32(clientInstance)
				.WriteByte(0x00) // env
				.WriteVector3(position)
				.WriteUInt32(0)
				.WriteFloat(facingX)
				.WriteUInt32(0)
				.WriteFloat(facingZ)
				.WriteByte(PlayfieldProxyVersion)
				.WriteUInt32(0x0000C7A0) // proxy[0] (revive uses 0xC7A0, not 0xC79C)
				.WriteUInt32(mapId) // proxy[1] = current map
				.WriteUInt32(0) // proxy[2]
				.WriteUInt32(playerHandle) // proxy[3]
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0) // proxy[4..7] (instance slots zero)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0) // trailing words
				.WriteByte(0x00)
				.WriteByte(0x01) // trailing bytes
				.ToArray();
		}

		// The same-map REVIVE teleport (#214): the single IntendedTeleport (0x10D27BC0) retail sends after the
		// RespawnMe revive to relocate the player to the chosen res point WITHOUT a loading screen. ONE record, NO
		// position (the client positions off the RespawnMe spawnPointId / its RDB). The PlayfieldProxy is pinned to
		// the CURRENT playfield: map + the player's own handle in slot3 + the ready flag + the LIVE playfield token in
		// slot7. CRITICAL: playfieldToken MUST be the token the client currently holds (Zone.PlayfieldToken, captured
		// from the burst-less world-entry snapshot - the same one SpawnPlayfieldRewriter stamps into rendered spawns),
		// NOT the MapWorldBurst token (ResolveMapPlayfieldToken): a mismatch makes the client treat it as a real
		// changemap and show a LOADING SCREEN. Decoded byte-for-byte from the beach resurrect (Stihetfv #1387, map
		// 0xFAA): trailing IIR bool = 1.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=01]
		//   [PlayfieldProxy 'b': [0]=0xC79C [1]=map [2]=0 [3]=playerHandle [4]=0 [5]=1 [6]=0x9C50 [7]=token, trailing 0]
		//   [u8 bool=1]
		public static byte[] BuildReviveTeleport(uint clientInstance, uint mapId, uint playerHandle, uint playfieldToken)
		{
			var s = new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.IntendedTeleportIIR)
				.WriteUInt32(ReceiverClass)
				.WriteUInt32(clientInstance)
				.WriteByte(0x01) // env
				.WriteByte(PlayfieldProxyVersion)
				.WriteUInt32(PlayfieldClassWord) // [0]
				.WriteUInt32(mapId) // [1] = the CURRENT map (same-map revive)
				.WriteUInt32(0) // [2]
				.WriteUInt32(playerHandle) // [3] = the reviving player (beach capture)
				.WriteUInt32(0) // [4]
				.WriteUInt32(ProxyReadyFlag) // [5] = 1 (instance ready)
				.WriteUInt32(ProxyInstanceWord) // [6] = 0x9C50
				.WriteUInt32(playfieldToken) // [7] = the LIVE playfield-instance token
				.WriteByte(0x00); // PlayfieldProxy trailing bool
			return s.WriteByte(0x01).ToArray(); // IntendedTeleportIIR trailing bool = 1
		}

		// The PlayfieldProxy stream (FUN_00a6a600): version 'b' + 8 u32 + a trailing bool. u32[0]=class word,
		// u32[1]=map; u32[5..7] pin the destination instance (zero in the "begin" body, set in the dest announce).
		private static FaolanStream WritePlayfieldProxy(FaolanStream s, uint mapId, uint slot5, uint slot6, uint slot7)
		{
			return s
				.WriteByte(PlayfieldProxyVersion)
				.WriteUInt32(PlayfieldClassWord) // [0]
				.WriteUInt32(mapId) // [1] destination map
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0) // [2..4]
				.WriteUInt32(slot5) // [5]
				.WriteUInt32(slot6) // [6]
				.WriteUInt32(slot7) // [7]
				.WriteByte(0x00); // trailing bool
		}
	}
}
