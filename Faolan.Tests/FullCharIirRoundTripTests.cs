using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The FullChar (0x737A6DF9 snapshot) generation oracle, mirroring the spawn's
	// PlayerCharacterMessage_RoundTripsByteIdentically: the snapshot's protobuf root must decode as the
	// recovered ProtoFullCharacterIIR and RE-ENCODE byte-identically, over every captured snapshot body that
	// the inspector can split (prefix + root + tails). Truncated analyzer captures (the TCP-reassembly gap
	// drops parts of compressed downloads) are skipped; a body that splits but does not round-trip fails.
	public class FullCharIirRoundTripTests
	{
		private readonly ITestOutputHelper _output;

		public FullCharIirRoundTripTests(ITestOutputHelper output)
		{
			_output = output;
		}

		// The AUTHORING oracle (#57): FullPlayerCharacterBuilder.BuildRoot, fed a captured root's own field values,
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
					rootBytes = FullCharSnapshotCodec.ParseInventoryPacketBody(stream).Root.ToProtobuf();
				}
				catch
				{
					continue; // truncated/non-splitting - covered by the round-trip test above
				}

				var decoded = rootBytes.FromProtobuf<ProtoFullCharacterIIR>();

				// f2/f3 are absent on every captured snapshot; BuildRoot doesn't author them, so a capture that
				// carried them would fail here (a guard against a silently-dropped field).
				Assert.False(decoded.ShouldSerializeFreeFeatRespecs(), $"{source}: unexpected free_feat_respecs");
				Assert.False(decoded.ShouldSerializeFeatResets(), $"{source}: unexpected feat_resets");

				var rebuilt = FullPlayerCharacterBuilder.BuildRoot(decoded.CharacterTags, decoded.ShouldSerializeExpansionFlags()
					? decoded.ExpansionFlags
					: null, decoded.ShouldSerializeSubscriptionFlags()
					? decoded.SubscriptionFlags
					: null, decoded.ShouldSerializeHasNewClaimItems()
					? decoded.HasNewClaimItems
					: null, decoded.ShouldSerializeHasSpellbook()
					? decoded.HasSpellbook
					: null, decoded.Inventories);

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

				FullCharSnapshotCodec.InventoryPacketParts parts;
				byte[] rootBytes;
				try
				{
					var stream = new FaolanStream(body);
					stream.ReadUInt32(); // subop
					stream.ReadUInt32(); // c350
					stream.ReadUInt32(); // instance
					parts = FullCharSnapshotCodec.ParseInventoryPacketBody(stream);
					rootBytes = parts.Root.ToProtobuf();
				}
				catch (Exception e)
				{
					skipped++;
					_output.WriteLine($"SKIP {source}: does not split ({e.GetType().Name}: {e.Message})");
					continue;
				}

				var decoded = rootBytes.FromProtobuf<ProtoFullCharacterIIR>();
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
			foreach (var bin in new[] { "account-stricken997-character-Strie-login-randomActions-bigboy.bin", "strie-login-inventory.bin" })
			{
				var path = TestPaths.Asset(bin); // committed fixture - must exist (no silent skip)

				var packet = Rpc.Parse(File.ReadAllBytes(path));
				yield return (bin, packet.BodyReader().ReadArrayUInt32Length());
			}

			// NOTE: there is deliberately no capture-corpus loop here. It used to enumerate Captures/*.json while
			// SKIPPING *.tshark.json - and the corpus contains nothing else, so it never yielded a single packet even
			// when the captures were present. Dead code that also silently skipped when they were absent.
		}
	}
}
