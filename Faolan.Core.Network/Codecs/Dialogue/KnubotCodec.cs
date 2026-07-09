using System.Collections.Generic;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
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
		private const ushort Version = 1;

		// KnubotAnswerListIIR_c (0xF164A4A5) + KnubotCloseChatWindowIIR_c (0xC4AF39EB) share one grammar (verified
		// 197/197 + 48/48): the KnubotBase header then `[u32 count][ count x ([u16 len][remoteformat string]) ]`. The
		// answer list is the screen's option buttons; the close-window is the same shape with a single empty
		// remoteformat (id="0"), retail's "nothing (more) to say" reply.
		public static byte[] BuildAnswerList(uint playerInstance, uint npcHandle, IReadOnlyList<byte[]> options, byte env = 0x00)
		{
			return BuildStringList((uint)GameServer0X2000Opcodes.KnubotAnswerListIIR, playerInstance, npcHandle, options, env);
		}

		// KnubotAppendTextIIR_c (0x4A3D899A) - an NPC dialogue line. The string is the `remoteformat` XML (the same
		// shape ChatText/Feedback use). [u32 field][u16 xmlLen][xml][u8 bool].
		public static byte[] BuildAppendText(uint playerInstance, uint npcHandle, byte[] xml, uint field = 1, byte trailing = 1, byte env = 0x00)
		{
			var c = Header((uint)GameServer0X2000Opcodes.KnubotAppendTextIIR, playerInstance, npcHandle, env).WriteUInt32(field).WriteUInt16(xml.Length).WriteArray(xml).WriteByte(trailing);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static byte[] BuildCloseChatWindow(uint playerInstance, uint npcHandle, IReadOnlyList<byte[]> options, byte env = 0x00)
		{
			return BuildStringList((uint)GameServer0X2000Opcodes.KnubotCloseChatWindowIIR, playerInstance, npcHandle, options, env);
		}

		// KnubotOpenChatWindowIIR_c (0x045B0B0F) - opens the dialogue window. A single trailing byte.
		public static byte[] BuildOpenChatWindow(uint playerInstance, uint npcHandle, byte value = 0x00, byte env = 0x00)
		{
			var c = Header((uint)GameServer0X2000Opcodes.KnubotOpenChatWindowIIR, playerInstance, npcHandle, env).WriteByte(value);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static byte[] BuildStringList(uint subOpcode, uint playerInstance, uint npcHandle, IReadOnlyList<byte[]> strings, byte env = 0x00)
		{
			var c = Header(subOpcode, playerInstance, npcHandle, env).WriteUInt32(strings.Count);
			foreach (var str in strings)
				c.WriteUInt16(str.Length).WriteArray(str);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static AppendText ParseAppendText(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32(); // len, subop
			var player = ReadHeader(s, out var env, out var npc);
			var field = s.ReadUInt32();
			var xml = s.ReadArrayUInt16Length();
			return new AppendText(player, npc, env, field, xml, s.ReadByte());
		}

		public static OpenChatWindow ParseOpenChatWindow(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32();
			var player = ReadHeader(s, out var env, out var npc);
			return new OpenChatWindow(player, npc, env, s.ReadByte());
		}

		public static StringList ParseStringList(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();
			s.ReadUInt32(); // len, subop
			var player = ReadHeader(s, out var env, out var npc);
			var count = s.ReadUInt32();
			var strings = new List<byte[]>((int)count);
			for (var i = 0; i < count; i++)
				strings.Add(s.ReadArrayUInt16Length());
			return new StringList(player, npc, env, strings);
		}

		private static FaolanStream Header(uint subOpcode, uint playerInstance, uint npcHandle, byte env)
		{
			return new FaolanStream()
				.WriteUInt32(subOpcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerInstance)
				.WriteByte(env)
				.WriteUInt16(Version)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(npcHandle);
		}

		// Reads the carrier marker + player instance, then the KnubotBase header (env, version, marker, npc handle).
		private static uint ReadHeader(FaolanStream s, out byte env, out uint npcHandle)
		{
			s.ReadUInt32(); // 0xC350 marker
			var player = s.ReadUInt32(); // player instance (carrier handle)
			env = s.ReadByte();
			s.ReadUInt16(); // version (1)
			s.ReadUInt32(); // 0xC350 marker
			npcHandle = s.ReadUInt32();
			return player;
		}

		public sealed record AppendText(uint PlayerInstance, uint NpcHandle, byte Env, uint Field, byte[] Xml, byte Trailing);

		public sealed record OpenChatWindow(uint PlayerInstance, uint NpcHandle, byte Env, byte Value);

		public sealed record StringList(uint PlayerInstance, uint NpcHandle, byte Env, IReadOnlyList<byte[]> Strings);
	}
}
