using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs
{
	// Parses/builds the DispatchShopData (DL 0x2033) vendor-window body. Wire grammar (decoded 2026-06-12,
	// validated on all 11 captured vendor windows; dissector Captures/scripts/decode_shopdata.py):
	//   [shop ID32 (type 0xC75B)][vendor NPC ID32 (type 0xC350)][u32 contentLength][ProtoInventoryGame]
	// The content is the SAME ProtoInventoryGame the FullChar snapshot uses for the player's bag, with
	// inventory_id.type = 111 (the shop container; bag = 104) and per-slot InventoryListEntry items whose
	// ProtoAcgItem tail carries the item's full identity (Data1..4 quad + template id + hash). Shop
	// entries differ from bag entries only in their stat list: (0, 0x200000), (26, count), (356, perSlot)
	// instead of (0, flags), (23, recordId), (26, count) - and item_id = 0 (wares have no instance GUID).
	public static class ShopContentCodec
	{
		public const uint ShopEntryFlags = 0x200000;
		private const uint CharacterWireType = EntityClass.Character;

		// Builds the full 0x2033 body from rows. `resolveItem` maps a template id to its Items row (for the
		// quad + hash); unresolvable slots are skipped (the importer harvests the wares into Items from
		// the same packets, so resolution only fails on a hand-made shop with a bogus template). Maps the
		// domain rows to the wire ShopContent, then serializes it (ICodec).
		public static byte[] Build(uint shopInstance, uint npcHandle, uint maxSize, IEnumerable<ShopItem> items, Func<uint, Item> resolveItem)
		{
			var entries = new List<ShopEntry>();
			foreach (var row in items.OrderBy(i => i.Pos))
			{
				var item = resolveItem(row.ItemTemplateId);
				if (item == null)
					continue;

				entries.Add(new ShopEntry(row.Pos, item.Id, row.Count, row.Stat356, row.TailLevel, item.Hash, item.Data1, item.Data2, item.Data3, item.Data4));
			}

			return new ShopContent(shopInstance, npcHandle, maxSize, entries).ToArray();
		}

		// True when `body` (the bytes after the 16-bit opcode) is a shop-data body: shop ID32 type + char ID32
		// type + a content length that lands exactly on the body end.
		public static bool LooksLikeShopData(ReadOnlySpan<byte> body)
		{
			return body.Length > 20 && BinaryPrimitives.ReadUInt32BigEndian(body) == Shop.WireType && BinaryPrimitives.ReadUInt32BigEndian(body[8..]) == CharacterWireType &&
				   BinaryPrimitives.ReadUInt32BigEndian(body[16..]) == body.Length - 20;
		}

		public static ShopContent Parse(byte[] body)
		{
			if (!LooksLikeShopData(body))
				throw new InvalidDataException("not a DispatchShopData body");

			return ShopContent.Read(new FaolanStream(body));
		}

		public sealed record ShopEntry(uint Pos, uint Template, uint Count, uint Stat356, uint TailLevel, uint Hash, uint Data1, uint Data2, uint Data3, uint Data4);

		// The decoded DispatchShopData body as a self-serializing codec (ICodec): Write frames the shop/vendor ID32
		// header + the ProtoInventoryGame built from Entries; Read recovers the header + per-slot wares.
		public sealed record ShopContent(uint ShopInstance, uint NpcHandle, uint MaxSize, IReadOnlyList<ShopEntry> Entries) : ICodec<ShopContent>
		{
			public void Write(FaolanStream s)
			{
				var inv = new ProtoInventoryGame
				{
					InventoryId = new ProtoID32
					{
						Type = Shop.ContainerType,
						Instance = 0
					},
					MaxSize = MaxSize,
					DisabledSlotsCount = 0
				};

				foreach (var entry in Entries)
				{
					var key = new ProtoDecryptionKey
					{
						Key1 = entry.Data1,
						Key2 = entry.Data2,
						Key3 = entry.Data3,
						Key4 = entry.Data4
					};
					inv.Items.Add(new ProtoInventoryGame.InventoryListEntry
					{
						Pos = entry.Pos,
						Item = new ProtoInventoryGameEntry
						{
							ItemId = 0,
							StatLists =
							{
								new ProtoInventoryGameEntry.StatValue
								{
									Stat = 0,
									Value = ShopEntryFlags
								},
								new ProtoInventoryGameEntry.StatValue
								{
									Stat = 26,
									Value = entry.Count
								},
								new ProtoInventoryGameEntry.StatValue
								{
									Stat = 356,
									Value = entry.Stat356
								}
							},
							ItemData = new ProtoAcgItem
							{
								Key1 = key,
								Key2 = key,
								Key3 = key,
								Template1 = entry.Template,
								Template2 = entry.Template,
								Level = entry.TailLevel,
								Hash = entry.Hash
							}
						}
					});
				}

				using var content = new MemoryStream();
				Serializer.Serialize(content, inv);
				var proto = content.ToArray();

				s.WriteUInt32(Shop.WireType);
				s.WriteUInt32(ShopInstance);
				s.WriteUInt32(CharacterWireType);
				s.WriteUInt32(NpcHandle);
				s.WriteUInt32((uint)proto.Length);
				s.WriteArray(proto);
			}

			public static ShopContent Read(FaolanStream s)
			{
				s.ReadUInt32(); // shop ID32 type (Shop.WireType)
				var shopInstance = s.ReadUInt32();
				s.ReadUInt32(); // char ID32 type (CharacterWireType)
				var npcHandle = s.ReadUInt32();
				var contentLength = s.ReadUInt32();
				var proto = s.ReadArray(contentLength);

				using var ms = new MemoryStream(proto);
				var inv = Serializer.Deserialize<ProtoInventoryGame>(ms);

				var entries = new List<ShopEntry>();
				foreach (var slot in inv.Items)
				{
					var item = slot.Item;
					var data = item?.ItemData;
					if (data?.Key1 == null)
						continue;

					var stats = item.StatLists.ToDictionary(st => st.Stat, st => st.Value);
					entries.Add(new ShopEntry(slot.Pos, data.Template1, stats.GetValueOrDefault(26u, 1u), stats.GetValueOrDefault(356u), data.Level, data.Hash, data.Key1.Key1, data.Key1.Key2, data.Key1.Key3, data.Key1.Key4));
				}

				return new ShopContent(shopInstance, npcHandle, inv.MaxSize, entries);
			}
		}
	}
}
