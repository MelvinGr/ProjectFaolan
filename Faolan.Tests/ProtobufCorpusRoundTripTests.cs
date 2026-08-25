using System;
using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Whole-corpus re-encode oracles for the two big protobuf messages - Gitea #385.
	//
	// Both messages already had a round-trip test, and both were pinned on LEVEL-1 STRIE fixtures:
	// FullCharIirRoundTripTests skips *.tshark.json outright, and PlayerCharacterMessage_RoundTripsByteIdentically
	// asserts on the single SpawnStrieTemplate golden. A level-1 beach character carries no character_tag and no
	// generic_stats, so the one field whose PACKING we had wrong was absent from every fixture either test could
	// see, and "round-trips byte-identically" held while the live wire form did not.
	//
	// These read the tshark corpus directly (like ProgressedJournalSnapshotTests) so the progressed characters are
	// actually covered, and they assert the populated case is present - a corpus gate that silently degrades to
	// the degenerate case is the failure mode being fixed here.
	public class ProtobufCorpusRoundTripTests
	{
		private readonly ITestOutputHelper _output;

		public ProtobufCorpusRoundTripTests(ITestOutputHelper output)
		{
			_output = output;
		}

		// ProtoFullCharacterIIR (the 0x737A6DF9 snapshot root) must re-encode byte-identically. character_tag
		// (repeated uint32, field 1) is PACKED on the wire in all 81 captured roots that carry it - never repeated
		// varints - so the schema marks it [packed=true] (#385).
		//
		// It used to tolerate ONE residual divergence, asserted narrowly: protobuf-net writes a repeated primitive
		// holding EXACTLY ONE element as an unpacked varint (`08 <v>`, 1 byte shorter) even when IsPacked is set -
		// the library's own optimization, not reachable from the schema, so 7 of the 151 roots came back a byte
		// short (#538). `ProtobufExtensions.ToProtobuf` re-packs those fields after the fact now, so the tolerance
		// is GONE and this is a plain byte-for-byte equality over every captured root.
		[Fact]
		public void EveryCapturedSnapshotRoot_ReEncodesByteIdentically()
		{
			var checkedRoots = 0;
			var withCharacterTags = 0;
			var singleTag = 0;

			foreach (var (source, body) in CapturedBodies((uint)IirTypes.FullPlayerCharacterIIR))
			{
				var stream = new FaolanStream(body);
				stream.ReadUInt32(); // subOpcode
				stream.ReadUInt32(); // typeTag
				stream.ReadUInt32(); // instance

				var start = stream.Position;
				stream.ReadArray(1); // prefix flag
				var rootLength = stream.ReadUInt32();
				stream.Position = start;
				stream.ReadArray(5); // the whole prefix
				var original = stream.ReadArray(rootLength);

				var root = original.FromProtobuf<ProtoFullCharacterIIR>();

				Assert.True(original.SequenceEqual(root.ToProtobuf()), $"{source}: the snapshot root must re-encode byte-identically");

				checkedRoots++;
				if (root.CharacterTags is { Length: > 0 })
					withCharacterTags++;
				if (root.CharacterTags is { Length: 1 })
					singleTag++;
			}

			_output.WriteLine($"{checkedRoots} snapshot roots re-encoded ({withCharacterTags} carrying character_tag, {singleTag} carrying exactly one)");
			Assert.True(checkedRoots > 0, "no snapshot roots found in the capture corpus");
			// The whole point of this gate: without tag-carrying roots it proves nothing the L1 fixtures didn't.
			Assert.True(withCharacterTags > 0, "corpus has no character_tag-carrying root - this gate would be vacuous");
			// And specifically the SINGLE-tag case, which is the one protobuf-net writes unpacked (#538). A corpus
			// without it would pass this gate while the re-pack was broken.
			Assert.True(singleTag > 0, "corpus has no single-character_tag root - the #538 packing case would go untested");
		}

		// ProtoCoreCharacterIIR (the FA015807 spawn's character message) must re-encode byte-identically.
		[Fact]
		public void EveryCapturedSpawnMessage_ReEncodesByteIdentically()
		{
			var checkedMessages = 0;
			var withGenericStats = 0;

			foreach (var (source, body) in CapturedBodies(0xFA015807u))
			{
				if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					continue;

				var original = body.AsSpan(read.Offset, read.Length).ToArray();
				var reencoded = read.Message.ToProtobuf();

				Assert.True(original.SequenceEqual(reencoded), $"{source}: ProtoCoreCharacterIIR must re-encode byte-identically");

				checkedMessages++;
				if (read.Message.GenericStats is { Length: > 0 })
					withGenericStats++;
			}

			_output.WriteLine($"{checkedMessages} spawn character messages re-encoded ({withGenericStats} carrying generic_stats)");
			Assert.True(checkedMessages > 0, "no spawn character messages found in the capture corpus");
		}

		// Yields the record body (everything after the leading u32 length) of every packet whose IIR type is
		// `iirType`, across the tshark corpus.
		private static IEnumerable<(string Source, byte[] Body)> CapturedBodies(uint iirType)
		{
			// Reads the committed CORPUS EXTRACT rather than the capture repo, so the suite runs from a plain clone.
			// Bodies are byte-identical to the corpus' BodyHex and keep their "<capture id>#<index>" provenance.
			foreach (var (source, body) in CorpusExtract.OfType(iirType))
			{
				// BodyHex is the body WITH its leading u32 length; callers want everything after it.
				var declared = body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				yield return (source, body[4..]);
			}
		}
	}
}
