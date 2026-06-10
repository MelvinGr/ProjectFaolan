using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// The FullChar (0x737A6DF9 snapshot) generation oracle, mirroring the spawn's
	// PlayerCharacterMessage_RoundTripsByteIdentically: the snapshot's protobuf root must decode as the
	// recovered ProtoFullCharacterIIR and RE-ENCODE byte-identically, over every captured snapshot body that
	// the inspector can split (prefix + root + tails). Truncated analyzer captures (the TCP-reassembly gap
	// drops parts of compressed downloads) are skipped; a body that splits but does not round-trip fails.
	public class FullCharIirRoundTripTests
	{
		private const string CapturesDir = "/home/melvin/Development/C#/Project Faolan/Captures";

		private readonly ITestOutputHelper _output;

		public FullCharIirRoundTripTests(ITestOutputHelper output)
		{
			_output = output;
		}

		[Fact]
		public void FullCharRoot_RoundTripsByteIdentically_OnEveryParseableSnapshot()
		{
			var total = 0;
			var skipped = 0;
			var ok = 0;

			foreach (var (source, body) in SnapshotBodies())
			{
				total++;

				InventorySnapshotInspector.InventoryPacketParts parts;
				byte[] rootBytes;
				try
				{
					var stream = new FaolanStream(body);
					stream.ReadUInt32(); // subop
					stream.ReadUInt32(); // c350
					stream.ReadUInt32(); // instance
					parts = InventorySnapshotInspector.ParseInventoryPacketBody(stream);
					rootBytes = parts.Root.ToArray();
				}
				catch (Exception e)
				{
					skipped++;
					_output.WriteLine($"SKIP {source}: does not split ({e.GetType().Name}: {e.Message})");
					continue;
				}

				var decoded = ((ReadOnlySpan<byte>)rootBytes).FromProtobuf<ProtoFullCharacterIIR>();
				var reencoded = decoded.ToProtobufBytes();

				Assert.True(rootBytes.SequenceEqual(reencoded), $"{source}: ProtoFullCharacterIIR did not re-encode byte-identically (root {rootBytes.Length} -> {reencoded.Length})");
				Assert.True(decoded.Inventories.Count > 0, $"{source}: no inventory containers decoded");
				ok++;
			}

			_output.WriteLine($"round-tripped {ok}/{total} snapshots ({skipped} skipped: truncated/non-splitting)");
			Assert.True(ok >= 2, $"expected at least the two .bin fixtures to round-trip (ok={ok}, total={total})");
		}

		// All 0x737A6DF9 bodies (after [subop][c350][instance]) we can harvest: the two .bin fixtures + every
		// snapshot in the analyzer JSON captures.
		private static IEnumerable<(string Source, byte[] Body)> SnapshotBodies()
		{
			foreach (var bin in new[] { "account-stricken997-character-Strie-login-randomActions-bigboy.bin", "strie-login-inventory.bin" })
			{
				var path = Path.Combine(CapturesDir, bin);
				if (!File.Exists(path))
					continue;

				var packet = new ConanPacket(File.ReadAllBytes(path));
				yield return (bin, packet.Body.ReadArrayUInt32Length());
			}

			if (!Directory.Exists(CapturesDir))
				yield break;

			foreach (var json in Directory
				         .EnumerateFiles(CapturesDir, "*.json")
				         .OrderBy(f => f))
			{
				using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
				if (!doc.RootElement.TryGetProperty("Packets", out var packets))
					continue;

				var index = 0;
				foreach (var p in packets.EnumerateArray())
				{
					var raw = Convert.FromBase64String(p
						.GetProperty("RawBytesBase64")
						.GetString() ?? "");
					if (raw.Length < 32)
						continue;

					// [len4][crc4][headerLen4][header][body]; body = [len4][subop4]... Non-GameServer packets
					// (other protocols / framings) yield nonsense header lengths - skip them.
					var headerLen = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8, 4));
					if (headerLen > (uint)raw.Length)
						continue;

					var bodyStart = 12 + (int)headerLen;
					if (bodyStart + 8 > raw.Length || BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(bodyStart + 4, 4)) != 0x737A6DF9u)
						continue;

					yield return ($"{Path.GetFileName(json)}#{index++}", raw[(bodyStart + 4)..]);
				}
			}
		}
	}
}
