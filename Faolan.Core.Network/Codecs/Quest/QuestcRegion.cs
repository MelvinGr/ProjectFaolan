using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The Quest_c (List A) region at the START of the 0x737A6DF9 FullChar snapshot tails - the quest JOURNAL.
	// Grammar recovered from the client reader FullPlayerCharacterIIR::ReadSubClass (FUN_0148c210 -> base
	// FUN_00797440 -> List A driver FUN_0091f5c0 -> per-record FUN_0091df50) and validated byte-exact against
	// BeachTailsTemplate.bin (docs/fullplayer-snapshot-reader.md, Captures/scripts/decode_questc.py).
	//
	// Wire: u32 version(0x18) ; u32 listA_count = (records+1)*0x3F1 ; then per record:
	//   u32 listKey(=questId) ; u32 ver(=1) ; u32 questId ; u32 state ; u32 0 ; u32 N ; u32 0x412 ;
	//   string x5 ([u16 len][utf8] <remoteformat>) ; u32 ; u32 areaId ; u32 questId ; u32 ;
	//   u64 ; u32 ; objlist1 ; objlist2 ; u32 ; u64 ; objlist3 ; objlist4
	// where each objlist = u32 count then count x ObjRec(16 u32). The four objlists are EMPTY for the beach
	// starter (so ObjRec is carried verbatim as raw u32s and is not yet field-validated).
	//
	// Proven by a byte-exact round-trip golden test (QuestcRegionTests); the construct-don't-replay foundation
	// for retiring BeachTailsTemplate.bin's quest region (Gitea #186). WIRED into FullCharSnapshotSubClass (it models the
	// tails' front region instead of the former opaque header+string fields) - the live snapshot is byte-
	// identical (BeachSnapshotConstructionTests). Generating DIFFERENT quests from a character's CharacterQuests
	// is the next step and is gated by an in-client chat-connect smoke test.
	//
	// #304 RE 2026-07-05: the FULL structure is decoded from the client readers (Ghidra FUN_0091f5c0 driver +
	// FUN_0091df50 per-record) and validated byte-exact on NecroL80's lvl80 login — see docs/questc-region-decode.md.
	// Two fields below are MISLABELED (beach-template coincidence): `Marker`(asserted 0x412) is really the variable
	// `n` goal-count, and `QuestId2`(asserted ==questId) is really `field2c`; both asserts REJECT real progressed
	// snapshots. Each record also ends with a NESTED 0x3F1-framed per-goal PROGRESS sub-list (empty on the beach
	// template, so unmodeled here) whose entries carry a `"WTHD"` block that is **opaque server data with no client
	// reader** (FindConst = 0 hits) — the hard limit of client-only RE. Production is unaffected: fresh chars (the
	// only journals we generate) have empty objlists + empty progress. TODO(#186): parse/generate progressed
	// journals once the WTHD fields are pinned (needs a server spec or a progressed-capture corpus).
	public sealed class QuestcRegion : ICodec<QuestcRegion>
	{
		public const uint Frame = 0x3F1; // 1009 - the list-count framing multiplier
		public const uint Marker = 0x412;
		public const uint RecordVersion = 1;
		public const uint Version = 0x18;

		public List<QuestcRecord> Records { get; init; } = [];

		public void Write(FaolanStream w)
		{
			w.WriteUInt32(Version);
			w.WriteUInt32((Records.Count + 1) * Frame);
			foreach (var record in Records)
				record.Write(w);
		}

		public static QuestcRegion Parse(byte[] data)
		{
			return Parse(data, out _);
		}

		// Parses the Quest_c region from the FRONT of `data` (which may be the whole tails) and reports how many
		// bytes it consumed - the boundary where the next region (objective blocks / index) begins.
		public static QuestcRegion Parse(byte[] data, out int consumed)
		{
			ArgumentNullException.ThrowIfNull(data);
			using var r = new FaolanStream(data, false);
			var region = Read(r);
			consumed = checked((int)r.Position);
			return region;
		}

		public static QuestcRegion Read(FaolanStream r)
		{
			var version = r.ReadUInt32();
			if (version != Version)
				throw new InvalidOperationException($"Quest_c region: version 0x{version:X} != 0x{Version:X}");

			var count = r.ReadUInt32();
			if (count == 0 || count % Frame != 0)
				throw new InvalidOperationException($"Quest_c region: listA count 0x{count:X} is not a positive multiple of 0x{Frame:X}");

			var records = count / Frame - 1;

			var region = new QuestcRegion();
			for (var i = 0; i < records; i++)
				region.Records.Add(QuestcRecord.Read(r));
			return region;
		}

		// The <remoteformat> token text the client resolves by (id, category) against its RDB; `key` is the
		// pre-escaped deterministic content hash from QuestTextRef.KeyText. Matches WorldQuest.RemoteFormat's
		// exact spelling (incl. the trailing knubot="0" and two spaces) so it reproduces the captured bytes.
		public static string RemoteFormat(uint id, uint category, string key)
		{
			return $"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}
	}

	public sealed class QuestcRecord : ICodec<QuestcRecord>
	{
		private const int ObjRecWords = 16;

		// The four objective lists. Each entry is one ObjRec carried as 16 raw u32s (unvalidated; empty for
		// the beach starter, so this is exercised only by round-trip of progressed captures).
		public List<uint[]> Obj1 { get; init; } = [];
		public List<uint[]> Obj2 { get; init; } = [];
		public List<uint[]> Obj3 { get; init; } = [];
		public List<uint[]> Obj4 { get; init; } = [];
		public string Area { get; init; } = ""; // <remoteformat category=50300>
		public string Region1 { get; init; } = ""; // <remoteformat category=52000>
		public string Region2 { get; init; } = ""; // <remoteformat category=52000>
		public string Summary { get; init; } = ""; // <remoteformat category=50302>
		public string Title { get; init; } = ""; // <remoteformat category=50301>
		public uint AreaId { get; init; } // @+0xcc (matches the Area string's id)
		public uint Field11c { get; init; } // @+0x11c (=0)
		public uint Field28 { get; init; } // @+0x28 (=0)
		public uint FieldEc { get; init; } // @+0xec (=1 in template)
		public uint FieldF0 { get; init; } // @+0xf0 (=0x96)
		public uint N { get; init; }
		public uint QuestId { get; init; }
		public uint State { get; set; } // settable: world entry historically patches the lead quest's @20 word
		public uint Zero { get; init; }
		public ulong Accept { get; init; } // @+0x20 u64 (likely acceptTime; =0)
		public ulong FieldF8 { get; init; } // @+0xf8 u64 (=15)


		public void Write(FaolanStream w)
		{
			w.WriteUInt32(QuestId); // listKey
			w.WriteUInt32(QuestcRegion.RecordVersion);
			w.WriteUInt32(QuestId);
			w.WriteUInt32(State);
			w.WriteUInt32(Zero);
			w.WriteUInt32(N);
			w.WriteUInt32(QuestcRegion.Marker);

			w.WriteString(Title);
			w.WriteString(Summary);
			w.WriteString(Area);
			w.WriteString(Region1);
			w.WriteString(Region2);

			w.WriteUInt32(FieldEc);
			w.WriteUInt32(AreaId);
			w.WriteUInt32(QuestId);
			w.WriteUInt32(Field11c);
			w.WriteUInt64(Accept);
			w.WriteUInt32(Field28);

			WriteObjList(w, Obj1);
			WriteObjList(w, Obj2);
			w.WriteUInt32(FieldF0);
			w.WriteUInt64(FieldF8);
			WriteObjList(w, Obj3);
			WriteObjList(w, Obj4);
		}

		// Build a single quest-journal record from a QuestDefinition (its 5 header text refs + areaId) plus the
		// per-quest scalars that are NOT yet harvested into QuestDefinition (the goal/line count `n` and the two
		// undecoded words FieldF0/FieldF8 - carried from the capture per quest). The numeric objective lists are
		// left empty (the objective TEXT rides in the strings; see docs/fullplayer-snapshot-reader.md). This is
		// the construct-don't-replay generator for the quest region (#186), proven byte-exact for 10238 by
		// QuestcGenerationTests.
		public static QuestcRecord BuildSingle(QuestDefinition def, uint state, uint n, uint fieldEc, uint fieldF0, ulong fieldF8, uint field28 = 0)
		{
			ArgumentNullException.ThrowIfNull(def);

			return new QuestcRecord
			{
				QuestId = def.QuestId,
				State = state,
				Zero = 0,
				N = n,
				Title = Slot(QuestTextSlot.Title),
				Summary = Slot(QuestTextSlot.Summary),
				Area = Slot(QuestTextSlot.AreaTitle),
				Region1 = Slot(QuestTextSlot.RegionName),
				Region2 = Slot(QuestTextSlot.MapName),
				FieldEc = fieldEc,
				AreaId = def.AreaId,
				Field11c = 0,
				Accept = 0,
				Field28 = field28,
				FieldF0 = fieldF0,
				FieldF8 = fieldF8
			};

			string Slot(QuestTextSlot slot)
			{
				var t = def.TextRefs.FirstOrDefault(x => x.Slot == slot) ?? throw new InvalidOperationException($"Quest {def.QuestId}: missing text ref slot {slot}");
				return QuestcRegion.RemoteFormat(t.RefId, t.Category, t.KeyText);
			}
		}

		public static QuestcRecord Read(FaolanStream r)
		{
			var listKey = r.ReadUInt32();
			var ver = r.ReadUInt32();
			if (ver != QuestcRegion.RecordVersion)
				throw new InvalidOperationException($"Quest_c record: ver 0x{ver:X} != 1");

			var questId = r.ReadUInt32();
			if (listKey != questId)
				throw new InvalidOperationException($"Quest_c record: listKey {listKey} != questId {questId}");

			var state = r.ReadUInt32();
			var zero = r.ReadUInt32();
			var n = r.ReadUInt32();
			var marker = r.ReadUInt32();
			if (marker != QuestcRegion.Marker)
				throw new InvalidOperationException($"Quest_c record: marker 0x{marker:X} != 0x{QuestcRegion.Marker:X}");

			var title = r.ReadString();
			var summary = r.ReadString();
			var area = r.ReadString();
			var region1 = r.ReadString();
			var region2 = r.ReadString();

			var fieldEc = r.ReadUInt32();
			var areaId = r.ReadUInt32();
			var questId2 = r.ReadUInt32();
			if (questId2 != questId)
				throw new InvalidOperationException($"Quest_c record: trailing questId {questId2} != {questId}");

			var field11c = r.ReadUInt32();
			var accept = r.ReadUInt64();
			var field28 = r.ReadUInt32();

			var obj1 = ReadObjList(r);
			var obj2 = ReadObjList(r);
			var fieldF0 = r.ReadUInt32();
			var fieldF8 = r.ReadUInt64();
			var obj3 = ReadObjList(r);
			var obj4 = ReadObjList(r);

			return new QuestcRecord
			{
				QuestId = questId,
				State = state,
				Zero = zero,
				N = n,
				Title = title,
				Summary = summary,
				Area = area,
				Region1 = region1,
				Region2 = region2,
				FieldEc = fieldEc,
				AreaId = areaId,
				Field11c = field11c,
				Accept = accept,
				Field28 = field28,
				FieldF0 = fieldF0,
				FieldF8 = fieldF8,
				Obj1 = obj1,
				Obj2 = obj2,
				Obj3 = obj3,
				Obj4 = obj4
			};
		}

		private static List<uint[]> ReadObjList(FaolanStream r)
		{
			var count = r.ReadUInt32();
			var list = new List<uint[]>((int)count);
			for (var i = 0; i < count; i++)
			{
				var rec = new uint[ObjRecWords];
				for (var j = 0; j < ObjRecWords; j++)
					rec[j] = r.ReadUInt32();
				list.Add(rec);
			}

			return list;
		}

		private static void WriteObjList(FaolanStream w, List<uint[]> list)
		{
			w.WriteUInt32(list.Count);
			foreach (var rec in list)
				foreach (var word in rec)
					w.WriteUInt32(word);
		}
	}
}
