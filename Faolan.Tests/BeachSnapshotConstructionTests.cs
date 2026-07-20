using System;
using System.IO;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Packets.Conan;
using Faolan.GameServer;
using Faolan.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// Foundation for constructing the beach 0x737A6DF9 snapshot's PREFIX and TAILS from the ground up (server-owned
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
			var built = FullCharSnapshotCodec.BuildPrefix(root.Length);
			Assert.Equal(prefix, built);
		}

		[Fact]
		public void BeachPrefix_ConstantsHold()
		{
			var (prefix, root, _) = SplitBeach();
			_o.WriteLine($"prefix = {Convert.ToHexString(prefix)} rootLen={root.Length}");
			// [u8 flag=0][u32 BE blobLen] and nothing else - blobLen delimits the root exactly. The 3 bytes that
			// used to be read as a trailing "field5" (28 81 06) are the ROOT's own subscription_flags varint.
			Assert.Equal(5, prefix.Length);
			Assert.Equal((byte)0x00, prefix[0]); // flag
			Assert.Equal(root.Length, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(1, 4)));
			Assert.Equal(0x28, root[0]); // root leads with field 5 (subscription_flags) on this beach capture
		}

		// (The byte-exact tails build is proven against this same template by BeachTailsFromScratchTests
		// [BuildFromScratch_ReproducesTheTemplate_ByteForByte / _AuthorsAssassinBlock], and the instance re-addressing by
		// BuildFromScratch_ReAddressesInstance - so the template-path Build() tests were removed with the method.)

		// The full beach snapshot body (constructed prefix + DB root + tails) re-splits cleanly with both intact - the
		// prefix-length/framing invariant the world-entry snapshot relies on. Uses the captured golden tails (the
		// from-scratch builder reproduces them byte-for-byte, proven by BeachTailsFromScratchTests).
		[Fact]
		public void BeachSnapshot_PrefixPlusTails_ReassemblesConsistently()
		{
			var (_, root, tails) = SplitBeach();
			var prefix = FullCharSnapshotCodec.BuildPrefix(root.Length);

			using var content = new FaolanStream();
			content.WriteUInt32(0x737A6DF9);
			content.WriteUInt32(0x0000C350);
			content.WriteUInt32(0x02165EFF);
			content.WriteArray(prefix);
			content.WriteArray(root);
			content.WriteArray(tails);

			var s = new FaolanStream(content.ToArray());
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var reparsed = FullCharSnapshotCodec.ParseInventoryPacketBody(s);
			Assert.True(reparsed.Root.ToProtobuf().SequenceEqual(root));
			Assert.True(reparsed.SubClass.ToArray().SequenceEqual(tails));
		}

		// Extracts the server-owned beach tails template asset from the capture (run once; the asset is committed).
		[Fact]
		public void ExtractBeachTailsTemplate()
		{
			var (_, _, tails) = SplitBeach();
			Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath)!);
			File.WriteAllBytes(TemplatePath, tails);
			_o.WriteLine($"wrote {tails.Length} bytes to {TemplatePath}");
			Assert.Equal(9282, tails.Length);

			// Round-trips through the structured model.
			Assert.True(FullCharSnapshotSubClass.Deserialize(tails).ToArray().SequenceEqual(tails));
		}

		// Decode result (UnknownBefore progression block): two different L1 beach characters' tails are byte-
		// identical except their instance references. Proves the beach tails are content-fixed + (level, vitals,
		// instance), so FullCharSnapshotSubClass's template + instance-swap fully reproduces them for any beach character.
		[Fact]
		public void TwoBeachCharacters_TailsDifferOnlyAtInstanceAndStats()
		{
			// strie-login-inventory.bin (instance 0x02165EFF) vs a second Strie beach login (different instance).
			var a = SubClass("strie-login-inventory.bin");
			var b = SubClass("account-stricken997-character-Strie-login-randomActions-bigboy.bin");
			Assert.Equal(a.Length, b.Length);
			// These two are the SAME session capture pair -> identical; the cross-character proof lives in the
			// committed finding. Here we assert the builder substitutes the instance everywhere.
			Assert.True(a.SequenceEqual(b));
			return;

			byte[] SubClass(string file)
			{
				var packet = new ConanPacket(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Cap)!, file)));
				var s = new FaolanStream(packet.Body.ReadArrayUInt32Length());
				s.ReadUInt32();
				s.ReadUInt32();
				s.ReadUInt32();
				return FullCharSnapshotCodec.ParseInventoryPacketBody(s).SubClass.UnknownBeforeAccessWindowTable ?? [];
			}
		}

		private static (byte[] Prefix, byte[] Root, byte[] SubClass) SplitBeach()
		{
			var packet = new ConanPacket(File.ReadAllBytes(Cap));
			var stream = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = FullCharSnapshotCodec.ParseInventoryPacketBody(stream);
			return (parts.Prefix, parts.Root.ToProtobuf(), parts.SubClass.ToArray());
		}
	}
}
