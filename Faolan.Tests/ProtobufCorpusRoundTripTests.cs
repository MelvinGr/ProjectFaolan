using System;
using System.Buffers.Binary;
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
		// ONE residual divergence is accepted, and asserted narrowly rather than excluded: protobuf-net writes a
		// repeated primitive holding EXACTLY ONE element as an unpacked varint (`08 <v>`, 1 byte shorter) even when
		// IsPacked is set - verified to be the library's own optimization, identical for uint[] and List<uint>, and
		// not reachable from the schema. Two or more elements pack correctly. Both forms are valid protobuf and the
		// client accepts either, so this is a cosmetic byte-count difference, not a wire break. The assert below
		// still pins it exactly: the ONLY tolerated difference is that leading field-1 encoding on a single-tag
		// root, and every other byte must match. Any new drift fails.
		[Fact]
		public void EveryCapturedSnapshotRoot_ReEncodesByteIdentically()
		{
			var checkedRoots = 0;
			var withCharacterTags = 0;
			var singleTagUnpacked = 0;

			foreach (var (source, body) in CapturedBodies((uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR))
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

				var reencoded = root.ToProtobuf();
				if (!original.SequenceEqual(reencoded))
				{
					AssertOnlyDifferenceIsSingleTagPacking(source, original, reencoded, root);
					singleTagUnpacked++;
				}

				checkedRoots++;
				if (root.CharacterTags is { Length: > 0 })
					withCharacterTags++;
			}

			_output.WriteLine($"{checkedRoots} snapshot roots re-encoded ({withCharacterTags} carrying character_tag, " + $"{singleTagUnpacked} differing only by the single-tag packing quirk)");
			Assert.True(checkedRoots > 0, "no snapshot roots found in the capture corpus");
			// The whole point of this gate: without tag-carrying roots it proves nothing the L1 fixtures didn't.
			Assert.True(withCharacterTags > 0, "corpus has no character_tag-carrying root - this gate would be vacuous");
		}

		// ProtoCoreCharacterIIR (the FA015807 spawn's character message) must re-encode byte-identically.
		[Fact]
		public void EveryCapturedSpawnMessage_ReEncodesByteIdentically()
		{
			var checkedMessages = 0;
			var withGenericStats = 0;

			foreach (var (source, body) in CapturedBodies(0xFA015807u))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
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

		// The only tolerated re-encode difference: a root holding exactly ONE character_tag, where the capture has
		// `0A <len> <payload>` (packed) and protobuf-net emits `08 <payload>` (unpacked). Everything after that
		// leading field must be byte-identical.
		private static void AssertOnlyDifferenceIsSingleTagPacking(string source, byte[] original, byte[] reencoded, ProtoFullCharacterIIR root)
		{
			Assert.True(root.CharacterTags is { Length: 1 }, $"{source}: root re-encode differs but does not hold exactly one character_tag");

			// Captured: 0A <len> <payload>. Regenerated: 08 <payload>. Same payload, one byte apart.
			Assert.Equal(0x0A, original[0]);
			Assert.Equal(0x08, reencoded[0]);
			var payloadLength = original[1];
			Assert.Equal(original.Length - 1, reencoded.Length);
			Assert.True(original.AsSpan(2, payloadLength).SequenceEqual(reencoded.AsSpan(1, payloadLength)), $"{source}: the single tag's payload bytes must match");
			Assert.True(original.AsSpan(2 + payloadLength).SequenceEqual(reencoded.AsSpan(1 + payloadLength)), $"{source}: every byte after character_tag must be identical");
		}

		// Yields the record body (everything after the leading u32 length) of every packet whose IIR type is
		// `iirType`, across the tshark corpus.
		private static IEnumerable<(string Source, byte[] Body)> CapturedBodies(uint iirType)
		{
			// Reads the committed CORPUS EXTRACT rather than the capture repo, so the suite runs from a plain clone.
			// Bodies are byte-identical to the corpus' BodyHex and keep their "<file>#<index>" provenance.
			foreach (var (source, body) in CorpusExtract.OfType(iirType))
			{
				// BodyHex is the body WITH its leading u32 length; callers want everything after it.
				var declared = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				yield return (source, body[4..]);
			}
		}
	}
}
