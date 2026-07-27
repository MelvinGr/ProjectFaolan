using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The FullChar (0x737A6DF9) snapshot BODY codec - both directions, in one type.
	//
	// Body layout: [u32 recordLen]([subOpcode][typeTag=0xC350][instance][prefix][root protobuf][sub-class]).
	// The prefix is the client's blob-length frame [u8 flag][u32 BE blobLen] (n3InfoItemRemote::Construct ->
	// FUN_00cbfb80): the client reads exactly blobLen bytes, and those bytes are the WHOLE ProtoFullCharacterIIR
	// root. The root is decoded/encoded TYPED (protobuf-net, IExtensible => lossless); the sub-class is
	// FullCharSnapshotSubClass; content generation lives in FullPlayerCharacterBuilder. A pure structural
	// split/assembly - no byte scanning.
	//
	// ⚠️ WHY READ AND WRITE LIVE TOGETHER. They used to be split across two projects that could not see each other:
	// the reader here, and the prefix builder + assembly on GameServerListener in the server host. Nothing forced
	// them to agree, and they did not. The reader skipped a 3-byte "field5" after the length; the writer appended
	// those same 3 bytes verbatim. That "field5" was never a frame - it is the root's OWN first field
	// (`subscription_flags`, 5), which happens to encode as exactly 3 bytes on the two level-1 beach captures the
	// model came from. So both halves were self-consistent on the beach and every golden passed, while the reader
	// failed on EVERY level-80 snapshot (silently, inside the importer's catch - imported characters came out with
	// no carried inventory) and the writer put subscription_flags on the wire twice.
	//
	// One type, one statement of the model, with a reader/writer round-trip gate over it
	// (FullCharSnapshotWriterTests) - that is what makes the drift impossible rather than merely unlikely. Same
	// class of bug as the 0x7E2 "constant", see [[list_frame_0x3f1_count_word]]: a shape that is "constant across N
	// captures" means nothing when all N captures are the degenerate case.
	//
	// AUDIENCE NOTE, so neither half looks dead: ParseInventoryRoot and the container/entry getters are PRODUCTION
	// (the importer's ItemHashHarvester and CaptureCharacterImporter read every captured snapshot through them);
	// BuildBody/BuildPrefix are PRODUCTION (the world-entry snapshot); ParseInventoryPacketBody has no production
	// caller but stays because it is the read side of the model the writer emits - moving it to the tests would
	// leave the byte-exact round-trip gates asserting their own copy instead of the server's. Five one-line entry
	// accessors that only the tests used live in Faolan.Tests/Support/SnapshotEntryAccessors.cs.
	public static class FullCharSnapshotCodec
	{
		// Prefix = [u8 flag][u32 BE blobLen]; blobLen counts the root exactly, so the prefix carries no payload.
		public const int PrefixLength = 5;

		private const uint PrefixFlagBytes = 1;

		// ---- write ---------------------------------------------------------------------------------------------

		// The complete record body in the `[u32 recordLen][record]` form the world-entry framing expects. Pure over
		// its inputs - the caller supplies the client instance, so this needs no session.
		public static byte[] BuildBody(uint instance, byte[] rootBytes, byte[] subClassBytes)
		{
			ArgumentNullException.ThrowIfNull(rootBytes);
			ArgumentNullException.ThrowIfNull(subClassBytes);

			using var body = new FaolanStream();
			body.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(FullPlayerCharacterBuilder.Opcode);
				s.WriteUInt32(EntityClass.Character);
				s.WriteUInt32(instance);
				s.WriteArray(BuildPrefix(rootBytes.Length));
				s.WriteArray(rootBytes);
				s.WriteArray(subClassBytes);
			});
			return body.ToArray();
		}

		// The prefix the client reads before the root: [u8 flag=0][u32 BE blobLen], blobLen == rootLength. The
		// client reads EXACTLY blobLen bytes as the protobuf root, then expects the 0x18 sub-class version.
		public static byte[] BuildPrefix(int rootLength)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(rootLength);

			var prefix = new byte[PrefixLength];
			prefix[0] = 0x00; // flag
			prefix.WriteU32Be(1, (uint)rootLength);
			return prefix;
		}

		// ---- root accessors ------------------------------------------------------------------------------------

		public static ProtoInventoryGame? GetContainer(ProtoFullCharacterIIR root, ContainerKind kind)
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

		// ---- read ----------------------------------------------------------------------------------------------

		// `bodyStream` must be positioned right after the [subOpcode][typeTag][instance] header. The prefix's BE32
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

		// Decodes ONLY the inventory root (skips the sub-class, which for a high-level caster carries the whole
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
		// (reassembling Prefix + Root.ToProtobuf() + SubClass.ToArray() reproduces the body byte-for-byte).
		public sealed record InventoryPacketParts(byte[] Prefix, ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass);
	}
}
