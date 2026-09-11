using System;
using System.IO;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Golden regression guard for the real 0x737A6DF9 root framing, recovered from AgeOfConan.exe (Ghidra,
	// 2026-06-13): n3InfoItemRemote::Construct -> FUN_00cbfb80 reads the protobuf root as a 4-byte big-endian
	// length-prefixed blob (then ReadSubClass reads the 0x18 version immediately after). The body layout is
	// [u32 key][u32 type=50000][u32 instance][u8 flag][u32 BE blobLen][blob = blobLen bytes][u32 BE 0x18][tails],
	// where the blob is the whole FullCharacterIIR root. So blobLen == rootLen and the prefix is exactly 5 bytes.
	// The old varint model (AdjustSnapshotPrefixRootLength) was byte-misaligned and corrupted this length on resize.
	public class SnapshotBlobLengthTests
	{
		// BuildSnapshotPrefix writes the correct BE32 length for any root length, keeping the flag byte, and at the
		// captured root's own length reproduces the captured prefix byte-for-byte.
		[Fact]
		public void BuildSnapshotPrefix_WritesBe32_ForAnyRootLength()
		{
			var (prefix, rootLen, _) = Split("strie-login-inventory.bin");

			foreach (var newRoot in new[] { rootLen, rootLen - 370, 65, 0, rootLen + 2000 })
			{
				var p = SnapshotPackets.BuildPrefix(newRoot);
				Assert.Equal((uint)newRoot, p.ReadU32Be(1));
				Assert.Equal(prefix[0], p[0]); // flag
			}

			Assert.Equal(prefix, SnapshotPackets.BuildPrefix(rootLen));
		}

		// The captured prefix's BE32 at offset 1 already equals rootLen (the client's blob length), and the byte
		// immediately after that many blob bytes is the 0x18 version — proven across both beach captures.
		[Theory, InlineData("strie-login-inventory.bin", 686), InlineData("account-stricken997-character-Strie-login-randomActions-bigboy.bin", 681)]
		public void CapturedPrefix_EncodesBe32BlobLength(string bin, uint expectedBlobLen)
		{
			var (prefix, rootLen, tails) = Split(bin);
			var be32 = prefix.ReadU32Be(1);
			Assert.Equal(expectedBlobLen, be32); // matches the byte-level capture
			Assert.Equal((uint)rootLen, be32); // blobLen == rootLen (the prefix carries no payload of its own)
			Assert.Equal((byte)0x00, prefix[0]); // flag
			// The blob ends exactly at the root's end; the version 0x18 sits at tails offset 0 (= blob end).
			Assert.True(tails.Length >= 4);
			Assert.Equal(0x18u, tails.ReadU32Be(0));
		}

		// End-to-end: a DB-generated root with a different length than the capture, reassembled with the
		// rebuilt prefix, re-splits cleanly (tails byte-identical, root re-splits) and the
		// prefix BE32 blob length stays consistent with the new root.
		[Fact]
		public void GeneratedSnapshot_ReassemblesConsistently()
		{
			var (prefix, verbatimRootLen, tails, instance, verbatimRoot) = SplitFull("strie-login-inventory.bin");

			var held = new[]
			{
				Held(1, ContainerKind.Equipped, 0x12, Mk(3361557, 3714105241, 1980330998, 2034888138, 2381170642, 1129664565)),
				Held(2, ContainerKind.Bag, 1, Mk(3774542, 2515285418, 2815223308, 1368962356, 3023132244, 1347105359)),
				Held(3, ContainerKind.Bag, 2, Mk(13817, 1796783475, 3438012111, 3985322333, 505316191, 1177700698)),
				Held(4, ContainerKind.Bag, 3, Mk(13826, 1761429601, 4267309034, 1871298002, 3660826526, 1160857399))
			};

			var typed = verbatimRoot.FromProtobuf<ProtoFullCharacterIIR>();
			typed.Inventories.Clear();
			foreach (var c in FullCharacterRoots.Containers(instance, held))
				typed.Inventories.Add(c);
			var genRoot = typed.ToProtobuf();
			Assert.NotEqual(verbatimRootLen, genRoot.Length); // a real resize, so the prefix must be corrected

			// SnapshotPackets.BuildPrefix is the production path: the prefix is built from scratch rather than patched
			// into a captured one.
			var newPrefix = SnapshotPackets.BuildPrefix(genRoot.Length);
			Assert.Equal((uint)genRoot.Length, newPrefix.ReadU32Be(1));

			// Reassemble [subop][c350][instance][newPrefix][genRoot][tails] and re-split.
			using var content = new FaolanStream();
			content.WriteUInt32(IirTypes.FullPlayerCharacterIIR);
			content.WriteUInt32(0x0000C350);
			content.WriteUInt32(instance);
			content.WriteArray(newPrefix);
			content.WriteArray(genRoot);
			content.WriteArray(tails);

			var s2 = new FaolanStream(content.ToArray());
			s2.ReadUInt32();
			s2.ReadUInt32();
			s2.ReadUInt32();
			var reparsed = SnapshotPackets.Parse(s2);
			Assert.True(FullCharSnapshotSubClassCodec.Build(reparsed.SubClass).SequenceEqual(tails), "tails must survive intact after the regenerated root");
			Assert.True(reparsed.Root.ToProtobuf().SequenceEqual(genRoot), "the regenerated root must re-split byte-identically");
			return;

			Item Mk(uint id, uint d1, uint d2, uint d3, uint d4, uint arch)
			{
				return new Item
				{
					Id = id,
					Key = new ProtoDecryptionKey
					{
						Key1 = d1,
						Key2 = d2,
						Key3 = d3,
						Key4 = d4
					},
					Hash = arch
				};
			}
		}

		// A held item, as the container builder takes it: the instance's own id is what the wire's item_id
		// carries, so the test states it explicitly rather than deriving it from the cell.
		private static ItemInstance Held(uint id, ContainerKind container, int slot, Item template)
		{
			return new ItemInstance
			{
				Id = id,
				Container = container,
				Slot = slot,
				TemplateId = template.Id,
				Template = template,
				Count = 1
			};
		}

		private static (byte[] Prefix, int RootLen, byte[] SubClass) Split(string bin)
		{
			var packet = Rpc.Parse(File.ReadAllBytes(TestPaths.Asset(bin)));
			var s = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var parts = SnapshotPackets.Parse(s);
			return (SnapshotPackets.PrefixOf(parts), parts.Root.ToProtobuf().Length, FullCharSnapshotSubClassCodec.Build(parts.SubClass));
		}

		// Variant of Split that also returns the instance id and the verbatim root bytes (for the reassembly test).
		private static (byte[] Prefix, int RootLen, byte[] SubClass, uint Instance, byte[] Root) SplitFull(string bin)
		{
			var packet = Rpc.Parse(File.ReadAllBytes(TestPaths.Asset(bin)));
			var s = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			s.ReadUInt32();
			s.ReadUInt32();
			var instance = s.ReadUInt32();
			var parts = SnapshotPackets.Parse(s);
			var root = parts.Root.ToProtobuf();
			return (SnapshotPackets.PrefixOf(parts), root.Length, FullCharSnapshotSubClassCodec.Build(parts.SubClass), instance, root);
		}
	}
}
