using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     The Quest_c (List A) region at the start of the FullChar snapshot tails - the quest journal. The body is
	///     <c>[u32 version][ListFrame(records)]</c> then one <see cref="QuestcRecord" /> per held quest, each carrying its
	///     own interleaved @609 objective region.
	/// </summary>
	/// <remarks>
	///     Grammar recovered from the client reader FullPlayerCharacterIIR::ReadSubClass (FUN_0148c210, base FUN_00797440,
	///     List A driver FUN_0091f5c0, per-record FUN_0091df50) and validated byte-exact against BeachTailsTemplate.bin and
	///     against a level-80 login; see docs/re/fullplayer-snapshot-reader.md, docs/re/questc-region-decode.md and
	///     tools/capture/decode_questc.py.
	///     <para>
	///         Each record also ends with a nested count-framed per-goal progress sub-list, empty on the beach template and
	///         therefore unmodelled, whose entries carry a "WTHD" block that is opaque server data with no client reader.
	///         The journals we generate are for fresh characters, whose reward lists and progress are both empty.
	///     </para>
	/// </remarks>
	public static class QuestcRegionCodec
	{
		public const uint Version = 0x18;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The quest journal: the held quests, in wire order, primary first.</summary>
	public sealed record QuestcRegion
	{
		public List<QuestcRecord> Records { get; init; } = [];
	}
}
