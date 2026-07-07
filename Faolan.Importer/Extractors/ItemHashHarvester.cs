using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.FullCharSnapshot;
using Faolan.GameServer.Spawn;
using ProtoBuf;

namespace Faolan.Importer.Extractors
{
	// Harvests the full identity of every item seen in captures: the template id, the Data1..4 identity quad, and
	// the hash. Two sources, both yielding (template -> CapturedItem): the 0x737A6DF9 inventory snapshot
	// (bag/equipped container entries, tail.f8 hash) AND the FA015807 character SPAWN's worn-equipment entries
	// (ProtoAcgItem: Key1..4 = the quad, Template1 = id, Hash = hash). The spawn source is what makes items a
	// character only ever WORE (never carried in a bag snapshot - e.g. Seadog's Shorts) buildable, so the spawn
	// equipment renderer (PlayerSpawnEquipment, gated on Hash != 0) can draw them. The quad + hash are
	// exactly what SnapshotItemBuilder / PlayerSpawnEquipment need. Merged result is embedded as
	// Data/archive/captured-items.tsv and imported into the `Items` table. See docs/archive/spawn-dissection-plan.md Phase 5.
	public static class ItemHashHarvester
	{
		private const uint BankIirSubOpcode = (uint)GameServer0X2000Opcodes.BankIIR; // 0x0BA1C1CB - same framing

		// The container an item was captured in IS its kind: ItemType's values are the same protocol container-kind
		// numbers as ContainerKind (Bag=Normal 104 / Resource 112 / Quest 113), so those map straight. EXCEPT the
		// equipped container (101): that is a LOCATION, not an item class - worn gear is ordinary Normal loot - so it
		// folds into Normal, keeping the catalog to the three real item classes (the routing hint that keeps quest/
		// resource items out of random loot).
		private const uint EquippedContainerKind = 101;
		private const uint InventorySubOpcode = 0x737A6DF9;

		// Parses one on-wire packet and merges every (templateId -> CapturedItem) it can recover - from an inventory
		// snapshot's containers AND/OR a character spawn's worn equipment - into `into`. Conflicting identities for the
		// same template are reported (should not happen).
		public static void Harvest(byte[] packetBytes, IDictionary<uint, CapturedItem> into, IList<string> conflicts = null, IDictionary<uint, ItemType> types = null)
		{
			var found = new Dictionary<uint, CapturedItem>();
			try
			{
				if (TryReadInventoryRoot(packetBytes, out var root))
					found = ExtractFromRoot(root, types);
			}
			catch
			{
				// not a parseable inventory packet - fall through to the spawn-equipment source
			}

			// Also harvest worn equipment from a character spawn (additive; either source may be absent).
			try
			{
				ExtractFromSpawnEquipment(packetBytes, found);
			}
			catch
			{
				// not a parseable spawn packet
			}

			// Third source: vendor windows (DispatchShopData 0x2033) - every ware carries the same ProtoAcgItem
			// identity (quad + template + hash), so a single shop capture adds its whole stock to the
			// buildable-item catalog. See ShopContentCodec / Captures/scripts/decode_shopdata.py.
			try
			{
				ExtractFromShopData(packetBytes, found);
			}
			catch
			{
				// not a parseable shop packet
			}

			// Fourth source: loot bags (LootBagFullUpdateIIR 0xF096251E) - the bag's ProtoLootBagIIR inventory holds the
			// same ProtoAcgItem identities, so the items only ever seen as loot (e.g. the Casilda chain key) get into the
			// Items table too. Unlike the buildable sources, hash-0 loot items are kept (quest keys have none).
			try
			{
				ExtractFromLootBag(packetBytes, found);
			}
			catch
			{
				// not a parseable loot-bag packet
			}

			// Fifth source: live DispatchAddItem (0x2020) - its inventoryID container type IS the item's kind (0x68 Bag /
			// 0x71 Quest), so a quest key delivered to the quest tab is TYPED Quest from data (no domain-knowledge guess).
			// This is the only source that types an item the snapshot never showed in its quest container (the key only
			// arrives via this live add in the chain captures). Carries `types` so the classification reaches the upsert.
			try
			{
				ExtractFromAddItem(packetBytes, found, types);
			}
			catch
			{
				// not a parseable add-item packet
			}

			// Sixth source: the personal BANK (BankIIR 0x0BA1C1CB). Unlike the world-entry snapshot (a multi-container
			// root), the bank packet carries a SINGLE container blob (kind 105) after [c350][instance][u8 env][u32 len];
			// its field-4 entries are the stashed items in the same InventoryListEntry codec. This is the ONLY source
			// that reaches items a character merely STASHED (crafting mats/food/spare gear - never worn, vended, looted).
			try
			{
				ExtractFromBankIir(packetBytes, found);
			}
			catch
			{
				// not a parseable bank packet
			}

			// Seventh source: mail attachments (DispatchMailMessage 0x2002). The AH-buy delivery escrows the bought
			// item as a mail attachment - a ProtoInventoryGameEntry in the same codec as everywhere else. Taken mail
			// items also arrive via the live-add source, so this only adds ones seen in a mailbox but never claimed;
			// still worth harvesting so a future mail capture's items land without a code change.
			try
			{
				ExtractFromMail(packetBytes, found);
			}
			catch
			{
				// not a parseable mail packet
			}

			foreach (var (template, item) in found)
			{
				if (into.TryGetValue(template, out var existing))
				{
					if (!existing.Equals(item))
						conflicts?.Add($"template {template}: {existing} vs {item}");
					continue;
				}

				into[template] = item;
			}
		}

		private static ItemType ClassifyContainer(FullCharSnapshotReader.ContainerKind kind)
		{
			return (uint)kind == EquippedContainerKind
				? ItemType.Normal
				: (ItemType)(uint)kind;
		}

		// Recovers (template -> CapturedItem) AND the item's TYPE from a live DispatchAddItem (GameCharInterface s2c
		// 0x2020). Its inventoryID's container type IS the item's kind (the protocol numbers equal ItemType/ContainerKind:
		// 0x68 = 104 Bag/Normal, 0x71 = 113 Quest), so the Casilda chain key - delivered to the quest tab via a live 0x71
		// add and NEVER present in any snapshot quest container - is typed Quest straight from the wire, no hardcoded
		// override. Body grammar mirrors WriteAddItemBody: [u32 containerType][u32 charId][u32 itemPos][u32 len + entry]
		// [u32 feedback]; the entry is a bare ProtoInventoryGameEntry (no Pos wrapper).
		private static void ExtractFromAddItem(byte[] packetBytes, IDictionary<uint, CapturedItem> into, IDictionary<uint, ItemType> types)
		{
			var packet = new ConanPacket(packetBytes);
			if (packet.Opcode != 0x2020)
				return;

			var body = packet.Body;
			var containerType = body.ReadUInt32();
			body.ReadUInt32(); // charId
			body.ReadUInt32(); // itemPos
			var entryBytes = body.ReadArrayUInt32Length();

			var entry = Serializer.Deserialize<ProtoInventoryGameEntry>(entryBytes.AsMemory());
			var acg = entry?.ItemData;
			if (acg?.Key1 == null || acg.Template1 == 0)
				return;

			into.TryAdd(acg.Template1, new CapturedItem(acg.Key1.Key1, acg.Key1.Key2, acg.Key1.Key3, acg.Key1.Key4, acg.Hash));

			// Only record the specific kinds (Quest/Resource); Normal items are left to the DB default so this never
			// downgrades an item another source already classified.
			var type = (ItemType)containerType;
			if (types != null && Enum.IsDefined(typeof(ItemType), type) && type != ItemType.Normal)
				types[acg.Template1] = type;
		}

		// Recovers (template -> CapturedItem) from a BankIIR (0x0BA1C1CB) - the personal bank, delivered on-demand
		// (protocol_bank_storage). Body after the sub-opcode is [u32 c350][u32 instance][u8 env=01][u32 len][blob],
		// where blob is a SINGLE ProtoInventoryGame container (kind 105 = ContainerKind.Bank) whose field-4 entries
		// use the same InventoryListEntry codec as the snapshot. Bank entries carry a real hash, so banked-only
		// items become buildable too. Identity only - the bank holds every kind in one container, so no type stamp.
		private static void ExtractFromBankIir(byte[] packetBytes, IDictionary<uint, CapturedItem> into)
		{
			var packet = new ConanPacket(packetBytes);
			if (packet.Opcode != 0x2000)
				return;

			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			if (body.ReadUInt32() != BankIirSubOpcode)
				return;

			body.ReadUInt32(); // c350
			body.ReadUInt32(); // instance
			body.ReadByte(); // env marker (0x01)
			var blob = body.ReadArrayUInt32Length();

			using var ms = new MemoryStream(blob, false);
			var container = Serializer.Deserialize<ProtoInventoryGame>(ms);
			if (FullCharSnapshotReader.GetContainerKind(container) != (ulong)FullCharSnapshotReader.ContainerKind.Bank)
				return;

			foreach (var entry in FullCharSnapshotReader.GetContainerEntries(container))
			{
				var template = FullCharSnapshotReader.GetEntryTemplateId(entry);
				var quad = FullCharSnapshotReader.GetEntryIdentityQuad(entry);
				var hash = FullCharSnapshotReader.GetEntryArchetype(entry);
				if (template.HasValue && quad.HasValue)
					into.TryAdd(template.Value, new CapturedItem(quad.Value.Data1, quad.Value.Data2, quad.Value.Data3, quad.Value.Data4, hash ?? 0));
			}
		}

		// Recovers (template -> CapturedItem) from a loot bag IIR carrier (0x2000 packet whose payload sub-opcode is
		// 0xF096251E). The trailing ProtoLootBagIIR's inventory entries each carry a ProtoAcgItem (Key1 = the quad,
		// Template1 = the id, Hash = the hash) - identical to the snapshot/spawn sources. Hash 0 is kept here
		// (quest keys legitimately have none), so the item still lands in the DB even though it won't render in a snapshot.
		private static void ExtractFromLootBag(byte[] packetBytes, IDictionary<uint, CapturedItem> into)
		{
			var packet = new ConanPacket(packetBytes);
			if (packet.Opcode != 0x2000)
				return;

			var payload = packet.Body.ReadArrayUInt32Length();
			var s = new FaolanStream(payload);
			if (s.ReadUInt32() != LootBagMessageCodec.SubOpcode)
				return;

			if (!LootBagMessageCodec.TryParse(payload.AsSpan(4), out var contents, out _) || contents?.Inventory?.Items == null)
				return;

			foreach (var listEntry in contents.Inventory.Items)
			{
				var acg = listEntry.Item?.ItemData;
				if (acg?.Key1 == null || acg.Template1 == 0)
					continue;

				into.TryAdd(acg.Template1, new CapturedItem(acg.Key1.Key1, acg.Key1.Key2, acg.Key1.Key3, acg.Key1.Key4, acg.Hash));
			}
		}

		// Recovers (template -> CapturedItem) from a DispatchMailMessage (S->C 0x2002) that carries attachments (the
		// AH-buy delivery escrows the bought item as a mail attachment). Reuses MailMessageCodec.Parse - the canonical
		// inverse of the builder - so the wire grammar lives in one place. 0x2002 is class-multiplexed, but a non-mail
		// body either throws (caught by the caller) or yields attachments with no real identity, which we skip.
		private static void ExtractFromMail(byte[] packetBytes, IDictionary<uint, CapturedItem> into)
		{
			var packet = new ConanPacket(packetBytes);
			if (packet.Opcode != 0x2002)
				return;

			var mail = MailMessageCodec.Parse(packet.Body.ToArray());
			foreach (var a in mail.Attachments)
			{
				if (a.Template != 0 && (a.Data1 | a.Data2 | a.Data3 | a.Data4) != 0)
					into.TryAdd(a.Template, new CapturedItem(a.Data1, a.Data2, a.Data3, a.Data4, a.ItemHash));
			}
		}

		// `types` (optional) receives the item's classification from the container it was found in - only the
		// specific kinds (Quest/Resource) are recorded; Normal items are left to the DB default. Kept separate from
		// CapturedItem so the identity quad's equality (the conflict check) is unaffected.
		private static Dictionary<uint, CapturedItem> ExtractFromRoot(ProtoFullCharacterIIR root, IDictionary<uint, ItemType> types = null)
		{
			var map = new Dictionary<uint, CapturedItem>();

			foreach (FullCharSnapshotReader.ContainerKind kind in Enum.GetValues(typeof(FullCharSnapshotReader.ContainerKind)))
			{
				var container = FullCharSnapshotReader.GetContainer(root, kind);
				var itemType = ClassifyContainer(kind);

				foreach (var entry in FullCharSnapshotReader.GetContainerEntries(container))
				{
					var template = FullCharSnapshotReader.GetEntryTemplateId(entry);
					var hash = FullCharSnapshotReader.GetEntryArchetype(entry);
					var quad = FullCharSnapshotReader.GetEntryIdentityQuad(entry);

					if (template.HasValue && hash.HasValue && quad.HasValue)
					{
						map[template.Value] = new CapturedItem(quad.Value.Data1, quad.Value.Data2, quad.Value.Data3, quad.Value.Data4, hash.Value);
						// Only classify from a container whose kind IS a real ItemType (Resource/Quest); the Bank (105)
						// holds items of every kind in one container, so it must NOT stamp a type - leave the DB default.
						if (types != null && itemType != ItemType.Normal && Enum.IsDefined(typeof(ItemType), itemType))
							types[template.Value] = itemType;
					}
				}
			}

			return map;
		}

		// Recovers (template -> CapturedItem) from a character SPAWN's worn equipment. The spawn body holds a
		// ProtoCoreCharacterIIR whose InventoryEntries are the worn items, each a ProtoAcgItem carrying the same
		// identity the inventory snapshot does (Key1.Key1..4 = the Data quad, Template1 = the template id, Hash =
		// the hash). PlayerSpawnReader scans the body for the character message, so this works on any packet
		// that contains a spawn (FA015807) - we just try it on every packet's Conan body.
		private static void ExtractFromShopData(byte[] packetBytes, IDictionary<uint, CapturedItem> into)
		{
			if (packetBytes.Length < 12 + 32)
				return;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart >= packetBytes.Length)
				return;

			var body = packetBytes[bodyStart..];
			if (!ShopContentCodec.LooksLikeShopData(body))
				return;

			foreach (var e in ShopContentCodec.Parse(body).Entries)
				into.TryAdd(e.Template, new CapturedItem(e.Data1, e.Data2, e.Data3, e.Data4, e.Hash));
		}

		private static void ExtractFromSpawnEquipment(byte[] packetBytes, IDictionary<uint, CapturedItem> into)
		{
			if (packetBytes == null || packetBytes.Length < 12)
				return;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart >= packetBytes.Length)
				return;

			if (!PlayerSpawnReader.TryReadCharacterMessage(packetBytes[bodyStart..], out var read))
				return;

			foreach (var entry in read.Message.InventoryEntries)
			{
				var acg = entry.Item;
				if (acg?.Key1 == null)
					continue;

				var template = acg.Template1;
				var hash = acg.Hash;
				if (template == 0 || hash == 0)
					continue; // need both a template and a real hash to be buildable

				into[template] = new CapturedItem(acg.Key1.Key1, acg.Key1.Key2, acg.Key1.Key3, acg.Key1.Key4, hash);
			}
		}

		private static bool TryReadInventoryRoot(byte[] packetBytes, out ProtoFullCharacterIIR root)
		{
			root = null;

			var packet = new ConanPacket(packetBytes);
			if (packet.Opcode != 0x2000)
				return false;

			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			if (subOpcode != InventorySubOpcode)
				return false;

			body.ReadUInt32(); // c350
			body.ReadUInt32(); // instance

			root = FullCharSnapshotReader.ParseInventoryPacketBody(body).Root;
			return true;
		}

		public readonly record struct CapturedItem(uint Data1, uint Data2, uint Data3, uint Data4, uint Hash);
	}
}
