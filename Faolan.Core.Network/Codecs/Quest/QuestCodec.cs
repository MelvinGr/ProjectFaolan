using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestIIR (0x69B4B2C0, 29 B) - add/update one quest in the player's journal.
	//   [subop][class][playerHandle][u32 0][u8 0x01][u32 state][u32 questId][u32 0]
	//
	// On completion retail sends this TWICE for the quest: state 2 (active/update) then state 4 (completed) - see the
	// Stihetfv compeltefirstquest capture (13479 idx858/860).
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestIIR;

		// Quest state codes, from the capture.
		public const uint StateActive = 2;
		public const uint StateCompleted = 4;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint playerHandle, uint questId, uint state)
		{
			return Build(new QuestIIR(playerHandle, questId, state));
		}

		public static QuestIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU32Be(0, "reserved word before the marker");
			r.ExpectU8(0x01, "record marker");
			var state = r.ReadU32Be();
			var questId = r.ReadU32Be();
			r.ExpectU32Be(0, "trailing reserved word");
			return new QuestIIR(handle, questId, state);
		}

		internal static void Write(FaolanStream s, QuestIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteUInt32(0u).WriteByte((byte)0x01).WriteUInt32(v.State).WriteUInt32(v.QuestId).WriteUInt32(0u);
		}
	}

	// QuestIIR (0x69B4B2C0).
	public sealed record QuestIIR(uint PlayerHandle, uint QuestId, uint State);
}
