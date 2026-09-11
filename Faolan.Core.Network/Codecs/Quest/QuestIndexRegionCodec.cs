using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     The quest index region of the world-entry snapshot tails - the bytes between the completed-quest journal
	///     (<see cref="CompletedQuestJournalRegionCodec" />) and the access-window table.
	/// </summary>
	/// <remarks>
	///     Decoded byte-exact from the beach template, the two-quest capture and every progressed journal in the corpus
	///     (5 to 30 active quests), and carved to the client's own reader (MainQuest_c, FUN_00922210: five head words, the
	///     pair list, then a four-word trailer):
	///     <code>
	///     [ListFrame(heldQuests)]        the id-list framing
	///     [u32 questId x count]          the held quest ids
	///     [u32 count]                    a plain count, as the client's MainQuest_c list uses
	///     one goal-table block per held quest, in the same order as the ids:
	///       [u32 areaId]                 the quest's QuestLineId - the block's own first word
	///       [u32 magic]                  the area's goal-table name word ("W6QT", "RRL3", ...)
	///       [u32 stepNumber][u32 stepsAfter][u32 tf3]
	///                                    the held quest's 1-based step in its line and the number of steps after it,
	///                                    their sum being the line's length; tf3 is 0 across the corpus
	///       [u32 n][(questId, sequence) x n]   the area's full quest catalog
	///       [u32 trailerQuestId][u32 a][u32 b][u32 c]   trailerQuestId is the area's anchor quest
	///     </code>
	///     <para>
	///         The region has no leading tag word and no terminal word: an empty index is the empty-list frame followed by a
	///         zero count, and the region simply ends. The next bytes belong to the access-window table
	///         (PfAccessWindowEntryCodec), whose own frame word is easily mistaken for a terminator - it is constant only
	///         because every capture carries the same 86-entry catalog.
	///     </para>
	///     <para>
	///         Each block's leading word belongs to that block, not to the one before it; hanging it off the previous block
	///         manufactures a chain grammar that does not exist. The catalog's sequence is 1-based but not strictly
	///         monotonic, since branch and chapter quests repeat one, so it must be carried rather than regenerated.
	///     </para>
	/// </remarks>
	public static class QuestIndexRegionCodec
	{
		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(QuestIndexRegion value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestIndexRegion Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestIndexRegionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestIndexRegion Read(ref SpanReader r)
		{
			// The count is the frame's count: read exactly that many ids rather than peeking, so a held quest id below
			// 0x1000, or an area id above it, cannot mis-split the list.
			var declared = r.ReadListCount();
			var questIds = new List<uint>(declared);
			for (var i = 0; i < declared; i++)
				questIds.Add(r.ReadU32Be());

			var at = r.Position;
			var count = r.ReadU32Be();
			if (count != questIds.Count)
				throw CodecParseException.Unexpected(nameof(QuestIndexRegionCodec), at, $"count == the frame-derived {questIds.Count}", count.ToString());

			var blocks = new List<QuestIndexRegion.Block>((int)count);
			for (uint b = 0; b < count; b++)
			{
				var areaId = r.ReadU32Be();
				var magic = r.ReadU32Be();
				var f1 = r.ReadU32Be();
				var f2 = r.ReadU32Be();
				var f3 = r.ReadU32Be();

				var n = r.ReadU32Be();
				var areaQuestIds = new List<uint>((int)n);
				var areaQuestSequences = new List<uint>((int)n);
				for (uint i = 0; i < n; i++)
				{
					areaQuestIds.Add(r.ReadU32Be());
					areaQuestSequences.Add(r.ReadU32Be());
				}

				blocks.Add(new QuestIndexRegion.Block
				{
					AreaId = areaId,
					Magic = magic,
					StepNumber = f1,
					StepsAfter = f2,
					TableField3 = f3,
					AreaQuestIds = areaQuestIds,
					AreaQuestSequences = areaQuestSequences,
					TrailerQuestId = r.ReadU32Be(),
					TrailerA = r.ReadU32Be(),
					TrailerB = r.ReadU32Be(),
					TrailerC = r.ReadU32Be()
				});
			}

			return new QuestIndexRegion
			{
				QuestIds = questIds,
				Blocks = blocks
			};
		}

		internal static void Write(FaolanStream s, QuestIndexRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(ListFrame.ForCount(v.QuestIds.Count));
			foreach (var id in v.QuestIds)
				s.WriteUInt32(id);

			s.WriteUInt32((uint)v.Blocks.Count);

			foreach (var b in v.Blocks)
			{
				if (b.AreaQuestIds.Count != b.AreaQuestSequences.Count)
					throw new InvalidOperationException($"Block {b.TrailerQuestId}: {b.AreaQuestIds.Count} area quest ids but {b.AreaQuestSequences.Count} sequences.");

				s.WriteUInt32(b.AreaId);
				s.WriteUInt32(b.Magic);
				s.WriteUInt32(b.StepNumber);
				s.WriteUInt32(b.StepsAfter);
				s.WriteUInt32(b.TableField3);

				s.WriteUInt32((uint)b.AreaQuestIds.Count);
				for (var i = 0; i < b.AreaQuestIds.Count; i++)
				{
					s.WriteUInt32(b.AreaQuestIds[i]);
					s.WriteUInt32(b.AreaQuestSequences[i]);
				}

				s.WriteUInt32(b.TrailerQuestId);
				s.WriteUInt32(b.TrailerA);
				s.WriteUInt32(b.TrailerB);
				s.WriteUInt32(b.TrailerC);
			}
		}
	}

	/// <summary>The quest index: the held quest ids plus one area-catalog block per held quest.</summary>
	public sealed record QuestIndexRegion
	{
		public List<Block> Blocks { get; init; } = [];
		public List<uint> QuestIds { get; init; } = [];

		/// <summary>One held quest's area-catalog table - the client's MainQuest_c (FUN_00922210).</summary>
		public sealed record Block
		{
			/// <summary>The quest's QuestLineId, and the block's own first wire word.</summary>
			public uint AreaId { get; init; }

			/// <summary>
			///     The line's sequence-1 quest on every captured block. The client reader (FUN_00922210) matches it into the
			///     pair list and stores it at MainQuest_c +0x38, the line's current-quest slot - the same one
			///     MainQuestUpdateIIR's TrackedQuestId fills on the incremental path.
			/// </summary>
			public uint TrailerQuestId { get; init; }

			/// <summary>
			///     The area's full quest catalog, ordered by each quest's JournalSequence, and not this quest's goal ids:
			///     quest 10238's list is [10238, 12369, 12373, 10320, 12371, 10286, 10287, 10915], exactly area 241's eight
			///     quests, while its own goals are [1448, 3490]. A fresh character with no completed quests carries the whole
			///     list, so it is static area data rather than journal-derived.
			/// </summary>
			/// <remarks>
			///     On the wire each entry is a (questId, sequence) pair. AreaQuestSequences holds the sequence parallel to
			///     AreaQuestIds; the sequence is 1-based but not strictly monotonic, since branch and chapter quests repeat
			///     one, so it must be carried rather than regenerated from the position. Write requires the two lists to be
			///     the same length.
			/// </remarks>
			public List<uint> AreaQuestIds { get; init; } = [];
			public List<uint> AreaQuestSequences { get; init; } = [];
			public uint Magic { get; init; } // the area's goal-table name word (W6QT / RRL3 / ...)

			/// <summary>
			///     MainQuest_c +0x30 and +0x34: the held quest's 1-based step number within its line, and the number of steps
			///     after it. Their sum is AreaQuestIds.Count, and on every one of the 2,626 captured blocks StepNumber is
			///     exactly the held quest's own AreaQuestSequences entry.
			/// </summary>
			/// <remarks>
			///     The reader is the journal's line panel (FUN_01617260): it takes +0x30 as a running counter, formats it
			///     through the Roman-numeral converter (FUN_010cfbc0, the chapter numerals the journal shows) for the current
			///     step, then draws +0x34 more placeholder rows for the steps still to come, hiding that section when +0x34 is
			///     zero.
			///     <para>
			///         These are the only two words in the block that are per-character progress rather than line constants -
			///         the beach line alone shows 1, 3, 5 and 8 across characters - so generation derives them from the held
			///         quest's JournalSequence (QuestIndexRegions.ForQuests).
			///     </para>
			/// </remarks>
			public uint StepNumber { get; init; }
			public uint StepsAfter { get; init; }

			/// <summary>
			///     MainQuest_c +0x44, the same slot MainQuestUpdateIIR.Unknown1 lands in: the IIR path routes it through the
			///     FUN_00921980 constructor's third argument while the snapshot filler FUN_00922210 stores this word there.
			///     It is stored, written back out by MainQuest_c::WriteToStream and read by nothing; 0 on all 97 captured
			///     blocks and all 46 captured IIR lists.
			/// </summary>
			public uint TableField3 { get; init; }

			/// <summary>
			///     The block's trailer words after the catalog: [trailerQuestId][a][b][c]. Each is a MainQuest_c field
			///     (FUN_00922210 stores them at +0x38, +0x48, +0x4c and +0x50), and the last three are the slots the
			///     incremental MainQuestUpdateIIR fills for the same line: TrailerA is its Unknown0 and TrailerC its Unknown2.
			///     The IIR carries no TrailerB, the wire constructor parking the area token there.
			/// </summary>
			/// <remarks>
			///     The per-field facts live with <see cref="MainQuestUpdateIIR" />: TrailerA is the line's solo (1), raid (4)
			///     or group-dungeon (8) category, consumed as the quest tracker's sort tiebreaker; TrailerB is 0 on every
			///     captured block; TrailerC is a per-line flag, set on 6 of 97 captured lines, and the same flag the completed
			///     journal's line group carries as CompletedQuestJournalRegion.AreaGroup.LineFlag, the two agreeing on every
			///     line seen in both. All three are constant per line rather than per quest, and are carried rather than
			///     assumed.
			/// </remarks>
			public uint TrailerA { get; init; } = 1;
			public uint TrailerB { get; init; }
			public uint TrailerC { get; init; }
		}
	}
}
