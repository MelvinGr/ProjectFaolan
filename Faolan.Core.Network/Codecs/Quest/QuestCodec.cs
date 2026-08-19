using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestIIR (0x97E02314, 29 B) - add/update one quest in the player's journal.
	//   [subop][class][playerHandle][u32 0][u8 0x01][u32 state][u32 questId][u32 0]
	//
	// On completion retail sends this TWICE for the quest: state 2 (active/update) then state 4 (completed) - see the
	// Stihetfv compeltefirstquest capture (13479 idx858/860).
	//
	// ⚠️ The header used to cite 0x69B4B2C0, which is not this message and is not any message - the value appears
	// nowhere else in the tree and in zero of the corpus's 559,972 packets. `IirTypes.QuestIIR` was always 0x97E02314;
	// only the comment was wrong, which is why nothing failed.
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestIIR;

		// Quest state codes, from the capture.
		//
		// ⚠️ `StateCompleted` is only "completed" going DOWN. The client sends this same record UP to abandon a quest -
		// `QuestIIR_c` is its `DeleteQuest` method - and over the whole corpus the c2s direction carries state 4 and
		// nothing else: 3 c2s records against 50 s2c completions and 47 s2c updates. So the code is the message's
		// SLOT, not its meaning, and a reader that ignores direction turns a deletion into its opposite.
		// `GameServerListener.Inbound`'s QuestIIR case is the one that has to make that distinction.
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

	// QuestIIR (0x97E02314).
	public sealed record QuestIIR(uint PlayerHandle, uint QuestId, uint State);
}
