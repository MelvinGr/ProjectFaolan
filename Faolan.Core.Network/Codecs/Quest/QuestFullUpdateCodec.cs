using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     QuestFullUpdateIIR - one quest's full journal entry, sent live on grant and on every re-display. The body is
	///     <c>[subop][class][playerHandle][u32 0][u8 0x01]</c>, one QuestcRecord, then a zero byte.
	/// </summary>
	/// <remarks>
	///     The payload is a Quest_c journal record: the same 12 scalars, 5 text slots, 4 object lists and interleaved @609
	///     objective region the snapshot tails carry (see <see cref="QuestcRecordCodec" />), which is why the snapshot
	///     tails' per-quest block equals this body from the record onward, minus the trailing zero byte. Proven by the
	///     byte-exact Casilda golden.
	///     <para>The u32 length prefix belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class QuestFullUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestFullUpdateIIR;

		/// <summary>
		///     The per-quest level fallback, for a quest with no captured full-update (Level == 0). It is Casilda 13479's
		///     level, a sane Tortage default; harvested levels live in QuestDefinition.Level.
		/// </summary>
		/// <remarks>
		///     The field is the quest's level and is constant per quest across its whole lifecycle - every captured 13479
		///     full-update carries 3, on grant and on every re-display (frames 687, 8876, 9947, 10129) - and the harvest
		///     across the database tracks a level rather than a state: Tortage starters 1/3/4/5, open world 23/24, endgame
		///     80, with two quests in the same zone able to be 23 and 80. Passing the QuestIIR state here instead makes the
		///     client silently drop the full-update and leave the journal empty.
		/// </remarks>
		public const uint DefaultQuestLevel = 3;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The QuestFullUpdateIIR body: a player handle and the quest's journal record.</summary>
	public sealed record QuestFullUpdateIIR(uint PlayerHandle, QuestcRecord Record);
}
