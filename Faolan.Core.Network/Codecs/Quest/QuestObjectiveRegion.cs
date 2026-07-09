using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The @609 OBJECTIVE-DETAIL region of the 0x737A6DF9 FullChar snapshot tails - the bytes between the Quest_c
	// journal region (@0..609, see QuestcRegion) and the quest INDEX (the 0x000003F1 tag). Grammar decoded +
	// validated 2026-06-16 (Captures/scripts/decode_snapshot_609.py, docs/re/fullplayer-snapshot-reader.md): a per-GOAL
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

		public void Write(FaolanStream w)
		{
			w.WriteUInt32((Goals.Count + 1) * Frame);
			foreach (var goal in Goals)
				goal.Write(w);
		}

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			Write(w);
			return w.ToArray();
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
				region.Goals.Add(QuestObjectiveGoal.BuildSingle(instance, def.QuestId, g.GoalId, g.Sequence, g.Separator, g.SubId, g.MapId, g.MarkerX, g.MarkerY, Encoding.ASCII.GetBytes(objective),
					Encoding.ASCII.GetBytes(trailing), g.Extra5, g.Extra6, g.RequiredCount == 0
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
			using var r = new FaolanStream(data, false)
			{
				Position = checked((uint)offset)
			};
			var region = Read(r);
			consumed = checked((int)(r.Position - offset));
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
}
