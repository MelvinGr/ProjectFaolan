using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Everything an FA015807 post-IIR stream carries REGARDLESS of family (Gitea #475). The player's stream and the
	// NPC's differ in exactly one place - the entry slot in the middle - so that slot is all either codec reads for
	// itself, and this type is the rest of the stream:
	//
	//   [SpawnPostIirHeader]                        the header
	//   [firstRecordLen-4 prefix][u32 list frame]   the FIRST RECORD, which ENDS in the buff list's count frame
	//   [count x 60-byte ScaffoldBuff]              the active buffs
	//   >>>>>>>> the ENTRY SLOT <<<<<<<<            PLAYER: PlayerSpawnScaffoldCodec · NPC: NpcSpawnPostIir
	//   [u32 c1][c1 x 8][u32 c2][c2 x 8]            two PLAIN-counted pair lists
	//   [u32 list frame][n x 8]                     the count-framed third
	//
	// The two families are kept as two codecs on purpose - they mean different things and each asserts what its own
	// family guarantees - but everything they agree on lives here once, so a correction lands in one place.
	//
	// ⚠️ THE BUFF COUNT IS READ, NEVER SEARCHED. The last four bytes of the first record are that list's framing word
	// on all 11,632 captured streams, without exception. This was not obvious from the player side alone: a player's
	// first record IS the frame word (length 4, no prefix), so the frame reads as "the record", while an NPC's carries
	// a prefix in front of it - one 0x00 byte on a fresh spawn, up to 1,273 bytes of accrued runtime state
	// mid-session. Reading it rather than searching for the record count is what takes the corpus-wide walk from
	// 99.95% to 11,632/11,632, INCLUDING the streams a k-search could not resolve. Gate: SpawnPostIirGrammarTests.
	public sealed class SpawnPostIirCommon
	{
		// The stream's opening header (shared already, since #475's first pass).
		public SpawnPostIirHeader Header { get; set; } = new();

		// The first record's bytes BEFORE the buff list's framing word - the one region of the post-IIR still carried
		// opaque. Bounded and well-censused: EMPTY on every player stream, a single 0x00 byte on the 9,694 captured
		// fresh NPC streams, and 20..1,273 bytes of accrued runtime state on the ~1,600 mid-session ones. A
		// from-scratch build emits the fresh form, so nothing we construct depends on the unknown shape.
		public byte[] FirstRecordPrefix { get; set; } = [];

		public List<ScaffoldBuff> BuffRecords { get; } = [];

		// The first of the three closing lists IS the faction-reputation block (#315); see ScaffoldPair.
		public List<ScaffoldPair> EndPairs { get; } = [];
		public List<ScaffoldPair> EndPairs2 { get; } = [];

		// The third, and the stream's last field. Note the asymmetry, which is what the bytes say rather than what is
		// tidy: the two above are framed by a PLAIN count, this one by a ListFrame word. It was read as a closing
		// 0x3F1 constant for years because ForCount(0) IS 0x3F1 and it is empty on all but 10 captured spawns.
		public List<ScaffoldPair> EndPairs3 { get; } = [];

		// Reads the header, the first record and the buff list from `post` at `at`. Returns where the ENTRY SLOT
		// begins - the one part the caller reads for itself. Throws FormatException when the stream is not this
		// shape; a caller that probes rather than parses catches it (NpcSpawnPostIir.TryDecode).
		public static SpawnPostIirCommon ReadPrologue(byte[] post, int at, out int entrySlot)
		{
			if (SpawnPostIirHeader.Read(post, at) is not { } header)
				throw new FormatException("post-IIR is too short to hold its header");
			if (header.FirstRecordLen < 4)
				throw new FormatException($"first record length {header.FirstRecordLen} cannot hold the buff list's framing word");

			var common = new SpawnPostIirCommon { Header = header };
			var i = at + header.ByteCount;
			var frame = i + (int)header.FirstRecordLen - 4;
			if (frame + 4 > post.Length)
				throw new FormatException("the first record runs past the stream");

			common.FirstRecordPrefix = post[i..frame];

			var word = post.ReadU32Be(frame);
			if (!ListFrame.TryCountOf(word, out var count))
				throw new FormatException($"the buff list's framing word 0x{word:X} is not count-framed");

			i = frame + 4;
			for (var n = 0; n < count; n++)
			{
				if (i + ScaffoldBuff.ByteCount > post.Length)
					throw new FormatException($"buff record {n} runs past the stream");

				common.BuffRecords.Add(ScaffoldBuff.Read(post, ref i));
			}

			entrySlot = i;
			return common;
		}

		// Reads the three closing pair lists at `at` and REQUIRES them to consume the stream to its last byte. That
		// end-exactness is the check doing the real work in this grammar: a mis-parsed entry slot lands somewhere
		// else, so a walk that ends on post.Length is a walk that read the whole thing.
		public void ReadTail(byte[] post, int at)
		{
			var i = at;
			ReadPairs(post, ref i, EndPairs);
			ReadPairs(post, ref i, EndPairs2);

			if (i + 4 > post.Length)
				throw new FormatException("the stream ends before its final list word");

			var word = post.ReadU32Be(i);
			i += 4;
			if (!ListFrame.TryCountOf(word, out var count))
				throw new FormatException($"final list word 0x{word:X} is not count-framed");

			for (var n = 0; n < count; n++)
			{
				if (i + 8 > post.Length)
					throw new FormatException($"final pair {n} runs past the stream");

				EndPairs3.Add(new ScaffoldPair(post.ReadU32Be(i), (int)post.ReadU32Be(i + 4)));
				i += 8;
			}

			if (i != post.Length)
				throw new FormatException($"{post.Length - i} trailing bytes after the final list");
		}

		public void WritePrologue(FaolanStream stream)
		{
			if (Header.FirstRecordLen != FirstRecordPrefix.Length + 4)
				throw new InvalidOperationException(
					$"the header's first-record length {Header.FirstRecordLen} disagrees with the {FirstRecordPrefix.Length}-byte prefix + the buff list's 4-byte framing word");

			stream.WriteArray(Header.ToArray()).WriteArray(FirstRecordPrefix).WriteUInt32(ListFrame.ForCount(BuffRecords.Count));
			foreach (var buff in BuffRecords)
				buff.Write(stream);
		}

		public void WriteTail(FaolanStream stream)
		{
			foreach (var pairs in new[] { EndPairs, EndPairs2 })
			{
				stream.WriteUInt32((uint)pairs.Count);
				foreach (var pair in pairs)
					stream.WriteUInt32(pair.Id).WriteUInt32((uint)pair.Value);
			}

			// ForCount(0) IS 0x3F1, so an empty third list regenerates the exact closing word it always did.
			stream.WriteUInt32(ListFrame.ForCount(EndPairs3.Count));
			foreach (var pair in EndPairs3)
				stream.WriteUInt32(pair.Id).WriteUInt32((uint)pair.Value);
		}

		// A PLAIN-counted pair list: a bare count, not a ListFrame word. The one list spelling ListFrame does not
		// cover, and the reason the head-word list at the top of the header was misread as a constant too.
		private static void ReadPairs(byte[] post, ref int i, List<ScaffoldPair> pairs)
		{
			if (i + 4 > post.Length)
				throw new FormatException("the stream ends before a closing pair list's count");

			var count = post.ReadU32Be(i);
			i += 4;
			if (count > 0x100)
				throw new FormatException($"implausible pair count {count}");

			for (var n = 0; n < count; n++)
			{
				if (i + 8 > post.Length)
					throw new FormatException($"closing pair {n} runs past the stream");

				pairs.Add(new ScaffoldPair(post.ReadU32Be(i), (int)post.ReadU32Be(i + 4)));
				i += 8;
			}
		}
	}
}
