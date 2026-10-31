using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Tests.Support
{
	// The capture corpus' test slice: the packet bodies the whole-corpus gates read, extracted from the 349 MB
	// capture repo so the suite needs one derived file rather than the corpus. It is the corpus half of the fixture
	// archive (FixtureArchive); the named fixtures in the same file are reached through Golden / TestPaths instead.
	//
	// Bodies are byte-identical to the corpus' `BodyHex`, and each carries its provenance as "<capture id>#<packet
	// index>" - the capture id being the opaque 8-hex-digit id `FixtureArchive.CorpusRecord.Source` describes, not
	// the dump's file name - so a byte-exact failure is still traceable to one packet in one capture, by
	// whoever holds the capture repo.
	//
	// The extract is derived, so a regeneration that silently drops packets would quietly weaken every gate while
	// they all still pass - the exact failure mode these gates exist to catch. The reader therefore ratchets on the
	// per-IIR counts below. A short extract throws instead of narrowing coverage.
	public static class CorpusExtract
	{
		// Pinned at the counts of the full corpus (120 tshark.json, one per capture). Regenerating against a smaller
		// corpus fails here rather than passing with fewer packets.
		//
		// Re-pinned 2026-07-25 when the corpus itself changed in both directions, which is the case this guard's error
		// message sanctions:
		//   * a byte-identical duplicate dump was removed - `..._leave_king_conan_castle-Dispmount` was the same
		//     capture as `...-Dismount` under a typo'd name (2583/2583 identical TimeUtc and BodyHex), dumped before the
		//     plugin emitted Direction. It cost -1 FullPlayerCharacter, -48 SimpleCharFullUpdate, -1 PlayfieldF.
		//   * `NecroL80_..._login_cast-general-abilities` had a pcapng but no dump, so it had never been analysed. It
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
			// Added 2026-08-05 so the constructed buff payload can be gated against every captured apply
			// instead of four hand-picked rows. 4,666 bodies = 2,929 applies (80 bytes) + 1,737 ends (26).
			((uint)IirTypes.MagicBuffIIR, 4666)
		];

		// The shape floor, and the reason it exists beside the body-count floor rather than instead of it.
		//
		// The count ratchet catches a regeneration that covers fewer bodies. It cannot catch one that keeps the
		// bodies and loses a grammar path - 8,332 of one SimpleDynel shape plus one Wharf Door is the same count as
		// 8,333 of the common one, and the Wharf Door is the row that found a lift failure (a156b3c0).
		//
		// Distinct body length is a proxy for that path, not a measure of it - two different grammars can share a
		// length, so this is a floor on coverage rather than a proof of it. It is worth pinning anyway because it
		// fails on a whole class of narrowing the count floor is blind to, and because it is the assertion that
		// survives a representative selection: one body per (IIR, length) keeps every number below while
		// collapsing every number above, so a shrunken archive can keep this gate and must replace the other.
		private static readonly (uint Iir, int Lengths)[] ExpectedShapes =
		[
			((uint)IirTypes.SimpleCharFullUpdateIIR, 1191),
			((uint)IirTypes.MoveTaskIIR, 137),
			((uint)IirTypes.SimpleDynelFullUpdateIIR, 101),
			((uint)IirTypes.FullPlayerCharacterIIR, 76),
			((uint)IirTypes.QuestFullUpdateIIR, 28),
			((uint)IirTypes.QuestJournalIIR, 14),
			((uint)IirTypes.MainQuestUpdateIIR, 13),
			((uint)IirTypes.MagicBuffIIR, 2),
			((uint)IirTypes.PlayfieldFIIR, 2),
			((uint)IirTypes.TradeskillReplyIIR, 1)
		];

		private static readonly Lazy<List<Entry>> Entries = new Lazy<List<Entry>>(Load);

		public static string Path => FixtureArchive.Path;

		// Every extracted packet, in capture order.
		public static IEnumerable<Entry> All()
		{
			return Entries.Value;
		}

		// The bodies of one IIR type. `Body` is the raw wire body including its leading u32 length, exactly as
		// `BodyHex` carried it - callers that want the payload skip the first 4 bytes, as they did before.
		public static IEnumerable<(string Source, byte[] Body)> OfType(uint iir)
		{
			return Entries.Value.Where(e => e.Iir == iir).Select(e => (e.Source, e.Body));
		}

		// The full packets of one IIR, for the extractors that take CapturedPacket and strip framing themselves.
		public static IEnumerable<Entry> PacketsOfType(uint iir)
		{
			return Entries.Value.Where(e => e.Iir == iir);
		}

		// The ratchet: a regeneration may only add packets. Fewer means the extract was built against a partial
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

			foreach (var (iir, expected) in ExpectedShapes)
			{
				var actual = entries.Where(e => e.Iir == iir).Select(e => e.Body.Length).Distinct().Count();
				if (actual < expected)
				{
					throw new InvalidDataException($"Corpus extract covers only {actual} distinct body lengths of IIR 0x{iir:X8}, expected at least {expected}. " +
					                               "The body COUNT can be unchanged while a grammar path disappears - that is what this floor catches " +
					                               "and the count floor cannot. Regenerate with the full corpus, or update ExpectedShapes if the corpus " +
					                               "itself legitimately changed.");
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

		// One extracted packet: the full rebuilt wire packet, plus the provenance and frame the gates report.
		public sealed record Entry(string Source, uint Iir, int Frame, uint Opcode, byte[] Full)
		{
			// The 0x2000 body within the full packet: [len4][crc4][headerLen4][header][body] - what the gates compare
			// against.
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
