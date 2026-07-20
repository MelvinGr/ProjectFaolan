using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// Splits a FullChar (0x737A6DF9) snapshot body into its three regions and decodes the inventory root as the
	// TYPED ProtoFullCharacterIIR (protobuf-net, IExtensible => lossless). Body layout after the [subOpcode][c350]
	// [instance] header is [prefix][root protobuf][sub-class]. The prefix is the client's blob-length frame
	// [u8 flag][u32 BE blobLen] (RetailLoginReplay.SetSnapshotBlobLength / n3InfoItemRemote::Construct): the
	// client reads exactly blobLen bytes, and those bytes are the WHOLE ProtoFullCharacterIIR root. A pure
	// structural read - no byte scanning, and robust to a resized/re-lengthed prefix.
	// MIXED audience, so do not assume either half is dead:
	//   - ParseInventoryRoot + the container/entry getters are PRODUCTION (the importer's ItemHashHarvester and
	//     CaptureCharacterImporter read every captured snapshot through them).
	//   - ParseInventoryPacketBody (the full prefix/root/tails split) has no production caller - production BUILDS
	//     snapshots typed via FullPlayerCharacterBuilder - but it stays here deliberately: it encodes the server's
	//     own prefix model, so moving it into the tests would leave the byte-exact round-trip gates asserting their
	//     own copy of that model instead of the server's.
	// Five one-line entry accessors that only the tests used moved out to
	// Faolan.Tests/Support/SnapshotEntryAccessors.cs.
	//
	// ⚠ The prefix used to be modelled as [flag][blobLen][field5 = 3 bytes] with root = blobLen - 3. That "field5"
	// was not a frame at all: it was the root's OWN first field (`subscription_flags` = 5), which happens to encode
	// as exactly 3 bytes on the two low-level beach captures the model was derived from. A character that carries
	// `character_tag` (field 1, packed) leads with that instead, so the fixed 3-byte skip landed mid-varint and
	// EVERY level-80 snapshot failed to deserialize (silently, in the importer's catch - the imported characters
	// came out with no carried inventory at all). Verified against NecroL80/DemoL80 L80: the blob top-level-parses
	// to exactly blobLen bytes with zero remainder. Same class of bug as the 0x7E2 "constant" - see
	// [[list_frame_0x3f1_count_word]]: a shape "constant across N captures" means nothing when all N are the
	// degenerate case.
	public static class FullCharSnapshotReader
	{
		// Prefix = [u8 flag][u32 BE blobLen]; blobLen counts the root exactly.
		private const uint PrefixFlagBytes = 1;

		public static ProtoInventoryGame GetContainer(ProtoFullCharacterIIR root, ContainerKind kind)
		{
			return root.Inventories.FirstOrDefault(c => c.InventoryId.Type == (uint)kind);
		}

		public static IEnumerable<ProtoInventoryGame.InventoryListEntry> GetContainerEntries(ProtoInventoryGame container)
		{
			return container?.Items ?? [];
		}

		public static uint GetContainerKind(ProtoInventoryGame container)
		{
			return container.InventoryId.Type;
		}

		// The item's hash (ProtoAcgItem field 8) and its stable identity quad (ProtoAcgItem.key1 = a ProtoDecryptionKey
		// of four fixed32s = the same value as p_items.Data1..4).
		public static uint? GetEntryArchetype(ProtoInventoryGame.InventoryListEntry entry)
		{
			return entry.Item?.ItemData?.Hash;
		}

		public static (uint Data1, uint Data2, uint Data3, uint Data4)? GetEntryIdentityQuad(ProtoInventoryGame.InventoryListEntry entry)
		{
			var key = entry.Item?.ItemData?.Key1;
			return key == null
				? null
				: (key.Key1, key.Key2, key.Key3, key.Key4);
		}

		public static uint? GetEntryTemplateId(ProtoInventoryGame.InventoryListEntry entry)
		{
			return entry.Item?.ItemData?.Template1;
		}

		// `bodyStream` must be positioned right after the [subOpcode][c350][instance] header. The prefix's BE32
		// blob length delimits the root (blobLen == rootLength), so this is a pure structural split.
		public static InventoryPacketParts ParseInventoryPacketBody(FaolanStream bodyStream)
		{
			var prefixStart = bodyStream.Position;
			var rootLength = ReadPrefixRootLength(bodyStream);
			var prefixEnd = bodyStream.Position;

			bodyStream.Position = prefixStart;
			var prefix = bodyStream.ReadArray(prefixEnd - prefixStart);
			var rootBytes = bodyStream.ReadArray(rootLength);
			var subClass = bodyStream.ReadRemainingArray();

			using var rootStream = new MemoryStream(rootBytes, false);
			var root = Serializer.Deserialize<ProtoFullCharacterIIR>(rootStream);

			return new InventoryPacketParts(prefix, root, FullCharSnapshotSubClass.Deserialize(subClass));
		}

		// Decodes ONLY the inventory root (skips the sub-class, which for a high-level caster carry the whole
		// spellbook/pet/quest data and can be huge). Same structural split as ParseInventoryPacketBody.
		public static ProtoFullCharacterIIR ParseInventoryRoot(FaolanStream bodyStream)
		{
			var rootBytes = bodyStream.ReadArray(ReadPrefixRootLength(bodyStream));

			using var rootStream = new MemoryStream(rootBytes, false);
			return Serializer.Deserialize<ProtoFullCharacterIIR>(rootStream);
		}

		// Consumes the prefix [flag][BE32 blobLen] and returns the root length (== blobLen).
		private static uint ReadPrefixRootLength(FaolanStream bodyStream)
		{
			bodyStream.ReadArray(PrefixFlagBytes); // flag
			return bodyStream.ReadUInt32(); // BE32 blobLen == the root's length
		}

		// The three regions of a snapshot body: the exact prefix bytes, the typed inventory root, and the sub-class
		// (reassembling Prefix + Root.ToProtobuf() + Tails.ToArray() reproduces the body byte-for-byte).
		public sealed record InventoryPacketParts(byte[] Prefix, ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass);
	}
}
