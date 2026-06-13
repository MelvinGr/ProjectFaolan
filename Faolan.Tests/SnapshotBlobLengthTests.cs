using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Faolan.Core.Data;
using Faolan.Core.Network.Conan;
using Faolan.GameServer;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// Golden regression guard for the REAL 0x737A6DF9 root framing, recovered from AgeOfConan.exe (Ghidra,
	// 2026-06-13): n3InfoItemRemote::Construct -> FUN_00cbfb80 reads the protobuf root as a 4-byte BIG-ENDIAN
	// length-prefixed blob (then ReadSubClass reads the 0x18 version immediately after). The body layout is
	// [u32 KEY][u32 type=50000][u32 instance][u8 flag][u32 BE blobLen][blob = blobLen bytes][u32 BE 0x18][tails],
	// where blob = field5 (prefix[5..]) + the FullCharacterIIR root. So blobLen == (prefix.Length - 5) + rootLen.
	// The old varint model (AdjustSnapshotPrefixRootLength) was byte-misaligned and corrupted this length on resize.
	public class SnapshotBlobLengthTests
	{
		private const string CapturesDir = "/home/melvin/Development/C#/Project Faolan/Captures";

		private static (byte[] Prefix, int RootLen, byte[] Tails) Split(string bin)
		{
			var packet = new ConanPacket(File.ReadAllBytes(Path.Combine(CapturesDir, bin)));
			var s = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			s.ReadUInt32();
			s.ReadUInt32();
			s.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(s);
			return (parts.Prefix, parts.Root.ToArray().Length, parts.Tails.ToArray());
		}

		// The captured prefix's BE32 at offset 1 already equals field5Len + rootLen (the client's blob length), and
		// the byte immediately after that many blob bytes is the 0x18 version — proven across both beach captures.
		[Theory]
		[InlineData("strie-login-inventory.bin", 686)]
		[InlineData("account-stricken997-character-Strie-login-randomActions-bigboy.bin", 681)]
		public void CapturedPrefix_EncodesBe32BlobLength(string bin, uint expectedBlobLen)
		{
			if (!File.Exists(Path.Combine(CapturesDir, bin)))
				return;

			var (prefix, rootLen, tails) = Split(bin);
			var be32 = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(1, 4));
			Assert.Equal(expectedBlobLen, be32);                       // matches the byte-level capture
			Assert.Equal((uint)((prefix.Length - 5) + rootLen), be32); // blobLen == field5Len + rootLen
			Assert.Equal((byte)0x00, prefix[0]);                       // flag
			// field5 (prefix[5..]) is the start of the blob; the version 0x18 sits at tails offset 0 (= blob end).
			Assert.True(tails.Length >= 4);
			Assert.Equal(0x18u, BinaryPrimitives.ReadUInt32BigEndian(tails.AsSpan(0, 4)));
		}

		// SetSnapshotBlobLength writes the correct BE32 length for ANY regenerated root length, keeping the flag
		// and field5 bytes; a round-trip back through the captured length reproduces the original prefix.
		[Fact]
		public void SetSnapshotBlobLength_WritesBe32_PreservesFlagAndField5()
		{
			var (prefix, rootLen, _) = Split("strie-login-inventory.bin");

			foreach (var newRoot in new[] { rootLen, rootLen - 370, 65, 0, rootLen + 2000 })
			{
				var p = GameServerListener.SetSnapshotBlobLength(prefix, newRoot);
				Assert.Equal((uint)((prefix.Length - 5) + newRoot), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1, 4)));
				Assert.Equal(prefix[0], p[0]);                                       // flag preserved
				Assert.Equal(prefix.Skip(5).ToArray(), p.Skip(5).ToArray());         // field5 preserved
			}

			// Writing the original root length reproduces the captured prefix byte-for-byte.
			Assert.Equal(prefix, GameServerListener.SetSnapshotBlobLength(prefix, rootLen));
		}
	}
}
