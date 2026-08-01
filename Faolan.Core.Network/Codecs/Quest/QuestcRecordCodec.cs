using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// One Quest_c journal RECORD - the client's per-quest journal entry, read by FUN_0091df50. Composed by
	// QuestcRegionCodec (the snapshot's journal list) and by QuestFullUpdateCodec, which carries exactly ONE of these
	// as its whole payload (#530 wave 5 - the live full-update used to hand-roll the same 12 scalars + 5 strings + 4
	// object lists + objective region under a different set of names).
	//
	// Wire:
	//   [u32 listKey(=questId)][u32 ver(=1)][u32 questId][u32 state][u32 field114][u32 field118][u32 n]
	//   [string title][string summary][string area][string region1][string region2]
	//   [u32 fieldEc][u32 areaId][u32 field2c][u32 field11c][u64 accept][u32 field28]
	//   [objlist1][objlist2][u32 fieldF0][u64 fieldF8][objlist3][objlist4]
	//   [objective region]
	// where each objlist = [u32 count] then count × ObjRec (16 raw u32s, unvalidated - empty for the beach starter).
	//
	// The objective region is part of the RECORD (interleaved per quest, #384), not a separate top-level region: the
	// reader used to skip it and desynced on the SECOND record of any multi-quest journal with "marker 0x2 != 0x412".
	public static class QuestcRecordCodec
	{
		public const uint RecordVersion = 1;
		private const int ObjRecWords = 16;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestcRecord value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestcRecord Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestcRecordCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestcRecord Read(ref SpanReader r)
		{
			var at = r.Position;
			var listKey = r.ReadU32Be();
			r.ExpectU32Be(RecordVersion, "Quest_c record version");

			var questId = r.ReadU32Be();
			if (listKey != questId)
				throw CodecParseException.Unexpected(nameof(QuestcRecordCodec), at, $"listKey == questId ({questId})", listKey.ToString());

			var level = r.ReadU32Be();
			var field114 = r.ReadU32Be();
			var field118 = r.ReadU32Be();
			var n = r.ReadU32Be();

			var title = r.ReadStringU16Length();
			var summary = r.ReadStringU16Length();
			var area = r.ReadStringU16Length();
			var region1 = r.ReadStringU16Length();
			var region2 = r.ReadStringU16Length();

			var fieldEc = r.ReadU32Be();
			var areaId = r.ReadU32Be();
			var field2c = r.ReadU32Be();
			var field11c = r.ReadU32Be();
			var accept = r.ReadU64Be();
			var field28 = r.ReadU32Be();

			var obj1 = ReadObjList(ref r);
			var obj2 = ReadObjList(ref r);
			var fieldF0 = r.ReadU32Be();
			var fieldF8 = r.ReadU64Be();
			var obj3 = ReadObjList(ref r);
			var obj4 = ReadObjList(ref r);

			return new QuestcRecord
			{
				QuestId = questId,
				Level = level,
				Field114 = field114,
				Field118 = field118,
				QuestLineFlags = n,
				Title = title,
				Summary = summary,
				Area = area,
				Region1 = region1,
				Region2 = region2,
				FieldEc = fieldEc,
				QuestLineId = areaId,
				Field2c = field2c,
				Field11c = field11c,
				Accept = accept,
				QuestLineReward = field28,
				XpReward = fieldF0,
				CashReward = fieldF8,
				Obj1 = obj1,
				Obj2 = obj2,
				Obj3 = obj3,
				Obj4 = obj4,
				Objectives = QuestObjectiveRegionCodec.Read(ref r)
			};
		}

		internal static void Write(FaolanStream s, QuestcRecord v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(v.QuestId); // listKey
			s.WriteUInt32(RecordVersion);
			s.WriteUInt32(v.QuestId);
			s.WriteUInt32(v.Level);
			s.WriteUInt32(v.Field114);
			s.WriteUInt32(v.Field118);
			s.WriteUInt32(v.QuestLineFlags);

			s.WriteString(v.Title);
			s.WriteString(v.Summary);
			s.WriteString(v.Area);
			s.WriteString(v.Region1);
			s.WriteString(v.Region2);

			s.WriteUInt32(v.FieldEc);
			s.WriteUInt32(v.QuestLineId);
			s.WriteUInt32(v.Field2c);
			s.WriteUInt32(v.Field11c);
			s.WriteUInt64(v.Accept);
			s.WriteUInt32(v.QuestLineReward);

			WriteObjList(s, v.Obj1);
			WriteObjList(s, v.Obj2);
			s.WriteUInt32(v.XpReward);
			s.WriteUInt64(v.CashReward);
			WriteObjList(s, v.Obj3);
			WriteObjList(s, v.Obj4);

			// NULL means "this record's objective region is composed by the caller", which is how the beach goldens
			// slice the journal at @609 and compare the objective region separately. A record READ off the wire always
			// carries one, so the asymmetry only reaches hand-built values.
			if (v.Objectives != null)
				QuestObjectiveRegionCodec.Write(s, v.Objectives);
		}

		private static List<uint[]> ReadObjList(ref SpanReader r)
		{
			var count = r.ReadU32Be();
			var list = new List<uint[]>((int)count);
			for (var i = 0; i < count; i++)
			{
				var rec = new uint[ObjRecWords];
				for (var j = 0; j < ObjRecWords; j++)
					rec[j] = r.ReadU32Be();
				list.Add(rec);
			}

			return list;
		}

		private static void WriteObjList(FaolanStream s, List<uint[]> list)
		{
			s.WriteUInt32(list.Count);
			foreach (var rec in list)
			foreach (var word in rec)
				s.WriteUInt32(word);
		}
	}

	// One quest's journal record. See QuestcRecords for the DB-row factory.
	public sealed record QuestcRecord
	{
		public uint QuestLineId { get; init; } // @+0xcc (matches the Area string's id)
		public uint QuestId { get; init; }

		// The four objective lists. Each entry is one ObjRec carried as 16 raw u32s (unvalidated; empty for
		// the beach starter, so this is exercised only by round-trip of progressed captures).
		public List<uint[]> Obj1 { get; init; } = [];
		public List<uint[]> Obj2 { get; init; } = [];
		public List<uint[]> Obj3 { get; init; } = [];
		public List<uint[]> Obj4 { get; init; } = [];

		// The quest's @609 OBJECTIVE region, which on the wire follows this record's obj4 - the journal record and its
		// objective region are INTERLEAVED per quest, not two separate top-level regions (#384). Null only for a record
		// built without one (see the Write comment). See tools/capture/decode_questc_progress.py, which validates the
		// interleaved grammar across all 141 captured snapshots.
		public QuestObjectiveRegion? Objectives { get; set; }
		public string Area { get; init; } = ""; // <remoteformat category=50300>
		public string Region1 { get; init; } = ""; // <remoteformat category=52000>
		public string Region2 { get; init; } = ""; // <remoteformat category=52000>
		public string Summary { get; init; } = ""; // <remoteformat category=50302>
		public string Title { get; init; } = ""; // <remoteformat category=50301>
		public uint Field114 { get; init; } // @+0x114 (=0 on the beach template)
		public uint Field118 { get; init; } // @+0x118
		public uint Field11c { get; init; } // @+0x11c (=0)
		// @+0x28 - a reward magnitude carried on the LAST step of a quest line (25 of 26 captured cases; the XP/cash
		// pair below is never on the last step). Its POSITION is proven, its currency is not - see QuestDefinition
		// .SnapshotQuestLineReward. (=0 on the beach template; 0x50 in the live full-update.)
		public uint QuestLineReward { get; init; }
		// @+0x2c - the CHAIN HEAD: the first quest of the series this quest belongs to, which is the quest's own id for
		// a standalone one. Measured over the 99 quests with a captured record: 84 carry their own id and every one of
		// the other 15 carries an earlier quest that is a step of the same chain - 10915 and 12371 both carry 10238
		// (the beach starter, and both appear in ITS IndexQuestLineQuestIds list), 12068 carries 12067, 13986 carries 13984.
		// ⚠️ QuestcRecords.ForQuest still emits `def.QuestId` unconditionally, which is right for 84 of them and wrong
		// for a chain member - the head is not derivable from what we store (only the head itself carries
		// IndexQuestLineQuestIds), so it needs harvesting into its own column. Tracked in Gitea #566.
		public uint Field2c { get; init; }
		public uint FieldEc { get; init; } // @+0xec (=1 in template)
		// @+0xf0 - THIS step's own experience reward, and @+0xf8 below its money reward. The client's quest catalog
		// (RDB 1000104) gives a quest line exactly one `xp_reward`/`cash_reward` pair in this order and of these
		// shapes, and the magnitudes coincide (`[Thunder River]` lines carry xp_reward 45800, which is the captured
		// @+0xf0 of quests 13895/14427). @+0xf0 also selects the objective region's text spelling - see
		// QuestDefinition.SnapshotXpReward. (=0x96 in the template, 0x64 in the live full-update.)
		public uint XpReward { get; init; }

		// @+0x10 - the quest's LEVEL, not a state. It equals the DB `Level` on 96 of the 99 quests with a captured
		// record (the three that differ - 12373, 17402, 17403 - differ by 1-2, i.e. the row took its level from a
		// different capture), and both writers already fed it a level: QuestFullUpdates passed `level` and
		// QuestDefinitionExtractor assigned it straight to `def.Level`. It was called `State` until 2026-08-01, which
		// made QuestcRegionTests assert `Equal(1u, q.State) // active` on the beach starter - passing only because
		// that quest happens to be LEVEL 1. Distinct from QuestCodec.State (StateActive/StateCompleted), which is a
		// real state on a different message.
		public uint Level { get; set; } // settable: world entry patches the lead quest's word

		// @+0x14 - a flag word of the quest LINE, inherited by each of its steps: constant per quest across 2626
		// captured records AND constant across every quest of the same line (90 of 90 lines agree). NOT a goal count,
		// despite carrying that name until 2026-07-20: the beach template holds 0x412 (1042) for a TWO-goal quest, and
		// NecroL80's first record holds 2. Emitting it as a goal count - telling the client "N objectives follow" and
		// then writing none - desynced the client's cursor and crashed it in StatList_t. NOT a ListFrame word either
		// (1042 is not a multiple of 1009). Bit 10 marks the progression lines; the rest is open - see
		// QuestDefinition.SnapshotQuestLineFlags. Carry it through from the capture rather than deriving it.
		public uint QuestLineFlags { get; init; }
		public ulong Accept { get; init; } // @+0x20 u64 (likely acceptTime; =0)
		// @+0xf8 - this step's MONEY reward (see XpReward). ⚠️ It correlates with @+0xf0 through a level-dependent
		// factor but the law does not close over 99 samples - do NOT derive one from the other.
		public ulong CashReward { get; init; } // (=15 in the template, 0 in the live full-update)
	}
}
