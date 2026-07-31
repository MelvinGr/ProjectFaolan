using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Quest
{
	// The "currently tracked quest" pointer block that CLOSES a QuestJournal:
	//   [u32 trackedQuestId] [list A] [list B]      each list = [u32 (count + 1) * 0x3F1][u32 count][entries]
	//
	// Decoded byte-exact from every captured single-quest journal, all of which carry the NO-TRACK state
	// (trackedQuestId 0, both lists empty). The server does not drive quest TRACKING - the client picks which quest to
	// follow - so this emits that no-track form from typed fields instead of a hex blob.
	//
	// A POPULATED tracked form has never been captured, so the per-entry grammar is unknown. Parse THROWS on a
	// non-empty list rather than guessing one (#193 owns the capture); silently skipping would put an invented
	// grammar on the wire the first time the client sent a real one.
	public static class TrackedQuestPointerCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TrackedQuestPointer value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static TrackedQuestPointer Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TrackedQuestPointerCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static TrackedQuestPointer Read(ref SpanReader r)
		{
			var trackedQuestId = r.ReadU32Be();
			ReadEmptyList(ref r, "list A");
			ReadEmptyList(ref r, "list B");
			return new TrackedQuestPointer(trackedQuestId);
		}

		internal static void Write(FaolanStream s, TrackedQuestPointer v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(v.TrackedQuestId);
			s.WriteUInt32(ListFrame.Empty).WriteUInt32(0u); // list A
			s.WriteUInt32(ListFrame.Empty).WriteUInt32(0u); // list B
		}

		private static void ReadEmptyList(ref SpanReader r, string which)
		{
			var at = r.Position;
			var framed = r.ReadListCount();
			var count = r.ReadU32Be();
			if (framed != 0 || count != 0)
				throw CodecParseException.Unexpected(nameof(TrackedQuestPointerCodec), at, $"an EMPTY {which} (the only tracked form ever captured - see #193)", $"{framed} framed / {count} declared");
		}
	}

	// The tracked-quest pointer. 0 = no tracked quest, which is every captured journal.
	public sealed record TrackedQuestPointer(uint TrackedQuestId = 0);
}
