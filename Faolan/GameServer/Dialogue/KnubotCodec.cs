using System;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Dialogue
{
	// Construct-side codecs for the server->client Knubot conversation records (#188, docs/iir-readers-conversation-misc.md).
	// The Knubot carrier nests BOTH conversation endpoints: the player is the IIR carrier handle (`instance`), and the
	// KnubotBaseIIR_c header (FUN_009a7e00) carries the version + the NPC endpoint:
	//   [u32 len][u32 sub][u32 0xC350][u32 playerInstance]
	//   [u8 env][u16 version=1][u32 0xC350][u32 npcHandle]            <- KnubotBaseIIR_c header
	//   [ type-specific body ]
	// Big-endian; golden-tested byte-exact in KnubotCodecTests.
	public static class KnubotCodec
	{
		private const uint IirMarker = EntityClass.Character;
		private const ushort Version = 1;

		private static FaolanStream Header(uint subOpcode, uint playerInstance, uint npcHandle, byte env) =>
			new FaolanStream()
				.WriteUInt32(subOpcode)
				.WriteUInt32(IirMarker)
				.WriteUInt32(playerInstance)
				.WriteByte(env)
				.WriteUInt16(Version)
				.WriteUInt32(IirMarker)
				.WriteUInt32(npcHandle);

		// KnubotAppendTextIIR_c (0x4A3D899A) - an NPC dialogue line. The string is the `remoteformat` XML (the same
		// shape ChatText/Feedback use). [u32 field][u16 xmlLen][xml][u8 bool].
		public static byte[] BuildAppendText(uint playerInstance, uint npcHandle, byte[] xml,
			uint field = 1, byte trailing = 1, byte env = 0x00)
		{
			var c = Header((uint)GameServer0X2000RespondsOpcodes.KnubotAppendTextIIR, playerInstance, npcHandle, env)
				.WriteUInt32(field)
				.WriteUInt16((ushort)xml.Length)
				.WriteArray(xml)
				.WriteByte(trailing);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public sealed record AppendText(uint PlayerInstance, uint NpcHandle, byte Env, uint Field, byte[] Xml, byte Trailing);

		public static AppendText ParseAppendText(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32(); s.ReadUInt32();                     // len, subop
			var player = ReadHeader(s, out var env, out var npc);
			var field = s.ReadUInt32();
			var xml = s.ReadArrayUInt16Length();
			return new AppendText(player, npc, env, field, xml, s.ReadByte());
		}

		// KnubotOpenChatWindowIIR_c (0x045B0B0F) - opens the dialogue window. A single trailing byte.
		public static byte[] BuildOpenChatWindow(uint playerInstance, uint npcHandle, byte value = 0x00, byte env = 0x00)
		{
			var c = Header((uint)GameServer0X2000RespondsOpcodes.KnubotOpenChatWindowIIR, playerInstance, npcHandle, env)
				.WriteByte(value);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public sealed record OpenChatWindow(uint PlayerInstance, uint NpcHandle, byte Env, byte Value);

		public static OpenChatWindow ParseOpenChatWindow(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32(); s.ReadUInt32();
			var player = ReadHeader(s, out var env, out var npc);
			return new OpenChatWindow(player, npc, env, s.ReadByte());
		}

		// Reads the carrier marker + player instance, then the KnubotBase header (env, version, marker, npc handle).
		private static uint ReadHeader(FaolanStream s, out byte env, out uint npcHandle)
		{
			s.ReadUInt32();                 // 0xC350 marker
			var player = s.ReadUInt32();    // player instance (carrier handle)
			env = s.ReadByte();
			s.ReadUInt16();                 // version (1)
			s.ReadUInt32();                 // 0xC350 marker
			npcHandle = s.ReadUInt32();
			return player;
		}
	}
}
