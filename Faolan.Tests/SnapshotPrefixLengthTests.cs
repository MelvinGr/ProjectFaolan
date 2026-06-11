using System;
using System.IO;
using System.Linq;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// Regression guard for the 0x737A6DF9 snapshot's IIR prefix root-length field. The client reads the
	// FullCharacterIIR protobuf root by a length the snapshot carries in its 8-byte IIR prefix: a varint at
	// prefix offset 4 equals (constant + rootLength). When we regenerate the root from DB items its length
	// changes, so that varint must be delta-adjusted or the client misframes the following subclass version
	// ("ReadSubClass Wrong IIR_t version ..."). These tests pin the relationship (proven across two captures)
	// and the delta-adjust, so a regression in either is caught without a live client.
	public class SnapshotPrefixLengthTests
	{
		private const string CapturesDir = "/home/melvin/Development/C#/Project Faolan/Captures";
		private readonly ITestOutputHelper _o;
		public SnapshotPrefixLengthTests(ITestOutputHelper o) => _o = o;

		private static ulong PrefixVarintAt4(byte[] prefix)
		{
			ulong v = 0;
			var shift = 0;
			var i = 4;
			while (i < prefix.Length)
			{
				var b = prefix[i++];
				v |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					break;

				shift += 7;
			}

			return v;
		}

		private static (byte[] Prefix, byte[] Root, byte[] Tails, uint Instance) Split(string bin)
		{
			var packet = new ConanPacket(File.ReadAllBytes(Path.Combine(CapturesDir, bin)));
			var stream = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			var instance = stream.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(stream);
			return (parts.Prefix, parts.Root.ToArray(), parts.Tails.ToArray(), instance);
		}

		// The prefix varint - rootLength is the SAME constant across captures with different root lengths: that is
		// what makes it a root-length field. (strie root 683, bigboy root 678; varint 5166 vs 5161, both - root = 4483.)
		[Fact]
		public void PrefixVarint_MinusRootLength_IsConstantAcrossCaptures()
		{
			var bins = new[] { "strie-login-inventory.bin", "account-stricken997-character-Strie-login-randomActions-bigboy.bin" }
				.Where(b => File.Exists(Path.Combine(CapturesDir, b)))
				.ToArray();
			Assert.True(bins.Length >= 2, "need both capture fixtures");

			long? constant = null;
			foreach (var bin in bins)
			{
				var (prefix, root, _, _) = Split(bin);
				var c = (long)PrefixVarintAt4(prefix) - root.Length;
				_o.WriteLine($"{bin}: rootLen={root.Length} prefixVarint={PrefixVarintAt4(prefix)} constant={c}");
				constant ??= c;
				Assert.Equal(constant, c);
			}
		}

		// AdjustSnapshotPrefixRootLength preserves the invariant: after adjusting by (newRoot - oldRoot), the
		// varint equals constant + newRoot.
		[Fact]
		public void AdjustPrefix_PreservesRootLengthInvariant()
		{
			var (prefix, root, _, _) = Split("strie-login-inventory.bin");
			var oldConst = (long)PrefixVarintAt4(prefix) - root.Length;

			foreach (var newRootLen in new[] { root.Length - 1, root.Length - 50, root.Length + 200, root.Length + 5000 })
			{
				var adjusted = GameServerListener.AdjustSnapshotPrefixRootLength(prefix, newRootLen - root.Length);
				var newConst = (long)PrefixVarintAt4(adjusted) - newRootLen;
				Assert.Equal(oldConst, newConst);
			}

			// delta 0 returns the prefix unchanged.
			Assert.Equal(prefix, GameServerListener.AdjustSnapshotPrefixRootLength(prefix, 0));
		}

		// End-to-end: a DB-generated root with a DIFFERENT length than the capture, reassembled with the adjusted
		// prefix, re-splits cleanly (tails byte-identical) AND the prefix varint stays consistent with the new root.
		[Fact]
		public void GeneratedSnapshot_ReassemblesConsistently()
		{
			var (prefix, verbatimRoot, tails, instance) = Split("strie-login-inventory.bin");
			var oldConst = (long)PrefixVarintAt4(prefix) - verbatimRoot.Length;

			Item Mk(uint id, uint d1, uint d2, uint d3, uint d4, uint arch) => new Item { Id = id, Data1 = d1, Data2 = d2, Data3 = d3, Data4 = d4, Archetype = arch };
			var equipped = new System.Collections.Generic.Dictionary<ulong, (Item, uint)>
			{
				{ 0x12, (Mk(3361557, 3714105241, 1980330998, 2034888138, 2381170642, 1129664565), 1129664565) },
			};
			(Item, uint)? bagFirst = (Mk(3774542, 2515285418, 2815223308, 1368962356, 3023132244, 1347105359), 1347105359);
			var rows = new[]
			{
				new CharacterInventory { Container = 104, Slot = 2, Count = 1, Item = Mk(13817, 1796783475, 3438012111, 3985322333, 505316191, 1177700698) },
				new CharacterInventory { Container = 104, Slot = 3, Count = 1, Item = Mk(13826, 1761429601, 4267309034, 1871298002, 3660826526, 1160857399) },
			};

			var typed = ((ReadOnlySpan<byte>)verbatimRoot).FromProtobuf<ProtoFullCharacterIIR>();
			typed.Inventories.Clear();
			foreach (var c in FullCharSnapshotBuilder.BuildContainers(instance, equipped, bagFirst, rows))
				typed.Inventories.Add(c);
			var genRoot = typed.ToProtobufBytes();
			Assert.NotEqual(verbatimRoot.Length, genRoot.Length); // a real resize, so the prefix MUST be adjusted

			var newPrefix = GameServerListener.AdjustSnapshotPrefixRootLength(prefix, genRoot.Length - verbatimRoot.Length);
			Assert.Equal(oldConst, (long)PrefixVarintAt4(newPrefix) - genRoot.Length);

			// Reassemble [subop][c350][instance][newPrefix][genRoot][tails] and re-split.
			using var content = new FaolanStream();
			content.WriteUInt32(0x737A6DF9);
			content.WriteUInt32(0x0000C350);
			content.WriteUInt32(instance);
			content.WriteArray(newPrefix);
			content.WriteArray(genRoot);
			content.WriteArray(tails);

			var s2 = new FaolanStream(content.ToArray());
			s2.ReadUInt32();
			s2.ReadUInt32();
			s2.ReadUInt32();
			var reparsed = InventorySnapshotInspector.ParseInventoryPacketBody(s2);
			Assert.True(reparsed.Tails.ToArray().SequenceEqual(tails), "tails must survive intact after the regenerated root");
			Assert.True(reparsed.Root.ToArray().SequenceEqual(genRoot), "the regenerated root must re-split byte-identically");
		}
	}
}
