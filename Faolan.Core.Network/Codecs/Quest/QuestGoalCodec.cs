using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     QuestGoalIIR - one objective's progress, 33 bytes:
	///     <c>[subop][class][playerHandle][u8 0x01][u32 questId][u32 goalId][u32 w1][u32 w2][u32 w3]</c>.
	/// </summary>
	/// <remarks>
	///     The three trailing words are one value, the update kind, not a count plus padding. A single-count goal sends
	///     (1, 0, 0), which is 52 of the 66 QuestGoalIIR packets in the corpus, so the triple reads as a count until the
	///     multi-count collect goal shows the other two spellings. A triple retail never sends is rejected rather than read
	///     as a count.
	///     <para>The u32 length prefix belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class QuestGoalCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestGoalIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(QuestGoalIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint playerHandle, uint questId, uint goalId, QuestGoalUpdate update)
		{
			return Build(new QuestGoalIIR(playerHandle, questId, goalId, update));
		}

		public static QuestGoalIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestGoalCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestGoalIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestGoalIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU8(0x01, "record marker");
			var questId = r.ReadU32Be();
			var goalId = r.ReadU32Be();

			var at = r.Position;
			var words = (r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
			foreach (var kind in new[] { QuestGoalUpdate.Satisfied, QuestGoalUpdate.Progress, QuestGoalUpdate.Advanced })
			{
				if (words == UpdateWords(kind))
					return new QuestGoalIIR(handle, questId, goalId, kind);
			}

			throw CodecParseException.Unexpected(nameof(QuestGoalCodec), at, "one of the three update triples retail sends", $"({words.Item1}, {words.Item2}, {words.Item3})");
		}

		internal static void Write(FaolanStream s, QuestGoalIIR v)
		{
			var (w1, w2, w3) = UpdateWords(v.Update);
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteByte((byte)0x01).WriteUInt32(v.QuestId).WriteUInt32(v.GoalId).WriteUInt32(w1).WriteUInt32(w2)
				.WriteUInt32(w3);
		}

		/// <summary>The only three word-triples retail sends. One table, so the build and parse sides cannot drift.</summary>
		private static (uint W1, uint W2, uint W3) UpdateWords(QuestGoalUpdate update)
		{
			return update switch
			{
				QuestGoalUpdate.Satisfied => (1u, 0u, 0u),
				QuestGoalUpdate.Progress => (2u, 0u, 1u),
				QuestGoalUpdate.Advanced => (3u, 2u, 0u),
				_ => throw new ArgumentOutOfRangeException(nameof(update), update, "unknown QuestGoalIIR update kind")
			};
		}
	}

	/// <summary>The QuestGoalIIR body.</summary>
	public sealed record QuestGoalIIR(uint PlayerHandle, uint QuestId, uint GoalId, QuestGoalUpdate Update);
}
