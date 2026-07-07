using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Protobuf;
using Xunit;
using Xunit.Abstractions;
using Faolan.Core.Network.GameServer.FullCharSnapshot;

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

		// The AUTHORING oracle (#57): FullCharSnapshotBuilder.BuildRoot, fed a captured root's own field values,
		// must re-encode that root byte-identically - so the snapshot path can author the root from typed inputs
		// (DB containers + account flags + tag/spell state) instead of carrying the captured protobuf root.
		[Fact]
		public void BuildRoot_FromCapturedFields_ReEncodesByteIdentically()
		{
			var ok = 0;

			foreach (var (source, body) in SnapshotBodies())
			{
				byte[] rootBytes;
				try
				{
					var stream = new FaolanStream(body);
					stream.ReadUInt32(); // subop
					stream.ReadUInt32(); // c350
					stream.ReadUInt32(); // instance
					rootBytes = FullCharSnapshotReader.ParseInventoryPacketBody(stream).Root.ToProtobuf();
				}
				catch
				{
					continue; // truncated/non-splitting - covered by the round-trip test above
				}

				var decoded = ((ReadOnlySpan<byte>)rootBytes).FromProtobuf<ProtoFullCharacterIIR>();

				// f2/f3 are absent on every captured snapshot; BuildRoot doesn't author them, so a capture that
				// carried them would fail here (a guard against a silently-dropped field).
				Assert.False(decoded.ShouldSerializeFreeFeatRespecs(), $"{source}: unexpected free_feat_respecs");
				Assert.False(decoded.ShouldSerializeFeatResets(), $"{source}: unexpected feat_resets");

				var rebuilt = FullCharSnapshotBuilder.BuildRoot(decoded.CharacterTags,
					decoded.ShouldSerializeExpansionFlags()
						? decoded.ExpansionFlags
						: null,
					decoded.ShouldSerializeSubscriptionFlags()
						? decoded.SubscriptionFlags
						: null,
					decoded.ShouldSerializeHasNewClaimItems()
						? decoded.HasNewClaimItems
						: null,
					decoded.ShouldSerializeHasSpellbook()
						? decoded.HasSpellbook
						: null,
					decoded.Inventories);

				Assert.True(rootBytes.SequenceEqual(rebuilt.ToProtobuf()), $"{source}: BuildRoot did not reproduce the captured root ({rootBytes.Length} bytes)");
				ok++;
			}

			Assert.True(ok >= 2, $"expected at least the two .bin fixtures to author byte-identically (ok={ok})");
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

				FullCharSnapshotReader.InventoryPacketParts parts;
				byte[] rootBytes;
				try
				{
					var stream = new FaolanStream(body);
					stream.ReadUInt32(); // subop
					stream.ReadUInt32(); // c350
					stream.ReadUInt32(); // instance
					parts = FullCharSnapshotReader.ParseInventoryPacketBody(stream);
					rootBytes = parts.Root.ToProtobuf();
				}
				catch (Exception e)
				{
					skipped++;
					_output.WriteLine($"SKIP {source}: does not split ({e.GetType().Name}: {e.Message})");
					continue;
				}

				var decoded = ((ReadOnlySpan<byte>)rootBytes).FromProtobuf<ProtoFullCharacterIIR>();
				var reencoded = decoded.ToProtobuf();

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
			foreach (var bin in new[]
			{
				"account-stricken997-character-Strie-login-randomActions-bigboy.bin",
				"strie-login-inventory.bin"
			})
			{
				var path = Path.Combine(CapturesDir, bin);
				if (!File.Exists(path))
					continue;

				var packet = new ConanPacket(File.ReadAllBytes(path));
				yield return (bin, packet.Body.ReadArrayUInt32Length());
			}

			if (!Directory.Exists(CapturesDir))
				yield break;

			foreach (var json in Directory.EnumerateFiles(CapturesDir, "*.json").OrderBy(f => f))
			{
				if (json.EndsWith(".tshark.json", StringComparison.OrdinalIgnoreCase))
					continue; // tshark dumps use a different schema (BodyHex, not RawBytesBase64) - not this test's input

				using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
				if (!doc.RootElement.TryGetProperty("Packets", out var packets))
					continue;

				var index = 0;
				foreach (var p in packets.EnumerateArray())
				{
					if (!p.TryGetProperty("RawBytesBase64", out var rawEl))
						continue;

					var raw = Convert.FromBase64String(rawEl.GetString() ?? "");
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
