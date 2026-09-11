using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Faolan.Core;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	public class InventorySnapshotTests
	{
		private static readonly string BigboyPath = TestPaths.Asset("account-stricken997-character-Strie-login-randomActions-bigboy.bin");
		private static readonly string BurstInventoryPath = TestPaths.Asset("strie-login-inventory.bin");

		// Reconstruct the full packet (header + body) the way BuildBeachInventoryPacket does and compare to the
		// original - the inner-body round-trip passes, so if the no-op beach inventory stalls, the header/outer
		// framing reconstruction must differ. (Strie instance, so no substitution needed.)
		[Fact]
		public void BurstInventory_FullRebuild_IsByteIdentical()
		{
			if (!File.Exists(BurstInventoryPath))
				return;

			var original = File.ReadAllBytes(BurstInventoryPath);
			var packet = Rpc.Parse(original);
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = SnapshotPackets.Parse(body);

			byte[] sender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58];
			byte[] receiver = [0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xFF, 0xBD, 0xD9, 0x10];

			var produced = new Rpc(Endpoints.EndpointFromBytes(sender), Endpoints.EndpointFromBytes(receiver), packet.Method, b => b.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(subOpcode);
				s.WriteUInt32(c350);
				s.WriteUInt32(instance);
				s.WriteArray(SnapshotPackets.PrefixOf(parts));
				s.WriteArray(parts.Root.ToProtobuf());
				s.WriteArray(FullCharSnapshotSubClassCodec.Build(parts.SubClass));
			})).Serialize();

			var firstDiff = -1;
			for (var i = 0; i < Math.Min(original.Length, produced.Length); i++)
			{
				if (original[i] != produced[i])
				{
					firstDiff = i;
					break;
				}
			}

			Assert.True(original.Length == produced.Length && firstDiff == -1,
				$"len orig={original.Length} produced={produced.Length} firstDiff={firstDiff} " + $"origHead={Convert.ToHexString(original, 0, 40)} prodHead={Convert.ToHexString(produced, 0, 40)}");
		}

		// The inventory packet actually replayed at beach login (from the world burst) must also round-trip
		// byte-exact - it is a different capture from the bigboy (different tails), and the no-op rebuild stalled
		// the client, which points at a non-lossless tails (or root) round-trip here.
		[Fact]
		public void BurstInventory_ParseThenReserialize_IsByteIdentical()
		{
			if (!File.Exists(BurstInventoryPath))
				return;

			var packet = Rpc.Parse(File.ReadAllBytes(BurstInventoryPath));
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = SnapshotPackets.Parse(body);

			using var rebuilt = new FaolanStream();
			rebuilt.WriteUInt32(subOpcode);
			rebuilt.WriteUInt32(c350);
			rebuilt.WriteUInt32(instance);
			rebuilt.WriteArray(SnapshotPackets.PrefixOf(parts));
			rebuilt.WriteArray(parts.Root.ToProtobuf());
			rebuilt.WriteArray(FullCharSnapshotSubClassCodec.Build(parts.SubClass));

			body.Position = 0;
			var original = body.ToArray();
			var produced = rebuilt.ToArray();

			// Report the first divergence offset to locate the lossy region (prefix/root/tails).
			var firstDiff = -1;
			for (var i = 0; i < Math.Min(original.Length, produced.Length); i++)
			{
				if (original[i] != produced[i])
				{
					firstDiff = i;
					break;
				}
			}

			Assert.True(original.Length == produced.Length && firstDiff == -1, $"len orig={original.Length} produced={produced.Length} firstDiff={firstDiff}");
		}

		// The non-Strie path: substitute the live instance into the snapshot then parse->full-rebuild, and
		// compare to the substituted packet. If this diverges, the substitute+rebuild interaction is the stall.
		[Fact]
		public void BurstInventory_SubstitutedFullRebuild_IsByteIdentical()
		{
			if (!File.Exists(BurstInventoryPath))
				return;

			const uint newInstance = 0x02000006;
			var substituted = File.ReadAllBytes(BurstInventoryPath);
			ReplaceAll(substituted, EncodeVarint(0x02165EFF), EncodeVarint(newInstance));
			var oldRaw = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(oldRaw, 0x02165EFF);
			var newRaw = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(newRaw, newInstance);
			ReplaceAll(substituted, oldRaw, newRaw);
			var crc = Crc32.Calculate(substituted[8..]);
			substituted.WriteU32Be(4, crc);

			var packet = Rpc.Parse(substituted);
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = SnapshotPackets.Parse(body);

			byte[] sender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58];
			byte[] receiver = [0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, .. EncodeVarint(newInstance)];

			var produced = new Rpc(Endpoints.EndpointFromBytes(sender), Endpoints.EndpointFromBytes(receiver), packet.Method, b => b.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(subOpcode);
				s.WriteUInt32(c350);
				s.WriteUInt32(instance);
				s.WriteArray(SnapshotPackets.PrefixOf(parts));
				s.WriteArray(parts.Root.ToProtobuf());
				s.WriteArray(FullCharSnapshotSubClassCodec.Build(parts.SubClass));
			})).Serialize();

			var firstDiff = -1;
			for (var i = 0; i < Math.Min(substituted.Length, produced.Length); i++)
			{
				if (substituted[i] != produced[i])
				{
					firstDiff = i;
					break;
				}
			}

			Assert.True(substituted.Length == produced.Length && firstDiff == -1, $"len sub={substituted.Length} produced={produced.Length} firstDiff={firstDiff}");
		}

		// Foundational gate for any inventory rebuild: parsing the 0x737A6DF9 snapshot and re-serializing it
		// (Prefix + Root.ToArray() + Tails.ToArray()) must reproduce the original body byte-for-byte. If this is
		// not lossless, that drift - amplified by a full re-serialize - is the likely cause of the add/remove
		// loading stall (tiny same-width count edits would survive it, large structural edits would not).
		[Fact]
		public void ParseThenReserialize_IsByteIdentical()
		{
			if (!File.Exists(BigboyPath))
				return; // capture not present in this checkout

			var packet = Rpc.Parse(File.ReadAllBytes(BigboyPath));
			var body = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());

			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();

			var parts = SnapshotPackets.Parse(body);

			using var rebuilt = new FaolanStream();
			rebuilt.WriteUInt32(subOpcode);
			rebuilt.WriteUInt32(c350);
			rebuilt.WriteUInt32(instance);
			rebuilt.WriteArray(SnapshotPackets.PrefixOf(parts));
			rebuilt.WriteArray(parts.Root.ToProtobuf());
			rebuilt.WriteArray(FullCharSnapshotSubClassCodec.Build(parts.SubClass));

			body.Position = 0;
			var original = body.ToArray();
			var produced = rebuilt.ToArray();

			Assert.Equal(original.Length, produced.Length);
			Assert.Equal(original, produced);
		}

		private static byte[] EncodeVarint(uint value)
		{
			var bytes = new List<byte>(5);
			do
			{
				var b = (byte)(value & 0x7F);
				value >>= 7;
				if (value != 0)
					b |= 0x80;
				bytes.Add(b);
			}
			while (value != 0);

			return bytes.ToArray();
		}

		private static void ReplaceAll(byte[] buffer, byte[] find, byte[] repl)
		{
			for (var i = 8; i + find.Length <= buffer.Length; i++)
			{
				var match = true;
				for (var j = 0; j < find.Length; j++)
				{
					if (buffer[i + j] != find[j])
					{
						match = false;
						break;
					}
				}

				if (match)
				{
					repl.CopyTo(buffer, i);
					i += find.Length - 1;
				}
			}
		}
	}
}
