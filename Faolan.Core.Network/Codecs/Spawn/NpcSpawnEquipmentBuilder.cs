using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Builds and lifts an NPC's worn-equipment InventoryEntries - the CharacterIIR f23 list - to and from equipment
	///     rows, so the equipment generates from the database rather than a captured blob.
	/// </summary>
	/// <remarks>
	///     The NPC counterpart of PlayerSpawnEquipment: the captured NPC items follow the same ProtoAcgItem shape the
	///     player builder emits - the three keys identical, the two templates identical, no third template - so a row stores
	///     only the key quad, template, level and hash, and <see cref="BuildEntries" /> reproduces the wire item
	///     byte-for-byte.
	/// </remarks>
	public static class NpcSpawnEquipmentBuilder
	{
		/// <summary>
		///     Rebuilds the InventoryEntries, ordered by slot, from the stored rows - per-placement NpcSpawnEquipment or
		///     per-type NpcEquipment, both INpcEquipment.
		/// </summary>
		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildEntries(IEnumerable<INpcEquipment> rows)
		{
			return rows.OrderBy(r => r.Pos).Select(BuildEntry).ToList();
		}

		public static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(INpcEquipment row)
		{
			// The item identity - key quad and hash - lives in Items and is reached through the foreign-key navigation.
			// Require it loaded rather than silently emitting an empty item.
			var item = row.Item ?? throw new InvalidOperationException(
				$"NPC worn-equipment slot {row.Pos} (template {row.Template}): the Item navigation is not loaded - Include it before building the spawn.");

			return BuildEntry(row.Pos, row.Template, row.Level, item.Key, item.Hash);
		}

		/// <summary>
		///     Lifts a captured InventoryEntry into a row - per-placement NpcSpawnEquipment or per-type NpcEquipment through
		///     the type parameter - but only if it matches the reproducible shape: the three keys identical, the two
		///     templates identical, no third template. Returns false for any entry outside that shape, so the importer can
		///     decline the whole NPC rather than store something the builder would not reproduce byte-for-byte.
		/// </summary>
		/// <remarks>
		///     The row stores only the slot, template and level; the key quad and hash it validates against belong to the
		///     Items row the foreign key on Template points at, harvested from this same spawn packet, so they match by
		///     construction.
		/// </remarks>
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

			// Byte-exact gate: the entry must be fully described by (pos, template, level) plus the canonical identity
			// (key quad and hash). Rebuild from exactly those and require an exact match, so an entry carrying fields
			// the row model cannot reproduce - extra ProtoAcgItem fields, an absent level or hash - is never stored.
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
