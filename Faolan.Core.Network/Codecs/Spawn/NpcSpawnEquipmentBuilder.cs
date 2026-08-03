using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds / lifts an NPC's worn-equipment InventoryEntries (the CharacterIIR f23 list) to and from
	// NpcSpawnEquipment rows, so the equipment generates from the DB instead of the captured NpcSpawn.Data blob
	// (Gitea #185). The NPC counterpart of PlayerSpawnEquipment: the captured NPC items follow the same ProtoAcgItem
	// shape the player builder emits - key1==key2==key3 (the GUID quad), template1==template2, no template3 - so a
	// row stores just the quad + Template + Level + Hash and BuildEntries reproduces the wire item byte-for-byte.
	public static class NpcSpawnEquipmentBuilder
	{
		// Rebuilds the InventoryEntries (ordered by slot) from the stored rows (per-placement NpcSpawnEquipment or
		// per-type NpcEquipment - both INpcEquipment).
		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildEntries(IEnumerable<INpcEquipment> rows)
		{
			return rows.OrderBy(r => r.Pos).Select(BuildEntry).ToList();
		}

		public static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(INpcEquipment row)
		{
			// The item identity (quad + hash) lives in Items and is reached through the FK navigation; require it
			// loaded rather than silently emitting an empty item (no-fallbacks: say what's missing).
			var item = row.Item ?? throw new InvalidOperationException(
				$"NPC worn-equipment slot {row.Pos} (template {row.Template}): the Item navigation is not loaded - Include it before building the spawn.");

			return BuildEntry(row.Pos, row.Template, row.Level, item.Key, item.Hash);
		}

		// Lifts a captured InventoryEntry into a row (per-placement NpcSpawnEquipment or per-type NpcEquipment via the
		// type param), but ONLY if it matches the reproducible shape (the three keys identical, the two templates
		// identical, no template3). Returns false for any entry outside that shape, so the importer can decline the whole
		// NPC rather than store something the builder would not reproduce byte-for-byte. The row stores just the slot +
		// template + level; the quad + hash it validates against belong to the Items row the FK on Template points at
		// (harvested from this same spawn packet, so they match by construction - verified template-canonical).
		public static bool TryLift<T>(ProtoCoreCharacterIIR.InventoryEntry entry, [MaybeNullWhen(false)] out T row) where T : INpcEquipment, new()
		{
			row = default!;
			var item = entry?.Item;
			var k = item?.Key1;
			if (k == null || item!.Key2 == null || item.Key3 == null)
				return false;
			if (!SameKey(k, item.Key2) || !SameKey(k, item.Key3))
				return false;
			if (item.Template1 != item.Template2 || item.ShouldSerializeTemplate3())
				return false;

			// Byte-exact gate: the entry must be fully described by (pos, template, level) + the canonical identity
			// (quad + hash). Rebuild from exactly those and require an exact match, so we never store an entry carrying
			// fields the FK model can't reproduce (extra ProtoAcgItem fields, an absent level/hash, etc.).
			if (!BuildEntry(entry!.Pos, item.Template1, item.Level, k, item.Hash).ToProtobuf().SequenceEqual(entry.ToProtobuf()))
				return false;

			row = new T
			{
				Pos = entry.Pos,
				Template = item.Template1,
				Level = item.Level
			};

			return true;
		}

		private static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(uint pos, uint template, uint level, ProtoDecryptionKey key, uint hash)
		{
			return new ProtoCoreCharacterIIR.InventoryEntry
			{
				Pos = pos,
				Item = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = template,
					Template2 = template,
					Level = level,
					Hash = hash
				}
			};
		}

		private static bool SameKey(ProtoDecryptionKey a, ProtoDecryptionKey b)
		{
			return a.Key1 == b.Key1 && a.Key2 == b.Key2 && a.Key3 == b.Key3 && a.Key4 == b.Key4;
		}
	}
}
