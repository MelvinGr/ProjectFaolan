using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Tests.Support
{
	// The capture corpus' TEST SLICE: the packet bodies the whole-corpus gates read, extracted from the 349 MB
	// capture repo so the suite runs from a plain clone. It is the corpus half of `Faolan.Tests/Assets/fixtures.bin`
	// (FixtureArchive); the named fixtures in the same file are reached through Golden / TestPaths instead.
	//
	// Bodies are byte-identical to the corpus' `BodyHex`, and each carries its provenance as "<capture file>#<packet
	// index>" - the same string the gates printed when they read the JSON directly, so a byte-exact failure is still
	// traceable to one packet in one capture.
	//
	// ⚠️ The extract is DERIVED, so a regeneration that silently drops packets would quietly WEAKEN every gate while
	// they all still pass - the exact failure mode these gates exist to catch. The reader therefore ratchets on the
	// per-IIR counts below. A short extract throws instead of narrowing coverage.
	public static class CorpusExtract
	{
		// Pinned at the counts of the full corpus (120 tshark.json, one per capture). Regenerating against a SMALLER
		// corpus fails here rather than passing with fewer packets.
		//
		// Re-pinned 2026-07-25 when the corpus itself changed in both directions, which is the case this guard's error
		// message sanctions:
		//   * a byte-identical DUPLICATE dump was removed - `..._leave_king_conan_castle-Dispmount` was the same
		//     capture as `...-Dismount` under a typo'd name (2583/2583 identical TimeUtc and BodyHex), dumped before the
		//     plugin emitted Direction. It cost -1 FullPlayerCharacter, -48 SimpleCharFullUpdate, -1 PlayfieldF.
		//   * `NecroL80_..._login_cast-general-abilities` had a pcapng but NO dump, so it had never been analysed. It
		//     adds +8 MoveTask.
		// Re-pinned 2026-07-31 on the first regeneration since 2026-07-27: nine captures added and every dump rebuilt
		// with the current dissector, which is +51% bodies (30,158 -> 45,520). Every count grew; that is the direction
		// this ratchet allows. A drop with no such explanation is the failure it exists to catch - do not just lower
		// the numbers.
		private static readonly (uint Iir, int Count)[] ExpectedCounts =
		[
			((uint)IirTypes.FullPlayerCharacterIIR, 184),
			((uint)IirTypes.SimpleCharFullUpdateIIR, 17717),
			((uint)IirTypes.MoveTaskIIR, 18932),
			((uint)IirTypes.QuestFullUpdateIIR, 79),
			((uint)IirTypes.QuestJournalIIR, 42),
			((uint)IirTypes.MainQuestUpdateIIR, 48),
			((uint)IirTypes.PlayfieldFIIR, 184),
			((uint)IirTypes.TradeskillReplyIIR, 1),
			((uint)IirTypes.SimpleDynelFullUpdateIIR, 8333),
			// #617: added 2026-08-05 so the constructed buff payload can be gated against every captured apply
			// instead of four hand-picked rows. 4,666 bodies = 2,929 applies (80 bytes) + 1,737 ends (26).
			((uint)IirTypes.MagicBuffIIR, 4666)
		];

		private static readonly Lazy<List<Entry>> Entries = new Lazy<List<Entry>>(Load);

		public static string Path => FixtureArchive.Path;

		// Every extracted packet, in capture order.
		public static IEnumerable<Entry> All()
		{
			return Entries.Value;
		}

		// The bodies of one IIR type. `Body` is the raw wire body INCLUDING its leading u32 length, exactly as
		// `BodyHex` carried it - callers that want the payload skip the first 4 bytes, as they did before.
		public static IEnumerable<(string Source, byte[] Body)> OfType(uint iir)
		{
			return Entries.Value.Where(e => e.Iir == iir).Select(e => (e.Source, e.Body));
		}

		// The FULL packets of one IIR, for the extractors that take CapturedPacket and strip framing themselves.
		public static IEnumerable<Entry> PacketsOfType(uint iir)
		{
			return Entries.Value.Where(e => e.Iir == iir);
		}

		// The ratchet: a regeneration may only ADD packets. Fewer means the extract was built against a partial
		// corpus, and every gate reading it would silently cover less.
		private static void AssertCoverage(List<Entry> entries)
		{
			foreach (var (iir, expected) in ExpectedCounts)
			{
				var actual = entries.Count(e => e.Iir == iir);
				if (actual < expected)
				{
					throw new InvalidDataException($"Corpus extract covers only {actual} packets of IIR 0x{iir:X8}, expected at least {expected}. " +
					                               "It was probably regenerated against a partial capture corpus - the whole-corpus gates would " +
					                               "still pass while testing less. Regenerate with the full corpus, or update ExpectedCounts if " + "the corpus itself legitimately changed.");
				}
			}
		}

		private static List<Entry> Load()
		{
			var entries = FixtureArchive.Corpus
				.Select(e => new Entry($"{e.Source}#{e.Index}", e.Iir, e.Frame, e.Opcode, e.Full))
				.ToList();

			AssertCoverage(entries);
			return entries;
		}

		// One extracted packet: the FULL rebuilt wire packet, plus the provenance and frame the gates report.
		public sealed record Entry(string Source, uint Iir, int Frame, uint Opcode, byte[] Full)
		{
			// The 0x2000 body within the full packet: [len4][crc4][headerLen4][header][body]. This is what BodyHex
			// held, so every gate that used to read BodyHex gets identical bytes.
			public byte[] Body
			{
				get
				{
					var headerLength = (int)Full.ReadU32Be(8);
					return Full[(12 + headerLength)..];
				}
			}
		}
	}
}
