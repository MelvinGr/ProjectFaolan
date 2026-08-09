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

		// The entry's wire LEVEL (ProtoAcgItem field 6). Per-template, not per-instance: across all 8,644 captured
		// inventory entries, 0 templates carry two different levels (#725).
		public static uint? LevelOf(ProtoInventoryGame.InventoryListEntry entry)
		{
			return (uint?)entry.Item?.ItemData?.Level;
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

		// The GEMS socketed into this entry's item, as (socket index, the gem's own ProtoAcgItem).
		//
		// `slot_map` (field 4) is where a socket lives in the PERSISTENT form - the login snapshot restates every
		// socketed item's full gem list, which is how a gemmed item survives a relog. Its live twin is
		// `DispatchAddToItemSlot` (GameCharInterface 0x1D), which carries the same pair for one socket.
		//
		// ⚠️ The socket names the GEM BY TEMPLATE and carries no stat values: the client derives the gem's effect
		// from the gem's own template. So this is the whole of a socket on the wire, and anything that stores one
		// stores an item reference, never a stat list.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json frame 8248 (the live add);
		//  1,187 slot_map blocks across 56 captures carry the persistent form]
		public static IEnumerable<(uint Socket, ProtoAcgItem Gem)> SocketsOf(ProtoInventoryGame.InventoryListEntry entry)
		{
			foreach (var socket in entry.Item?.SlotMaps ?? [])
			{
				if (socket.Item != null)
					yield return (socket.Slot, socket.Item);
			}
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
