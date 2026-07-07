using System;
using Faolan.Core.Network.Codecs;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.GameServer
{
	// The @609 OBJECTIVE-DETAIL region of the 0x737A6DF9 FullChar snapshot tails - the bytes between the Quest_c
	// journal region (@0..609, see QuestcRegion) and the quest INDEX (the 0x000003F1 tag). Grammar decoded +
	// validated 2026-06-16 (Captures/scripts/decode_snapshot_609.py, docs/fullplayer-snapshot-reader.md): a per-GOAL
	// objective-detail list, NOT a BuildQuestFullUpdate block (that earlier claim was disproven - 559 B vs 869 B).
	//
	// Wire: u32 chapterTag(0x0BD3) ; then per goal:
	//   u32 1 ; u32 k ; u32 instance ; u32 questId ; u32 goalId ; u32 n ; u32 goalIdRep ; u32 requiredCount ;
	//   u32 x7 reserved ; u32 subId ; u32 mapId ; u32 markerX ; u32 markerY ; u32 0 ; u32 0 ;
	//   [u32 len][objective <remoteformat 50304> ascii] ; [00][u8 len][text record <remoteformat 51200>/<localized 51000> ascii]
	// Goals run until the next u32 is the index tag (goals open with [1], the index with 0x000003F1).
	//
	// The beach starter (quest 10238) has TWO goals (1448 + 3490). Objective text is DB-sourced; the per-goal
	// markers/sub-refs are richer than QuestDefinition's single GoalId (so GENERATING needs a per-goal model, #186).
	// This codec round-trips the region byte-exact (QuestObjectiveRegionTests) - the construct-don't-replay
	// foundation, mirroring QuestcRegion's role for @0..609.
	public sealed class QuestObjectiveRegion : ICodec<QuestObjectiveRegion>
	{
		// The region's lead word is the goal-list FRAMING count = (goals + 1) * 0x3F1 (the same 0x3F1 list-framing
		// multiplier as QuestcRegion/QuestIndexRegion). The beach quest's 2 goals make it 0xBD3, which was previously
		// hardcoded as "ChapterTag"; a 1-goal quest (e.g. Casilda 13479) is 0x7E2. #255.
		public const uint Frame = 0x000003F1;
		public const uint GoalLead = 1; // every goal record opens with this word
		public const uint IndexTag = 0x000003F1; // opens the quest index that terminates this region

		public List<QuestObjectiveGoal> Goals { get; init; } = [];

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			Write(w);
			return w.ToArray();
		}

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(((uint)Goals.Count + 1) * Frame);
			foreach (var goal in Goals)
				goal.Write(w);
		}

		// GENERATES the objective region from a quest's per-goal definitions (construct, don't replay - #186): each
		// QuestGoalDefinition's numeric fields + its objective/trailing text refs build a record via BuildSingle, so
		// the region comes from the DB instead of BeachTailsTemplate.bin. `instance` is the live player's client
		// instance (the records carry raw entity references, re-addressed like the rest of the tails).
		public static QuestObjectiveRegion Build(QuestDefinition def, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);
			var region = new QuestObjectiveRegion();
			foreach (var g in def.Goals.OrderBy(g => g.Sequence))
			{
				var objective = QuestcRegion.RemoteFormat(g.ObjectiveRefId, g.ObjectiveCategory, g.ObjectiveKey);
				var trailing = g.TrailingLocalized
					? Localized(g.TrailingRefId, g.TrailingCategory, g.TrailingFlags ?? "", g.TrailingKey)
					: QuestcRegion.RemoteFormat(g.TrailingRefId, g.TrailingCategory, g.TrailingKey);
				region.Goals.Add(QuestObjectiveGoal.BuildSingle(instance,
					def.QuestId,
					g.GoalId,
					g.Sequence,
					g.Separator,
					g.SubId,
					g.MapId,
					g.MarkerX,
					g.MarkerY,
					Encoding.ASCII.GetBytes(objective),
					Encoding.ASCII.GetBytes(trailing),
					g.Extra5,
					g.Extra6,
					g.RequiredCount == 0
						? 1
						: g.RequiredCount));
			}

			return region;
		}

		// The <localized> text-record spelling (mirrors QuestcRegion.RemoteFormat); the trailing record of a goal is
		// either a <remoteformat> (cat 51200) or this <localized> form (cat 51000), with the exact trailing " >".
		public static string Localized(uint id, uint category, string flags, string key)
		{
			return $"<localized id=\"{id}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >";
		}

		public static QuestObjectiveRegion Parse(byte[] data, int offset)
		{
			return Parse(data, offset, out _);
		}

		// Parses the region starting at `offset` (the chapterTag) and reports how many bytes it consumed - the
		// boundary where the quest index begins.
		public static QuestObjectiveRegion Parse(byte[] data, int offset, out int consumed)
		{
			ArgumentNullException.ThrowIfNull(data);
			using var r = new FaolanStream(data, false) { Position = checked((uint)offset) };
			var region = Read(r);
			consumed = checked((int)(r.Position - offset));
			return region;
		}

		public static QuestObjectiveRegion Read(FaolanStream r)
		{
			var tag = r.ReadUInt32();
			if (tag == 0 || tag % Frame != 0)
				throw new InvalidOperationException($"@609 region: lead word 0x{tag:X} is not a multiple of 0x{Frame:X}");

			var region = new QuestObjectiveRegion();
			while (r.Position + 4 <= r.Length && Peek(r) == GoalLead)
				region.Goals.Add(QuestObjectiveGoal.Read(r));

			return region;
		}

		// Peeks the next big-endian word without advancing (goal records run until the first non-GoalLead word).
		private static uint Peek(FaolanStream r)
		{
			var p = r.Position;
			var v = r.ReadUInt32();
			r.Position = p;
			return v;
		}
	}

	public sealed class QuestObjectiveGoal : ICodec<QuestObjectiveGoal>
	{
		private const int ReservedWords = 7;
		public byte[] Objective { get; init; } = []; // <remoteformat category=50304> ascii (u32-length-prefixed)
		public byte[] TextRecord { get; init; } = []; // <remoteformat 51200>/<localized 51000> ascii (00 + u8 length)
		public uint GoalId { get; init; }
		public uint GoalIdRepeat { get; init; }
		public uint Instance { get; init; } // the live player's client instance (re-addressed)
		public uint K { get; init; } // goal separator second word (9 / 12 in the template)
		public uint Lead { get; init; } // [1]
		public uint MapId { get; init; }
		public uint MarkerX { get; init; }
		public uint MarkerY { get; init; }
		public uint N { get; init; }
		public uint QuestId { get; init; }
		public uint RequiredCount { get; init; } // the goal's required amount (kills/collects/etc.); 1/3/4/8/12/64 seen on the wire (decode 2026-06-19, #47)
		public uint SubId { get; init; } // a secondary text id (= the trailing record's id)
		public uint Zero1 { get; init; }
		public uint Zero2 { get; init; }
		public uint[] Reserved { get; init; } = new uint[ReservedWords]; // 7 words, all 0 in the template

		public byte[] ToArray()
		{
			using var s = new FaolanStream();
			Write(s);
			return s.ToArray();
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
			w.WriteUInt32(RequiredCount);
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

		// Builds one goal record from its MEANINGFUL fields, synthesizing the constants (Lead/One = 1, GoalIdRepeat =
		// GoalId, the 7 reserved words + Zero1/Zero2 = 0). So a compact per-goal definition (goalId, n, k, subId, map,
		// marker + the objective/trailing text records) regenerates the full @609 record - the construct-don't-replay
		// generator for the objective region (#186), mirroring QuestcRecord.BuildSingle for the journal region. The
		// objective/trailing text come from the quest's per-goal text refs (the same RemoteFormat/Localized records the
		// journal uses); `instance` is re-addressed to the live player like the rest of the tails.
		// extra5/extra6 are the last two of the 7 "reserved" words: 0 for the first goal, but the second goal carries
		// a number + a 4-char tag there (70099 / 0x36314C56 = "61LV"; still undecoded - carried per goal like
		// QuestcRecord's undecoded scalars). The first five reserved words are always 0.
		public static QuestObjectiveGoal BuildSingle(uint instance, uint questId, uint goalId, uint n, uint k, uint subId, uint mapId, uint markerX, uint markerY, byte[] objective, byte[] textRecord, uint extra5 = 0,
			uint extra6 = 0, uint requiredCount = 1)
		{
			ArgumentNullException.ThrowIfNull(objective);
			ArgumentNullException.ThrowIfNull(textRecord);
			return new QuestObjectiveGoal
			{
				Lead = 1,
				K = k,
				Instance = instance,
				QuestId = questId,
				GoalId = goalId,
				N = n,
				GoalIdRepeat = goalId,
				RequiredCount = requiredCount,
				Reserved = [0, 0, 0, 0, 0, extra5, extra6],
				SubId = subId,
				MapId = mapId,
				MarkerX = markerX,
				MarkerY = markerY,
				Zero1 = 0,
				Zero2 = 0,
				Objective = objective,
				TextRecord = textRecord
			};
		}

		public static QuestObjectiveGoal Read(FaolanStream r)
		{
			var lead = r.ReadUInt32();
			var k = r.ReadUInt32();
			var instance = r.ReadUInt32();
			var questId = r.ReadUInt32();
			var goalId = r.ReadUInt32();
			var n = r.ReadUInt32();
			var goalIdRepeat = r.ReadUInt32();
			var requiredCount = r.ReadUInt32();
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
				Lead = lead,
				K = k,
				Instance = instance,
				QuestId = questId,
				GoalId = goalId,
				N = n,
				GoalIdRepeat = goalIdRepeat,
				RequiredCount = requiredCount,
				Reserved = reserved,
				SubId = subId,
				MapId = mapId,
				MarkerX = markerX,
				MarkerY = markerY,
				Zero1 = zero1,
				Zero2 = zero2,
				Objective = objective,
				TextRecord = text
			};
		}
	}
}
