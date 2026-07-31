using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestFullUpdateIIR (0x63268258) - one quest's FULL journal entry, sent live on grant and on every re-display.
	//
	// Wire:  [subop][class][playerHandle][u32 0][u8 0x01]  <one QuestcRecord>  [u8 0]
	//
	// ⚠️ The payload IS a Quest_c journal record - the same 12 scalars, 5 text slots, 4 object lists and interleaved
	// @609 objective region the snapshot tails carry (QuestcRecordCodec). It was hand-rolled here under a different
	// set of names until #530 wave 5, which is why the two never lined up: the "level" is the record's `state` slot,
	// the "acceptTime" u64 is the goal record's reserved (70099, 4CC-tag) pair and not a time field at all (#537), and
	// the trailing "stat/objective run" is one QuestObjectiveGoal. Proven by the byte-exact Casilda golden, which the
	// composed build reproduces unchanged.
	//
	// This is also why the snapshot-tails per-quest block equals this body from the record onward, minus the trailing
	// zero byte: it is literally the same record.
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestFullUpdateCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.QuestFullUpdateIIR;

		// The per-quest LEVEL fallback. The level is a quest PROPERTY, CONSTANT per quest across its whole lifecycle
		// (every captured 13479 full-update uses 3, on grant and on every re-display: frames 687/8876/9947/10129). It
		// was earlier read as a "state/category" enum, but the harvest across faolan.db tracks LEVEL, not state
		// (Tortage starter 1/3/4/5, open-world 23/24, endgame 80; two quests in the same zone can be 23 and 80; never
		// the QuestIIR StateActive value 2). Passing the QuestIIR state (2) here makes the client silently DROP the
		// full-update (journal stays empty) - the bug that first surfaced this field. Now harvested per quest into
		// QuestDefinition.Level (#352); this const is the fallback for a quest with no captured full-update
		// (Level == 0) - 3 is Casilda 13479's level, a sane Tortage default.
		public const uint DefaultQuestLevel = 3;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestFullUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestFullUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestFullUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestFullUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestFullUpdateIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU32Be(0, "reserved word before the marker");
			r.ExpectU8(0x01, "record marker");

			var record = QuestcRecordCodec.Read(ref r);
			r.ExpectU8(0x00, "trailing byte");
			return new QuestFullUpdateIIR(handle, record);
		}

		internal static void Write(FaolanStream s, QuestFullUpdateIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteUInt32(0u).WriteByte((byte)0x01);
			QuestcRecordCodec.Write(s, v.Record);
			s.WriteByte((byte)0x00);
		}
	}

	// QuestFullUpdateIIR (0x63268258) - a player handle and the quest's journal record.
	public sealed record QuestFullUpdateIIR(uint PlayerHandle, QuestcRecord Record);
}
