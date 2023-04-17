using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Crypt;
using Faolan.Core.Data;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	public class InventorySnapshotTests
	{
		private const string BigboyPath = "/home/melvin/Development/C#/Project Faolan/Captures/account-stricken997-character-Strie-login-randomActions-bigboy.bin";
		private const string BurstInventoryPath = "/home/melvin/Development/C#/Project Faolan/Captures/strie-login-inventory.bin";

		// Reconstruct the FULL packet (header + body) the way BuildBeachInventoryPacket does and compare to the
		// original - the inner-body round-trip passes, so if the no-op beach inventory stalls, the header/outer
		// framing reconstruction must differ. (Strie instance, so no substitution needed.)
		[Fact]
		public void BurstInventory_FullRebuild_IsByteIdentical()
		{
			if (!File.Exists(BurstInventoryPath))
				return;

			var original = File.ReadAllBytes(BurstInventoryPath);
			var packet = new ConanPacket(original);
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			byte[] sender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58];
			byte[] receiver = [0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xFF, 0xBD, 0xD9, 0x10];

			var stream = new PacketStream();
			stream.WriteHeader(sender, receiver, packet.Opcode);
			stream.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(subOpcode);
				s.WriteUInt32(c350);
				s.WriteUInt32(instance);
				s.WriteArray(parts.Prefix);
				s.WriteArray(parts.Root.ToArray());
				s.WriteArray(parts.Tails.ToArray());
			});
			stream.WriteLengthHash();
			var produced = stream.ToArray();

			var firstDiff = -1;
			for (var i = 0; i < Math.Min(original.Length, produced.Length); i++)
				if (original[i] != produced[i])
				{
					firstDiff = i;
					break;
				}

			Assert.True(original.Length == produced.Length && firstDiff == -1,
				$"len orig={original.Length} produced={produced.Length} firstDiff={firstDiff} " + $"origHead={Convert.ToHexString(original, 0, 40)} prodHead={Convert.ToHexString(produced, 0, 40)}");
		}

		// The inventory packet actually replayed at beach login (from the world burst) must ALSO round-trip
		// byte-exact - it is a different capture from the bigboy (different tails), and the no-op rebuild stalled
		// the client, which points at a non-lossless tails (or root) round-trip here.
		[Fact]
		public void BurstInventory_ParseThenReserialize_IsByteIdentical()
		{
			if (!File.Exists(BurstInventoryPath))
				return;

			var packet = new ConanPacket(File.ReadAllBytes(BurstInventoryPath));
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			using var rebuilt = new FaolanStream();
			rebuilt.WriteUInt32(subOpcode);
			rebuilt.WriteUInt32(c350);
			rebuilt.WriteUInt32(instance);
			rebuilt.WriteArray(parts.Prefix);
			rebuilt.WriteArray(parts.Root.ToArray());
			rebuilt.WriteArray(parts.Tails.ToArray());

			body.Position = 0;
			var original = body.ToArray();
			var produced = rebuilt.ToArray();

			// Report the first divergence offset to locate the lossy region (prefix/root/tails).
			var firstDiff = -1;
			for (var i = 0; i < Math.Min(original.Length, produced.Length); i++)
				if (original[i] != produced[i])
				{
					firstDiff = i;
					break;
				}

			Assert.True(original.Length == produced.Length && firstDiff == -1, $"len orig={original.Length} produced={produced.Length} firstDiff={firstDiff}");
		}

		// The non-Strie path: substitute the instance (like RewriteBurstInstance) THEN parse->full-rebuild, and
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
			BinaryPrimitives.WriteUInt32BigEndian(substituted.AsSpan(4, 4), crc);

			var packet = new ConanPacket(substituted);
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			byte[] sender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58];
			byte[] receiver = [0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, .. EncodeVarint(newInstance)];

			var stream = new PacketStream();
			stream.WriteHeader(sender, receiver, packet.Opcode);
			stream.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(subOpcode);
				s.WriteUInt32(c350);
				s.WriteUInt32(instance);
				s.WriteArray(parts.Prefix);
				s.WriteArray(parts.Root.ToArray());
				s.WriteArray(parts.Tails.ToArray());
			});
			stream.WriteLengthHash();
			var produced = stream.ToArray();

			var firstDiff = -1;
			for (var i = 0; i < Math.Min(substituted.Length, produced.Length); i++)
				if (substituted[i] != produced[i])
				{
					firstDiff = i;
					break;
				}

			Assert.True(substituted.Length == produced.Length && firstDiff == -1, $"len sub={substituted.Length} produced={produced.Length} firstDiff={firstDiff}");
		}

		// Foundational gate for any inventory rebuild: parsing the 0x737A6DF9 snapshot and re-serializing it
		// (Prefix + Root.ToArray() + Tails.ToArray()) must reproduce the original body byte-for-byte. If this is
		// NOT lossless, that drift - amplified by a full re-serialize - is the likely cause of the add/remove
		// loading stall (tiny same-width count edits would survive it, large structural edits would not).
		[Fact]
		public void ParseThenReserialize_IsByteIdentical()
		{
			if (!File.Exists(BigboyPath))
				return; // capture not present in this checkout

			var packet = new ConanPacket(File.ReadAllBytes(BigboyPath));
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());

			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var headerLen = body.Position; // bytes consumed before the inventory body proper

			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			using var rebuilt = new FaolanStream();
			rebuilt.WriteUInt32(subOpcode);
			rebuilt.WriteUInt32(c350);
			rebuilt.WriteUInt32(instance);
			rebuilt.WriteArray(parts.Prefix);
			rebuilt.WriteArray(parts.Root.ToArray());
			rebuilt.WriteArray(parts.Tails.ToArray());

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
					if (buffer[i + j] != find[j])
					{
						match = false;
						break;
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
