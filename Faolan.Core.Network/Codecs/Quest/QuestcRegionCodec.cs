using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The Quest_c (List A) region at the START of the 0x737A6DF9 FullChar snapshot tails - the quest JOURNAL. Grammar
	// recovered from the client reader FullPlayerCharacterIIR::ReadSubClass (FUN_0148c210 -> base FUN_00797440 ->
	// List A driver FUN_0091f5c0 -> per-record FUN_0091df50) and validated byte-exact against BeachTailsTemplate.bin
	// (docs/re/fullplayer-snapshot-reader.md, tools/capture/decode_questc.py).
	//
	// Wire:  [u32 version = 0x18][u32 (records + 1) * 0x3F1]  then one QuestcRecord per held quest, each of which
	// carries its own interleaved @609 objective region (#384).
	//
	// #304 RE 2026-07-05: the FULL structure is decoded from the client readers and validated byte-exact on
	// NecroL80's lvl80 login - see docs/re/questc-region-decode.md. Each record also ends with a NESTED 0x3F1-framed
	// per-goal PROGRESS sub-list (empty on the beach template, so unmodeled) whose entries carry a `"WTHD"` block that
	// is opaque server data with no client reader (FindConst = 0 hits) - the hard limit of client-only RE. Production
	// is unaffected: fresh chars (the only journals we generate) have empty objlists + empty progress.
	public static class QuestcRegionCodec
	{
		public const uint Version = 0x18;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestcRegion value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static QuestcRegion Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestcRegionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestcRegion Read(ref SpanReader r)
		{
			r.ExpectU32Be(Version, "Quest_c region version");

			var records = r.ReadListCount();
			var region = new QuestcRegion();
			for (var i = 0; i < records; i++)
				region.Records.Add(QuestcRecordCodec.Read(ref r));

			return region;
		}

		internal static void Write(FaolanStream s, QuestcRegion v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(Version);
			s.WriteUInt32(ListFrame.ForCount(v.Records.Count));
			foreach (var record in v.Records)
				QuestcRecordCodec.Write(s, record);
		}
	}

	// The quest journal: the held quests, in wire order (primary first).
	public sealed record QuestcRegion
	{
		public List<QuestcRecord> Records { get; init; } = [];
	}
}
