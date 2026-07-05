using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core;

namespace Faolan.GameServer.FullCharSnapshot
{
	// The QUEST INDEX region of the 0x737A6DF9 tails - the bytes between the @609 objective region (see
	// QuestObjectiveRegion) and the access-window table. Two parts (decoded from the template, validated by the
	// round-trip test):
	//   A) the quest index:  [0x000003F1 tag][u32 marker=0x7E2][questId × count][u32 count][u32 sentinel=0xF1]
	//   B) the goal-id table: [0x57365154 "W6QT"][u32 a=1][u32 b=7][u32 c=0][u32 n][ (goalId, 1..n) × n ] then a
	//      fixed 5-word trailer ([questId][1][0][0][u32]).
	// The (goalId, idx) pairs map the quest's objective/step ids to 1..n (the client's goal index). count == the
	// number of indexed quests; n == the number of goal steps. Carried as a typed codec (Parse/ToArray round-trips
	// byte-exact, QuestIndexRegionTests) so SnapshotTails models it instead of the opaque blob (#186). GENERATING it
	// for arbitrary quests needs the per-quest goal-step ids + trailer word in the DB (a follow-up seed, like the
	// per-goal model); the index/count/sentinel parts already follow from the character's quest list.
	public sealed class QuestIndexRegion
	{
		public const uint IndexTag = 0x000003F1;
		public const uint Sentinel = 0x000000F1;
		public const uint TableMagic = 0x57365154; // "W6QT"
		private const uint MinQuestId = 0x1000;
		public byte[] Trailer { get; init; } = [];
		public List<uint> GoalIds { get; init; } = [];
		public List<uint> QuestIds { get; init; } = [];

		public uint Marker { get; init; } = 0x7E2;

		// The W6QT goal-id table's three lead words (1 / 7 / 0 in the template) + the goal-step ids (their index is the
		// 1-based position) + the fixed trailer carried verbatim.
		public uint TableField1 { get; init; }
		public uint TableField2 { get; init; }
		public uint TableField3 { get; init; }

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(IndexTag);
			w.WriteUInt32(Marker);
			foreach (var id in QuestIds)
				w.WriteUInt32(id);
			w.WriteUInt32(QuestIds.Count);
			w.WriteUInt32(Sentinel);

			w.WriteUInt32(TableMagic);
			w.WriteUInt32(TableField1);
			w.WriteUInt32(TableField2);
			w.WriteUInt32(TableField3);
			w.WriteUInt32(GoalIds.Count);
			for (var i = 0; i < GoalIds.Count; i++)
			{
				w.WriteUInt32(GoalIds[i]);
				w.WriteUInt32(i + 1);
			}

			w.WriteArray(Trailer);
			return w.ToArray();
		}

		// GENERATES the index region from the indexed quest ids + the per-quest goal-step ids (+ the W6QT table's
		// three lead words and the trailer word), synthesizing the constants (tag/sentinel/magic, the 1..n goal
		// indices, the trailer's [1][0][0]). The MARKER is DERIVED from the quest count: it is the `0x3F1` list-framing
		// word `0x3F1 * (count + 1)` (decoded across captures: 1 quest -> 0x7E2, 2 quests -> 0xBD3 - the same
		// record-count invariant the client asserts), NOT a per-quest constant. The quest ids / count / sentinel follow
		// from the character's quest list; the goal-step ids + trailer word are per-quest data (seeded). #186.
		public static QuestIndexRegion Build(IReadOnlyList<uint> questIds, IReadOnlyList<uint> goalSteps, uint tableField1, uint tableField2, uint tableField3, uint trailerQuestId, uint trailerWord)
		{
			ArgumentNullException.ThrowIfNull(questIds);
			ArgumentNullException.ThrowIfNull(goalSteps);
			var trailer = new FaolanStream().WriteUInt32(trailerQuestId).WriteUInt32(1).WriteUInt32(0).WriteUInt32(0).WriteUInt32(trailerWord).ToArray();
			return new QuestIndexRegion
			{
				Marker = IndexTag * (uint)(questIds.Count + 1),
				QuestIds = [.. questIds],
				TableField1 = tableField1,
				TableField2 = tableField2,
				TableField3 = tableField3,
				GoalIds = [.. goalSteps],
				Trailer = trailer
			};
		}

		public static QuestIndexRegion Parse(byte[] data, int offset, int endOffset)
		{
			ArgumentNullException.ThrowIfNull(data);
			using var r = new FaolanStream(data, false) { Position = checked((uint)offset) };

			var tag = r.ReadUInt32();
			if (tag != IndexTag)
				throw new InvalidOperationException($"Quest index: tag 0x{tag:X} != 0x{IndexTag:X}");

			var marker = r.ReadUInt32();

			var questIds = new List<uint>();
			while (PeekUInt32(data, r.Position) >= MinQuestId)
				questIds.Add(r.ReadUInt32());
			var count = r.ReadUInt32();
			if (count != questIds.Count)
				throw new InvalidOperationException($"Quest index: count {count} != ids {questIds.Count}");

			var sentinel = r.ReadUInt32();
			if (sentinel != Sentinel)
				throw new InvalidOperationException($"Quest index: sentinel 0x{sentinel:X} != 0x{Sentinel:X}");

			var magic = r.ReadUInt32();
			if (magic != TableMagic)
				throw new InvalidOperationException($"Quest index: table magic 0x{magic:X} != 0x{TableMagic:X}");

			var f1 = r.ReadUInt32();
			var f2 = r.ReadUInt32();
			var f3 = r.ReadUInt32();
			var n = r.ReadUInt32();
			var goalIds = new List<uint>((int)n);
			for (uint i = 0; i < n; i++)
			{
				goalIds.Add(r.ReadUInt32());
				var idx = r.ReadUInt32();
				if (idx != i + 1)
					throw new InvalidOperationException($"Quest index: goal idx {idx} != {i + 1}");
			}

			var trailer = r.ReadArray((uint)(endOffset - r.Position));
			return new QuestIndexRegion
			{
				Marker = marker,
				QuestIds = questIds,
				TableField1 = f1,
				TableField2 = f2,
				TableField3 = f3,
				GoalIds = goalIds,
				Trailer = trailer
			};
		}

		private static uint PeekUInt32(byte[] data, uint position)
		{
			return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(checked((int)position), 4));
		}
	}
}
