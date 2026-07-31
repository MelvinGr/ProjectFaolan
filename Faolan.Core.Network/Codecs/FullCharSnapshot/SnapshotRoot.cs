using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// Reading the snapshot's protobuf ROOT without the tails, plus the container/entry accessors over it. A
	// neighbour rather than a second Parse on FullPlayerCharacterCodec (#530 rule 1: one Parse per codec, and
	// "give me only the root" is a different question with a different answer - the SpawnBodyReader precedent).
	//
	// The importer is why it exists: ItemHashHarvester and CaptureCharacterImporter walk every captured snapshot for
	// item identity, and a high-level caster's tails carry the whole spellbook/pet/quest data. Skipping them is a
	// pure structural short-cut - the blob-length frame delimits the root exactly - not a tolerant read.
	public static class SnapshotRoot
	{
		// The item's hash (ProtoAcgItem field 8).
		public static uint? ArchetypeOf(ProtoInventoryGame.InventoryListEntry entry)
		{
			return entry.Item?.ItemData?.Hash;
		}

		public static ProtoInventoryGame? ContainerOf(ProtoFullCharacterIIR root, ContainerKind kind)
		{
			return root.Inventories.FirstOrDefault(c => c.InventoryId.Type == (uint)kind);
		}

		public static IEnumerable<ProtoInventoryGame.InventoryListEntry> EntriesOf(ProtoInventoryGame container)
		{
			return container?.Items ?? [];
		}

		// The entry's stable identity quad (ProtoAcgItem.key1 = a ProtoDecryptionKey of four fixed32s = the same
		// value as p_items.Data1..4).
		public static (uint Data1, uint Data2, uint Data3, uint Data4)? IdentityQuadOf(ProtoInventoryGame.InventoryListEntry entry)
		{
			var key = entry.Item?.ItemData?.Key1;
			return key == null
				? null
				: (key.Key1, key.Key2, key.Key3, key.Key4);
		}

		public static uint KindOf(ProtoInventoryGame container)
		{
			return container.InventoryId.Type;
		}

		// Decodes ONLY the inventory root out of a whole FullPlayerCharacterIIR record - the same structural split
		// FullPlayerCharacterCodec.Parse does, stopping at the sub-class.
		public static ProtoFullCharacterIIR Of(in ReadOnlySpan<byte> record)
		{
			var r = new SpanReader(record, nameof(SnapshotRoot));
			r.ExpectU32Be(FullPlayerCharacterCodec.Opcode, "FullPlayerCharacterIIR type word");
			r.ExpectU32Be(EntityClass.Character, "type tag");
			r.ReadU32Be(); // instance
			r.ExpectU8(0, "blob-length frame flag");
			return r.ReadBytes(checked((int)r.ReadU32Be())).FromProtobuf<ProtoFullCharacterIIR>();
		}

		public static uint? TemplateIdOf(ProtoInventoryGame.InventoryListEntry entry)
		{
			return entry.Item?.ItemData?.Template1;
		}
	}
}
