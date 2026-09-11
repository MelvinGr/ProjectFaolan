using System;
using System.IO;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Foundation for constructing the beach 0x737A6DF9 snapshot's prefix and tails from the ground up (server-owned
	// template + generated fields) instead of patching the captured burst packet. Pins the prefix constants and
	// proves a from-scratch prefix reproduces the capture; extracts the tails template asset.
	public class BeachSnapshotConstructionTests
	{
		private static readonly string Cap = TestPaths.Asset("strie-login-inventory.bin");

		// Where the server-owned beach tails template asset lives in the source tree.
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");
		private readonly ITestOutputHelper _o;

		public BeachSnapshotConstructionTests(ITestOutputHelper o)
		{
			_o = o;
		}

		[Fact]
		public void BeachPrefix_BuildsFromScratch_ByteIdentical()
		{
			var (prefix, root, _) = SplitBeach();
			var built = SnapshotPackets.BuildPrefix(root.Length);
			Assert.Equal(prefix, built);
		}

		[Fact]
		public void BeachPrefix_ConstantsHold()
		{
			var (prefix, root, _) = SplitBeach();
			_o.WriteLine($"prefix = {Convert.ToHexString(prefix)} rootLen={root.Length}");
			// [u8 flag=0][u32 BE blobLen] and nothing else - blobLen delimits the root exactly. The 3 bytes 28 81 06
			// that follow are the root's own subscription_flags varint, not a trailing field after the root.
			Assert.Equal(5, prefix.Length);
			Assert.Equal((byte)0x00, prefix[0]); // flag
			Assert.Equal(root.Length, (int)prefix.ReadU32Be(1));
			Assert.Equal(0x28, root[0]); // root leads with field 5 (subscription_flags) on this beach capture
		}

		// (The byte-exact tails build is proven against this same template by SnapshotTailsFromScratchTests
		// [BuildFromScratch_ReproducesTheTemplate_ByteForByte / _AuthorsAssassinBlock], and the instance re-addressing by
		// BuildFromScratch_ReAddressesInstance - so the template-path Build() tests were removed with the method.)

		// The full beach snapshot body (constructed prefix + DB root + tails) re-splits cleanly with both intact - the
		// prefix-length/framing invariant the world-entry snapshot relies on. Uses the captured golden tails (the
		// from-scratch builder reproduces them byte-for-byte, proven by SnapshotTailsFromScratchTests).
		[Fact]
		public void BeachSnapshot_PrefixPlusTails_ReassemblesConsistently()
		{
			var (_, root, tails) = SplitBeach();
			var prefix = SnapshotPackets.BuildPrefix(root.Length);

			using var content = new FaolanStream();
			content.WriteUInt32(IirTypes.FullPlayerCharacterIIR);
			content.WriteUInt32(0x0000C350);
			content.WriteUInt32(0x02165EFF);
			content.WriteArray(prefix);
			content.WriteArray(root);
			content.WriteArray(tails);

			var s = new FaolanStream(content.ToArray());
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var reparsed = SnapshotPackets.Parse(s);
			Assert.True(reparsed.Root.ToProtobuf().SequenceEqual(root));
			Assert.True(FullCharSnapshotSubClassCodec.Build(reparsed.SubClass).SequenceEqual(tails));
		}

		// Verifies that the committed beach tails template still matches what the capture yields - it does not rewrite
		// it. Set FAOLAN_UPDATE_ASSETS=1 to regenerate, the same convention ProtocolDocsTests uses for the generated
		// docs.
		//
		// The write must stay behind that switch. Several other test classes read this asset (SubClassStatBlockTests,
		// QuestcRegionTests, AccessWindowCatalogTests, KnownSpellListTests, SnapshotTailsFromScratchTests) and xUnit runs
		// test classes in parallel, so an unconditional rewrite lets a reader land mid-write, see a truncated file and
		// fail deep in a parser ("EndOfStreamException at QuestcRegion.Read") - in a different test each run.
		//
		// Verifying rather than regenerating is also strictly better than just gating the write: if the asset ever
		// drifts from the capture it fails instead of silently rewriting itself.
		[Fact]
		public void BeachTailsTemplate_MatchesTheCapture()
		{
			var (_, _, tails) = SplitBeach();
			Assert.Equal(9282, tails.Length);

			// Round-trips through the structured model.
			Assert.True(FullCharSnapshotSubClassCodec.Build(FullCharSnapshotSubClassCodec.Parse(tails)).SequenceEqual(tails));

			if (Environment.GetEnvironmentVariable("FAOLAN_UPDATE_ASSETS") == "1")
			{
				Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath)!);
				File.WriteAllBytes(TemplatePath, tails);
				_o.WriteLine($"FAOLAN_UPDATE_ASSETS=1: wrote {tails.Length} bytes to {TemplatePath}");
				return;
			}

			Assert.True(File.Exists(TemplatePath), $"{TemplatePath} is missing - regenerate with FAOLAN_UPDATE_ASSETS=1");
			Assert.True(File.ReadAllBytes(TemplatePath).SequenceEqual(tails),
				"the committed BeachTailsTemplate.bin no longer matches what the capture yields - regenerate with FAOLAN_UPDATE_ASSETS=1 if that is intended");
		}

		private static (byte[] Prefix, byte[] Root, byte[] SubClass) SplitBeach()
		{
			var packet = Rpc.Parse(File.ReadAllBytes(Cap));
			var stream = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = SnapshotPackets.Parse(stream);
			return (SnapshotPackets.PrefixOf(parts), parts.Root.ToProtobuf(), FullCharSnapshotSubClassCodec.Build(parts.SubClass));
		}
	}
}
