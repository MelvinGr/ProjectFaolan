using System.Collections.Generic;
using Faolan.Core.Data;
using Faolan.Core.Network;

namespace Faolan.Core.Network.Packets
{
	// Builds/parses the four Knubot dialogue IIRs the GameServer pushes to drive an NPC conversation window
	// (#188), replacing the verbatim ConversationPacket replay. Each is an IIR RECORD: [u32 len][subop][body],
	// where len = bytes after the len field (same framing as ChatTextCodec). All big-endian.
	//
	// Shared 19-byte header after the sub-opcode:
	//   [u32 0x0000C350]  IIR marker (Character class)
	//   [u32 instance]    the conversing NPC's live handle/instance
	//   [u16 0x0000][u8 0x01]   consts
	//   [u32 0x0000C350]  marker2
	//   [u32 convId]      conversation/knubot object id
	// then per type:
	//   OpenChatWindow  0x045B0B0F : [u8 flag]
	//   AppendText      0x4A3D899A : [u32 field][u16 xmlLen][remoteformat][u8 flag]   (one NPC line + continue flag)
	//   CloseChatWindow 0xC4AF39EB : [u32 field][u16 xmlLen][remoteformat]            (one farewell screen)
	//   AnswerList      0xF164A4A5 : [u32 count]( [u16 xmlLen][remoteformat] )*count  (the answer options)
	//
	// The u32 after the header is the ANSWER COUNT for AnswerList; for Append/Close it's an opaque scalar (almost
	// always 1) carried as `Field`. Decoded in Captures/scripts/decode_knubot.py (307 rows + 565 capture packets);
	// golden-tested in KnubotConversationCodecTests.
	public static class KnubotConversationCodec
	{
		public const uint OpenChatWindow = 0x045B0B0F;
		public const uint AppendText = 0x4A3D899A;
		public const uint AnswerList = 0xF164A4A5;
		public const uint CloseChatWindow = 0xC4AF39EB;

		private const uint IirMarker = EntityClass.Character; // 0x0000C350
		private const ushort HeaderConstU16 = 0x0000;
		private const byte HeaderConstU8 = 0x01;

		public sealed record Parsed(
			uint SubOpcode, uint Instance, uint ConvId, uint? Field, byte? Flag, IReadOnlyList<byte[]> Screens);

		public static byte[] BuildOpen(uint instance, uint convId, byte flag) =>
			Frame(Header(OpenChatWindow, instance, convId).WriteByte(flag));

		public static byte[] BuildAppendText(uint instance, uint convId, byte[] xml, uint field = 1, byte flag = 1)
		{
			var c = Header(AppendText, instance, convId).WriteUInt32(field);
			WriteScreen(c, xml);
			return Frame(c.WriteByte(flag));
		}

		public static byte[] BuildCloseChatWindow(uint instance, uint convId, byte[] xml, uint field = 1)
		{
			var c = Header(CloseChatWindow, instance, convId).WriteUInt32(field);
			WriteScreen(c, xml);
			return Frame(c);
		}

		public static byte[] BuildAnswerList(uint instance, uint convId, IReadOnlyList<byte[]> answers)
		{
			var c = Header(AnswerList, instance, convId).WriteUInt32((uint)answers.Count);
			foreach (var a in answers)
				WriteScreen(c, a);
			return Frame(c);
		}

		public static Parsed Parse(byte[] record)
		{
			var s = new FaolanStream(record);
			s.ReadUInt32();                // len
			var subop = s.ReadUInt32();
			s.ReadUInt32();                // marker
			var instance = s.ReadUInt32();
			s.ReadUInt16();                // const u16
			s.ReadArray(1);                // const u8
			s.ReadUInt32();                // marker2
			var convId = s.ReadUInt32();

			uint? field = null;
			byte? flag = null;
			var screens = new List<byte[]>();
			switch (subop)
			{
				case OpenChatWindow:
					flag = s.ReadArray(1)[0];
					break;
				case AnswerList:
					var count = s.ReadUInt32();
					for (var i = 0; i < count; i++)
						screens.Add(s.ReadArray(s.ReadUInt16()));
					break;
				case AppendText:
					field = s.ReadUInt32();
					screens.Add(s.ReadArray(s.ReadUInt16()));
					flag = s.ReadArray(1)[0];
					break;
				case CloseChatWindow:
					field = s.ReadUInt32();
					screens.Add(s.ReadArray(s.ReadUInt16()));
					break;
			}

			return new Parsed(subop, instance, convId, field, flag, screens);
		}

		private static FaolanStream Header(uint subop, uint instance, uint convId)
		{
			var s = new FaolanStream();
			s.WriteUInt32(subop)
				.WriteUInt32(IirMarker)
				.WriteUInt32(instance)
				.WriteUInt16(HeaderConstU16)
				.WriteByte(HeaderConstU8)
				.WriteUInt32(IirMarker)
				.WriteUInt32(convId);
			return s;
		}

		private static void WriteScreen(FaolanStream s, byte[] xml) =>
			s.WriteUInt16((ushort)xml.Length).WriteArray(xml);

		// Wrap the IIR body in the [u32 len][body] record framing.
		private static byte[] Frame(FaolanStream content)
		{
			var body = content.ToArray();
			var s = new FaolanStream();
			s.WriteUInt32((uint)body.Length).WriteArray(body);
			return s.ToArray();
		}
	}
}
