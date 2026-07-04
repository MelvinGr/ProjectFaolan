using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Packets.FullCharSnapshot
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
	// for retiring BeachTailsTemplate.bin's quest region (Gitea #186). WIRED into SnapshotTails (it models the
	// tails' front region instead of the former opaque header+string fields) - the live snapshot is byte-
	// identical (BeachSnapshotConstructionTests). Generating DIFFERENT quests from a character's CharacterQuests
	// is the next step and is gated by an in-client chat-connect smoke test.
	//
	// #304 RE 2026-07-05 (Ghidra FUN_0091df50 = the per-record reader, decompiled; validated 4-u32 helper
	// FUN_00cc0540, ReadString FUN_0048f840, ReadU64 FUN_0048f720). The REAL per-record grammar is:
	//   ver(=1) · questId@+0xc · state@+0x10 · field114@+0x114 · field118@+0x118 · n@+0x14 · 5×string ·
	//   fieldEc@+0xec · areaId@+0xcc · field2c@+0x2c · field11c@+0x11c · accept:u64@+0x20 · field28@+0x28 ·
	//   objlist1(+0x10c) · objlist2(+0x110) · fieldF0:u32@+0xf0 · fieldF8:u64@+0xf8 · objlist3(+0x100) ·
	//   objlist4(+0x104) · <a NESTED 0x3F1-framed sub-list — see below>.
	// Each ObjRec IS 16 u32 (confirmed: 2 words + 4-word cc0540 + 2 words + 4-word cc0540 + 2 words, scrambled
	// into an 18-word struct). TWO CORRECTIONS to the model below: (1) the word this codec calls `Marker`/`0x412`
	// is actually the variable `n` (goal count) — the `!= 0x412` assert only holds for the beach template and
	// REJECTS real progressed snapshots (NecroL80's 27-quest login: n=2 in rec0); (2) `field2c` is NOT a second
	// questId (the `questId2 == questId` assert is likewise beach-only).
	//
	// KEY 2026-07-05 FINDING (why the beach codec can't parse real snapshots — validated on NecroL80's rec0 bytes):
	// after obj4 each quest record carries a NESTED `0x3F1`-framed sub-list `[u32 0x3F1*(k+1)][k × nested-record]`
	// (rec0: `0x7E2 = 0x3F1*2` ⇒ k=1; the nested record then carries the PLAYER HANDLE 0x0802E5D4 + quest/goal
	// ids — i.e. per-character goal PROGRESS, which the empty beach template never exercised). So the real
	// Quest_c record is deeply nested, NOT the flat header+4-objlists the beach round-trip suggested. The beach
	// golden test still passes (that template has empty objlists and no nested progress). To parse ARBITRARY
	// login snapshots the nested sub-list reader must be decompiled (Ghidra: FUN_0182d4c9 is the sub-call between
	// obj2 and F0; the driver is FUN_0091f5c0, per-record reader FUN_0091df50). Header grammar above is fully
	// decoded + validated on NecroL80's rec0.
	//
	// FULL MAP (deep RE 2026-07-05, validated byte-exact on NecroL80's rec0 @ snapshot offset 11024):
	//   record = [listKey=questId] header(above) obj1 obj2 [F0:u32][F8:u64] obj3 obj4
	//            [nested PROGRESS sub-list: u32 frame=0x3F1*(k+1); k × (4 u32: flag=1, goalId, PLAYER HANDLE,
	//             questId)]   // rec0: frame=0x7E2 ⇒ k=1, entry=[1, 12, 0x0802E5D4, 13907]
	//   Each ObjRec = 16 u32 (reader FUN_0091df50 calls 21-31 / 71-81; the 4-u32 helper is FUN_00cc0540; the
	//   post-obj4 calls 84-92 are std::sort (FUN_009202d0 introsort) + vector-push, NOT reads). rec0 round-trips.
	//   BUT the per-record structure is VARIABLE: rec1 (questId 6451) has EMPTY strings + n=0 and embeds a
	//   `"WTHD"` (0x57544844) magic sub-block — so progressed/other quest kinds carry extra magic-tagged blocks
	//   this flat model does not cover. Fully reconstructing every variant (WTHD block grammar, etc.) is a
	//   dedicated multi-session decode; rec0 + the nested progress list + the header are DONE. The beach golden
	//   test stays byte-exact. TODO(#186/#304): decode the WTHD sub-block + remaining record variants, drop the
	//   two beach-only asserts (Marker→n, questId2→field2c), then generate/parse arbitrary login quest journals.
	public sealed class QuestcRegion
	{
		public const uint Frame = 0x3F1; // 1009 - the list-count framing multiplier
		public const uint Marker = 0x412;
		public const uint RecordVersion = 1;
		public const uint Version = 0x18;

		public List<QuestcRecord> Records { get; init; } = [];

		public byte[] ToArray()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(Version);
			w.WriteUInt32(((uint)Records.Count + 1) * Frame);
			foreach (var record in Records)
				record.Write(w);
			return w.ToArray();
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
			consumed = checked((int)r.Position);
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

	public sealed class QuestcRecord
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
			w.WriteUInt32((uint)list.Count);
			foreach (var rec in list)
			foreach (var word in rec)
				w.WriteUInt32(word);
		}
	}
}
