using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// Lifts a captured conversation PACKET (rpcHeader included) into the structured Knubot-screen columns (#188), so
	// the importer can populate ConversationPacket.{SubOpcode,Env,Field,Flag} + the Screens rows (each decoded here
	// as raw remoteformat XML, columnized by ConversationScreenCodec.Parse) and the runtime builds the screen from
	// fields.
	//
	// ⚠️ NOT a message codec, and deliberately not on the #530 uniform surface: it is a FRAME-level scanner over
	// whole captured packets, so it owns the rpcHeader (§9 D keeps frame code out of the codec shape) and it must be
	// able to say "this packet is not a conversation packet" while sweeping a mixed capture. It no longer DECODES
	// anything itself - the bodies go through KnubotCodec / SetDialogueCameraCodec, which is what retires the
	// duplicate Knubot HandledTypes declaration this file used to carry (#530 §3). Its own conversion rides wave 8
	// with the rest of the importer's reading.
	//
	// The non-Knubot packets in a conversation node (~48 of 355 in faolan.db) are NOT dialogue text - they are
	// DispatchSetDialogueCamera (GameCharAgent 0xA9ECB95D -> GameCharInterface 0x8B87F791): the
	// cinematic conversation camera. No remoteformat text, so nothing to structure.
	public static class ConversationScreenDecoder
	{
		public const uint AnswerList = (uint)IirTypes.KnubotAnswerListIIR; // 0xF164A4A5
		public const uint AppendText = (uint)IirTypes.KnubotAppendTextIIR; // 0x4A3D899A
		public const uint CloseChatWindow = (uint)IirTypes.KnubotCloseChatWindowIIR; // 0xC4AF39EB
		public const uint OpenChatWindow = (uint)IirTypes.KnubotOpenChatWindowIIR; // 0x045B0B0F
		public const uint SetDialogueCamera = SetDialogueCameraCodec.Method; // an RPC method, not an IIR sub

		// The same method as the two bytes that END the rpcHeader - the method field's tag 0x20 followed by its
		// varint, whose whole of a sub-0x80 index is one group. This is the one place the decoder needs that view,
		// because it scans the frame by offset instead of parsing it; `SetDialogueCamera` above is the method itself,
		// which is what ConversationPacket.SubOpcode stores (#615).
		private static readonly ushort SetDialogueCameraWireOpcode = WireOpcode.Of(SetDialogueCameraCodec.Method);

		public static Columns? Decode(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;

			var headerLen = (int)data.ReadU32Be(8);
			var start = 12 + headerLen;
			if (start + 8 > data.Length)
				return null;

			var sub = data.ReadU32Be(start + 4);
			if (sub is not (OpenChatWindow or AppendText or AnswerList or CloseChatWindow))
				return DecodeCamera(data, headerLen, start);

			// The carrier's [u32 len] is the frame's; everything after it is the IIR record the codec owns.
			KnubotIIR record;
			try
			{
				record = KnubotCodec.Parse(data.AsSpan(start + 4));
			}
			catch (CodecParseException)
			{
				return null; // a mixed-capture sweep: a body that is not a well-formed Knubot record is not ours
			}

			var senderInstance = RpcCallerInstance(data);

			return record switch
			{
				KnubotOpenChatWindowIIR open => new Columns((ConversationSubOpcode)sub, open.Env, null, open.Value, [], senderInstance),
				KnubotAppendTextIIR text => new Columns((ConversationSubOpcode)sub, text.Env, text.TextType, text.Trailing, [Latin1(text.Xml)], senderInstance),
				KnubotAnswerListIIR list => new Columns((ConversationSubOpcode)sub, list.Env, null, null, list.Strings.Select(Latin1).ToList(), senderInstance),

				// The close's stored Field is its TextType, the same slot AppendText's is (#809). It used to be read
				// as the string COUNT of a list that is not there; the two agree on every s2c record, which all carry
				// TextType 1 and one screen, so a re-import produces the same rows.
				KnubotCloseChatWindowIIR close => new Columns((ConversationSubOpcode)sub, close.Env, close.TextType, null, [Latin1(close.Text)], senderInstance),
				_ => null
			};
		}

		// DispatchSetDialogueCamera (a method in the rpcHeader, not an IIR sub). We store the GameCharAgent
		// sender instance (varies per conversation) + the typed camera args; the npc/player are re-addressed live at
		// send time.
		private static Columns? DecodeCamera(byte[] data, int headerLen, int start)
		{
			// the method's two wire bytes end the rpcHeader (at headerLen-2 from the header start @ offset 12).
			if (10 + headerLen + 2 > data.Length || data.ReadU16Be(10 + headerLen) != SetDialogueCameraWireOpcode)
				return null;

			try
			{
				var camera = SetDialogueCameraCodec.Parse(data.AsSpan(start));

				return new Columns((ConversationSubOpcode)SetDialogueCamera, null, null, null, null, RpcCallerInstance(data), camera.Camera);
			}
			catch (CodecParseException)
			{
				return null;
			}
		}

		private static string Latin1(byte[] xml)
		{
			return Encoding.Latin1.GetString(xml);
		}

		// The caller endpoint instance from the rpcHeader: [0x0A len][0x0D class:u32][0x10 instance:varint].
		private static uint RpcCallerInstance(byte[] data)
		{
			if (data.Length < 14 || data[12] != 0x0A)
				return 0;

			var i = 14; // past 0x0A, len
			if (data[i] != 0x0D)
				return 0;

			i += 5; // past 0x0D + class u32
			if (data[i] != 0x10)
				return 0;

			i++;
			uint inst = 0;
			var shift = 0;
			while (i < data.Length)
			{
				var x = data[i++];
				inst |= (uint)(x & 0x7F) << shift;
				if ((x & 0x80) == 0)
					break;

				shift += 7;
			}

			return inst;
		}

		// The decoded columns of one conversation packet. Screens is the raw remoteformat screen XML list (the
		// importer columnizes each into ConversationScreen rows via ConversationScreenColumns.ToStoredRow).
		// SenderInstance is the rpcHeader caller endpoint instance (retail varies it per conversation), stored so the
		// send path re-frames from columns instead of the raw Data blob; Camera holds the SetDialogueCamera
		// arguments (#410), null on every Knubot packet.
		public sealed record Columns(ConversationSubOpcode SubOpcode, byte? Env, uint? Field, byte? Flag, IReadOnlyList<string>? Screens, uint? SenderInstance = null,
			CameraArgs? Camera = null);
	}
}
