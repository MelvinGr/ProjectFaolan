using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.FullCharSnapshot
{
	// The QUEST INDEX region of the 0x737A6DF9 tails - the bytes between the @609 objective region (see
	// QuestObjectiveRegion) and the access-window table. Two parts (decoded from the beach template + the 2-quest
	// capture #255, validated byte-exact by the round-trip test):
	//   A) the quest index:  [0x000003F1 tag][u32 marker=(count+1)*0x3F1][questId × count][u32 count][u32 sentinel=0xF1]
	//   B) ONE goal-id BLOCK per held quest, in the same order as the index ids:
	//        [u32 magic (the quest's goal-table NAME word: beach "W6QT" 0x57365154, Casilda 13479 0x52524C33)]
	//        [u32 tf1][u32 tf2][u32 tf3][u32 n][ (goalId, 1..n) × n ]
	//        [u32 trailerQuestId][1][0][0][u32 chainWord]
	// The (goalId, idx) pairs map the quest's objective/step ids to the client's 1..n goal index. The block trailers
	// CHAIN: each carries the NEXT held quest's AreaId, the last carries the primary (tracked) quest's IndexTrailerWord.
	// Typed codec (Parse/ToArray round-trips byte-exact - QuestIndexRegionTests single-quest, BuildQuestPortionTests
	// 2-quest) so FullCharSnapshotSubClass models it instead of the opaque blob (#186/#304). Build generates it from the held
	// QuestDefinitions (their IndexMagic/IndexTableField/IndexGoalSteps/AreaId/IndexTrailerWord seeds).
	public sealed class QuestIndexRegion : ICodec<QuestIndexRegion>
	{
		public const uint IndexTag = 0x000003F1;
		public const uint Sentinel = 0x000000F1;
		private const uint MinQuestId = 0x1000;

		public uint Marker { get; init; } = 0x7E2;
		public List<uint> QuestIds { get; init; } = [];
		public List<Block> Blocks { get; init; } = [];

		// One held quest's goal-id table + chained trailer.
		public sealed class Block
		{
			public uint Magic { get; init; } // the quest's goal-table name word (W6QT / Casilda / ...)
			public uint TableField1 { get; init; }
			public uint TableField2 { get; init; }
			public uint TableField3 { get; init; }
			public List<uint> GoalSteps { get; init; } = [];
			public uint TrailerQuestId { get; init; }
			public uint ChainWord { get; init; } // next held quest's AreaId, or the primary's IndexTrailerWord (last)
		}

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			Write(w);
			return w.ToArray();
		}

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(IndexTag);
			w.WriteUInt32(Marker);
			foreach (var id in QuestIds)
				w.WriteUInt32(id);
			w.WriteUInt32((uint)QuestIds.Count);
			w.WriteUInt32(Sentinel);

			foreach (var b in Blocks)
			{
				w.WriteUInt32(b.Magic);
				w.WriteUInt32(b.TableField1);
				w.WriteUInt32(b.TableField2);
				w.WriteUInt32(b.TableField3);
				w.WriteUInt32((uint)b.GoalSteps.Count);
				for (var i = 0; i < b.GoalSteps.Count; i++)
				{
					w.WriteUInt32(b.GoalSteps[i]);
					w.WriteUInt32((uint)(i + 1));
				}

				w.WriteUInt32(b.TrailerQuestId);
				w.WriteUInt32(1);
				w.WriteUInt32(0);
				w.WriteUInt32(0);
				w.WriteUInt32(b.ChainWord);
			}
		}

		// Generates the index region for the held quests (already ordered - primary first). Each quest contributes a
		// goal-id block from its seeded index fields; the block trailers chain to the next quest's AreaId, terminating
		// on the primary's IndexTrailerWord. The marker/count/sentinel follow from the quest count. #186/#255/#304.
		public static QuestIndexRegion Build(IReadOnlyList<QuestDefinition> ordered, QuestDefinition primary)
		{
			ArgumentNullException.ThrowIfNull(ordered);
			ArgumentNullException.ThrowIfNull(primary);

			var blocks = new List<Block>(ordered.Count);
			for (var i = 0; i < ordered.Count; i++)
			{
				var q = ordered[i];
				blocks.Add(new Block
				{
					Magic = q.IndexMagic,
					TableField1 = q.IndexTableField1,
					TableField2 = q.IndexTableField2,
					TableField3 = q.IndexTableField3,
					GoalSteps = ParseGoalSteps(q.IndexGoalSteps),
					TrailerQuestId = q.QuestId,
					ChainWord = i + 1 < ordered.Count
						? ordered[i + 1].AreaId
						: primary.IndexTrailerWord
				});
			}

			return new QuestIndexRegion
			{
				Marker = IndexTag * (uint)(ordered.Count + 1),
				QuestIds = ordered.Select(q => q.QuestId).ToList(),
				Blocks = blocks
			};
		}

		public static List<uint> ParseGoalSteps(string csv)
		{
			return (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
		}

		// Offset-bounded wrapper for parsing the index as an embedded sub-region: reads it at `offset` and asserts it
		// lands exactly at `endOffset` (the access-window table start).
		public static QuestIndexRegion Parse(byte[] data, int offset, int endOffset)
		{
			ArgumentNullException.ThrowIfNull(data);
			using var r = new FaolanStream(data, false) { Position = checked((uint)offset) };
			var region = Read(r);
			if (r.Position != endOffset)
				throw new InvalidOperationException($"Quest index: parsed to {r.Position}, expected {endOffset}");
			return region;
		}

		public static QuestIndexRegion Read(FaolanStream r)
		{
			var tag = r.ReadUInt32();
			if (tag != IndexTag)
				throw new InvalidOperationException($"Quest index: tag 0x{tag:X} != 0x{IndexTag:X}");

			var marker = r.ReadUInt32();

			var questIds = new List<uint>();
			while (Peek(r) >= MinQuestId)
				questIds.Add(r.ReadUInt32());
			var count = r.ReadUInt32();
			if (count != questIds.Count)
				throw new InvalidOperationException($"Quest index: count {count} != ids {questIds.Count}");

			var sentinel = r.ReadUInt32();
			if (sentinel != Sentinel)
				throw new InvalidOperationException($"Quest index: sentinel 0x{sentinel:X} != 0x{Sentinel:X}");

			var blocks = new List<Block>((int)count);
			for (uint b = 0; b < count; b++)
			{
				var magic = r.ReadUInt32();
				var f1 = r.ReadUInt32();
				var f2 = r.ReadUInt32();
				var f3 = r.ReadUInt32();
				var n = r.ReadUInt32();
				var goalSteps = new List<uint>((int)n);
				for (uint i = 0; i < n; i++)
				{
					goalSteps.Add(r.ReadUInt32());
					var idx = r.ReadUInt32();
					if (idx != i + 1)
						throw new InvalidOperationException($"Quest index: goal idx {idx} != {i + 1}");
				}

				var trailerQuestId = r.ReadUInt32();
				r.ReadUInt32(); // 1
				r.ReadUInt32(); // 0
				r.ReadUInt32(); // 0
				var chainWord = r.ReadUInt32();

				blocks.Add(new Block
				{
					Magic = magic,
					TableField1 = f1,
					TableField2 = f2,
					TableField3 = f3,
					GoalSteps = goalSteps,
					TrailerQuestId = trailerQuestId,
					ChainWord = chainWord
				});
			}

			return new QuestIndexRegion
			{
				Marker = marker,
				QuestIds = questIds,
				Blocks = blocks
			};
		}

		// Peeks the next big-endian word without advancing (the questId list runs until the first sub-MinQuestId word).
		private static uint Peek(FaolanStream r)
		{
			var p = r.Position;
			var v = r.ReadUInt32();
			r.Position = p;
			return v;
		}
	}
}
