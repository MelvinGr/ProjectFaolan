using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Faolan.Core.Network.Packets
{
	// Decodes a captured conversation packet's raw Conan Data into the structured Knubot-screen columns (#188), so
	// the importer can populate ConversationPacket.{SubOpcode,Env,Field,Flag,ScreensJson} and the runtime builds the
	// screen from fields. Pure byte-slicing (no codec dependency); returns null for non-Knubot packets, which keep
	// replaying from Data. Mirrors KnubotCodec's framing: carrier = Data[12 + headerLen ..] = [u32 len][u32 sub]
	// [u32 0xC350][u32 instance][u8 env][u16 ver][u32 0xC350][u32 npc][ body ].
	public static class ConversationScreenDecoder
	{
		public const uint OpenChatWindow = 0x045B0B0F;
		public const uint AppendText = 0x4A3D899A;
		public const uint AnswerList = 0xF164A4A5;
		public const uint CloseChatWindow = 0xC4AF39EB;
		private const uint Marker = 0x0000C350;

		public sealed record Columns(uint SubOpcode, byte Env, uint? Field, byte? Flag, IReadOnlyList<string> Screens)
		{
			public string ScreensJson => JsonSerializer.Serialize(Screens);
		}

		public static Columns Decode(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;
			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
			var start = 12 + headerLen;
			if (start + 27 > data.Length)
				return null;

			uint U32(int rel) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + rel, 4));
			ushort U16(int rel) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + rel, 2));

			var sub = U32(4);
			if (sub is not (OpenChatWindow or AppendText or AnswerList or CloseChatWindow))
				return null;
			if (U32(8) != Marker)
				return null;

			var env = data[start + 16];
			var o = 27; // past [len][sub][marker][instance][env][ver][marker2][npc]
			uint? field = null;
			byte? flag = null;
			var screens = new List<string>();

			void ReadScreen()
			{
				var ln = U16(o);
				o += 2;
				screens.Add(Encoding.Latin1.GetString(data, start + o, ln));
				o += ln;
			}

			switch (sub)
			{
				case OpenChatWindow:
					flag = data[start + o];
					break;
				case AnswerList:
					var count = U32(o);
					o += 4;
					for (var i = 0; i < count; i++)
						ReadScreen();
					break;
				case AppendText:
					field = U32(o);
					o += 4;
					ReadScreen();
					flag = data[start + o];
					break;
				default: // CloseChatWindow
					field = U32(o);
					o += 4;
					ReadScreen();
					break;
			}

			return new Columns(sub, env, field, flag, screens);
		}
	}
}
