using Faolan.Core.Network.Protobuf;
using ProtoBuf;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Faolan.Core.Network.FullCharSnapshot
{
    // Splits a FullChar (0x737A6DF9) snapshot body into its three regions and decodes the inventory root as the
    // TYPED ProtoFullCharacterIIR (protobuf-net, IExtensible => lossless). Body layout after the [subOpcode][c350]
    // [instance] header is [prefix][root protobuf][sub-class]. The prefix is the client's blob-length frame
    // [u8 flag][u32 BE blobLen][field5] (RetailLoginReplay.SetSnapshotBlobLength / n3InfoItemRemote::Construct):
    // the client reads exactly blobLen bytes = field5 + root, so rootLength = blobLen - field5Len. A pure structural
    // read - no byte scanning, and robust to a resized/re-lengthed prefix (unlike the earlier varint model).
    // Test/RE tooling only: production BUILDS snapshots typed via FullCharSnapshotBuilder.
    public static class FullCharSnapshotReader
    {
        // Beach prefix = [u8 flag][u32 BE blobLen][field5 = 3 bytes]; blobLen counts field5 + root.
        private const uint PrefixFlagBytes = 1;
        private const uint PrefixField5Bytes = 3;

        // The three regions of a snapshot body: the exact prefix bytes, the typed inventory root, and the sub-class
        // (reassembling Prefix + Root.ToProtobuf() + Tails.ToArray() reproduces the body byte-for-byte).
        public sealed record InventoryPacketParts(byte[] Prefix, ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass);

        // `bodyStream` must be positioned right after the [subOpcode][c350][instance] header. The prefix's BE32
        // blob length delimits the root (blobLen = field5Len + rootLength), so this is a pure structural split.
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

        // Consumes the prefix [flag][BE32 blobLen][field5] and returns the root length (blobLen - field5Len).
        private static uint ReadPrefixRootLength(FaolanStream bodyStream)
        {
            bodyStream.ReadArray(PrefixFlagBytes); // flag
            var blobLength = bodyStream.ReadUInt32(); // BE32: field5 + root
            bodyStream.ReadArray(PrefixField5Bytes); // field5
            return blobLength - PrefixField5Bytes;
        }

        public static ProtoInventoryGame GetContainer(ProtoFullCharacterIIR root, ContainerKind kind)
        {
            return root.Inventories.FirstOrDefault(c => c.InventoryId.Type == (uint)kind);
        }

        public static IEnumerable<ProtoInventoryGame.InventoryListEntry> GetContainerEntries(ProtoInventoryGame container)
        {
            return container?.Items ?? [];
        }

        public static ProtoInventoryGame.InventoryListEntry GetEntryBySlot(ProtoInventoryGame container, ulong slot)
        {
            return GetContainerEntries(container).FirstOrDefault(e => e.Pos == slot);
        }

        // Decodes ONLY the inventory root (skips the sub-class, which for a high-level caster carry the whole
        // spellbook/pet/quest data and can be huge). Same structural split as ParseInventoryPacketBody.
        public static ProtoFullCharacterIIR ParseInventoryRoot(FaolanStream bodyStream)
        {
            var rootBytes = bodyStream.ReadArray(ReadPrefixRootLength(bodyStream));

            using var rootStream = new MemoryStream(rootBytes, false);
            return Serializer.Deserialize<ProtoFullCharacterIIR>(rootStream);
        }

        public static uint GetContainerKind(ProtoInventoryGame container) => container.InventoryId.Type;

        // The item's hash (ProtoAcgItem field 8) and its stable identity quad (ProtoAcgItem.key1 = a ProtoDecryptionKey
        // of four fixed32s = the same value as p_items.Data1..4).
        public static uint? GetEntryArchetype(ProtoInventoryGame.InventoryListEntry entry) => entry.Item?.ItemData?.Hash;

        public static (uint Data1, uint Data2, uint Data3, uint Data4)? GetEntryIdentityQuad(ProtoInventoryGame.InventoryListEntry entry)
        {
            var key = entry.Item?.ItemData?.Key1;
            return key == null
                ? null
                : (key.Key1, key.Key2, key.Key3, key.Key4);
        }

        public static ulong GetEntrySlot(ProtoInventoryGame.InventoryListEntry entry) => entry.Pos;
        public static ulong? GetEntryItemId(ProtoInventoryGame.InventoryListEntry entry) => entry.Item?.ItemId;
        public static uint? GetEntryTemplateId(ProtoInventoryGame.InventoryListEntry entry) => entry.Item?.ItemData?.Template1;

        // Stat 23 = the item's local inventory record id; stat 26 = the stack count (both in ProtoInventoryGameEntry.stat_list).
        public static uint? GetEntryRecordId(ProtoInventoryGame.InventoryListEntry entry) => Stat(entry, 23);
        public static uint? GetEntryCount(ProtoInventoryGame.InventoryListEntry entry) => Stat(entry, 26);

        private static uint? Stat(ProtoInventoryGame.InventoryListEntry entry, uint stat)
        {
            return entry.Item?.StatLists.FirstOrDefault(s => s.Stat == stat)?.Value;
        }
    }
}
