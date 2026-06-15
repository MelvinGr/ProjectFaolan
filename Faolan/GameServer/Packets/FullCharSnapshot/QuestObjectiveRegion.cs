using System;
using System.Collections.Generic;
using Faolan.Core.Data;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// The @609 OBJECTIVE-DETAIL region of the 0x737A6DF9 FullChar snapshot tails - the bytes between the Quest_c
	// journal region (@0..609, see QuestcRegion) and the quest INDEX (the 0x000003F1 tag). Grammar decoded +
	// validated 2026-06-16 (Captures/decode_snapshot_609.py, docs/fullplayer-snapshot-reader.md): a per-GOAL
	// objective-detail list, NOT a BuildQuestFullUpdate block (that earlier claim was disproven - 559 B vs 869 B).
	//
	// Wire: u32 chapterTag(0x0BD3) ; then per goal:
	//   u32 1 ; u32 k ; u32 instance ; u32 questId ; u32 goalId ; u32 n ; u32 goalIdRep ; u32 1 ;
	//   u32 x7 reserved ; u32 subId ; u32 mapId ; u32 markerX ; u32 markerY ; u32 0 ; u32 0 ;
	//   [u32 len][objective <remoteformat 50304> ascii] ; [00][u8 len][text record <remoteformat 51200>/<localized 51000> ascii]
	// Goals run until the next u32 is the index tag (goals open with [1], the index with 0x000003F1).
	//
	// The beach starter (quest 10238) has TWO goals (1448 + 3490). Objective text is DB-sourced; the per-goal
	// markers/sub-refs are richer than QuestDefinition's single GoalId (so GENERATING needs a per-goal model, #186).
	// This codec round-trips the region byte-exact (QuestObjectiveRegionTests) - the construct-don't-replay
	// foundation, mirroring QuestcRegion's role for @0..609.
	public sealed class QuestObjectiveRegion
	{
		public const uint ChapterTag = 0x00000BD3;
		public const uint IndexTag = 0x000003F1; // opens the quest index that terminates this region
		public const uint GoalLead = 1;          // every goal record opens with this word

		public List<QuestObjectiveGoal> Goals { get; init; } = [];

		public static QuestObjectiveRegion Parse(byte[] data, int offset) => Parse(data, offset, out _);

		// Parses the region starting at `offset` (the chapterTag) and reports how many bytes it consumed - the
		// boundary where the quest index begins.
		public static QuestObjectiveRegion Parse(byte[] data, int offset, out int consumed)
		{
			ArgumentNullException.ThrowIfNull(data);
			using var r = new FaolanStream(data, false) { Position = checked((uint)offset) };

			var tag = r.ReadUInt32();
			if (tag != ChapterTag)
				throw new InvalidOperationException($"@609 region: chapterTag 0x{tag:X} != 0x{ChapterTag:X}");

			var region = new QuestObjectiveRegion();
			while (r.Position + 4 <= r.Length && PeekUInt32(data, r.Position) == GoalLead)
				region.Goals.Add(QuestObjectiveGoal.Read(r));

			consumed = checked((int)(r.Position - offset));
			return region;
		}

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(ChapterTag);
			foreach (var goal in Goals)
				goal.Write(w);
			return w.ToArray();
		}

		private static uint PeekUInt32(byte[] data, uint position) =>
			System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(checked((int)position), 4));
	}

	public sealed class QuestObjectiveGoal
	{
		public uint Lead { get; init; }       // [1]
		public uint K { get; init; }          // goal separator second word (9 / 12 in the template)
		public uint Instance { get; init; }   // the live player's client instance (re-addressed)
		public uint QuestId { get; init; }
		public uint GoalId { get; init; }
		public uint N { get; init; }
		public uint GoalIdRepeat { get; init; }
		public uint One { get; init; }         // [1]
		public uint[] Reserved { get; init; } = new uint[ReservedWords]; // 7 words, all 0 in the template
		public uint SubId { get; init; }       // a secondary text id (= the trailing record's id)
		public uint MapId { get; init; }
		public uint MarkerX { get; init; }
		public uint MarkerY { get; init; }
		public uint Zero1 { get; init; }
		public uint Zero2 { get; init; }
		public byte[] Objective { get; init; } = []; // <remoteformat category=50304> ascii (u32-length-prefixed)
		public byte[] TextRecord { get; init; } = []; // <remoteformat 51200>/<localized 51000> ascii (00 + u8 length)

		private const int ReservedWords = 7;

		public static QuestObjectiveGoal Read(FaolanStream r)
		{
			var lead = r.ReadUInt32();
			var k = r.ReadUInt32();
			var instance = r.ReadUInt32();
			var questId = r.ReadUInt32();
			var goalId = r.ReadUInt32();
			var n = r.ReadUInt32();
			var goalIdRepeat = r.ReadUInt32();
			var one = r.ReadUInt32();
			var reserved = new uint[ReservedWords];
			for (var i = 0; i < ReservedWords; i++)
				reserved[i] = r.ReadUInt32();
			var subId = r.ReadUInt32();
			var mapId = r.ReadUInt32();
			var markerX = r.ReadUInt32();
			var markerY = r.ReadUInt32();
			var zero1 = r.ReadUInt32();
			var zero2 = r.ReadUInt32();

			var objective = r.ReadArrayUInt32Length();
			var leadByte = r.ReadByte();
			if (leadByte != 0)
				throw new InvalidOperationException($"@609 goal: text-record lead 0x{leadByte:X} != 0");
			var text = r.ReadArrayByteLength();

			return new QuestObjectiveGoal
			{
				Lead = lead, K = k, Instance = instance, QuestId = questId, GoalId = goalId, N = n,
				GoalIdRepeat = goalIdRepeat, One = one, Reserved = reserved, SubId = subId, MapId = mapId,
				MarkerX = markerX, MarkerY = markerY, Zero1 = zero1, Zero2 = zero2,
				Objective = objective, TextRecord = text
			};
		}

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(Lead);
			w.WriteUInt32(K);
			w.WriteUInt32(Instance);
			w.WriteUInt32(QuestId);
			w.WriteUInt32(GoalId);
			w.WriteUInt32(N);
			w.WriteUInt32(GoalIdRepeat);
			w.WriteUInt32(One);
			foreach (var word in Reserved)
				w.WriteUInt32(word);
			w.WriteUInt32(SubId);
			w.WriteUInt32(MapId);
			w.WriteUInt32(MarkerX);
			w.WriteUInt32(MarkerY);
			w.WriteUInt32(Zero1);
			w.WriteUInt32(Zero2);
			w.WriteArrayUInt32Length(Objective);
			w.WriteByte(0);
			w.WriteArrayByteLength(TextRecord);
		}
	}
}
