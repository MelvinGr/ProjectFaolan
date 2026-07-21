using System;
using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The Quest_c (List A) region at the START of the 0x737A6DF9 FullChar snapshot tails - the quest JOURNAL.
	// Grammar recovered from the client reader FullPlayerCharacterIIR::ReadSubClass (FUN_0148c210 -> base
	// FUN_00797440 -> List A driver FUN_0091f5c0 -> per-record FUN_0091df50) and validated byte-exact against
	// BeachTailsTemplate.bin (docs/re/fullplayer-snapshot-reader.md, tools/capture/decode_questc.py).
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
	// FUN_0091df50 per-record) and validated byte-exact on NecroL80's lvl80 login — see docs/re/questc-region-decode.md.
	// Two fields below are MISLABELED (beach-template coincidence): `Marker`(asserted 0x412) is really the variable
	// `n` goal-count, and `QuestId2`(asserted ==questId) is really `field2c`; both asserts REJECT real progressed
	// snapshots. Each record also ends with a NESTED 0x3F1-framed per-goal PROGRESS sub-list (empty on the beach
	// template, so unmodeled here) whose entries carry a `"WTHD"` block that is **opaque server data with no client
	// reader** (FindConst = 0 hits) — the hard limit of client-only RE. Production is unaffected: fresh chars (the
	// only journals we generate) have empty objlists + empty progress. TODO(#186): parse/generate progressed
	// journals once the WTHD fields are pinned (needs a server spec or a progressed-capture corpus).
	public sealed class QuestcRegion : ICodec<QuestcRegion>
	{
		// The value the BEACH template carries in the record's `n` (goal-count) slot. It is NOT a marker and NOT a
		// constant on the wire - a progressed journal carries a real goal count here. Kept only so the fresh-char
		// generator reproduces the beach bytes exactly; never assert against it. See docs/re/questc-region-decode.md.
		public const uint BeachN = 0x412;
		public const uint Frame = ListFrame.Unit; // 1009 - the list-count framing multiplier
		public const uint RecordVersion = 1;
		public const uint Version = 0x18;

		public List<QuestcRecord> Records { get; init; } = [];

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

		// The <remoteformat> token text the client resolves by (id, category) against its RDB; `key` is the
		// pre-escaped deterministic content hash from QuestTextRef.KeyText. Matches WorldQuest.RemoteFormat's
		// exact spelling (incl. the trailing knubot="0" and two spaces) so it reproduces the captured bytes.
		public static string RemoteFormat(uint id, uint category, string? key)
		{
			return $"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}

		// The token-addressed form: `token="Noname"` instead of `id="0"`. See QuestObjectiveRegion.LocalizedToken.
		public static string RemoteFormatToken(string token, uint category, string? key)
		{
			return $"<remoteformat token=\"{token}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>";
		}
	}
}
