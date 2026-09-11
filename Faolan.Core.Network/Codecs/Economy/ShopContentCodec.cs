using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     The <c>DispatchShopData</c> vendor-window body:
	///     <c>[shop ID32][vendor NPC ID32][u32 contentLength][ProtoInventoryGame]</c>.
	/// </summary>
	/// <remarks>
	///     Decoded and validated on all 11 captured vendor windows (dissector tools/capture/decode_shopdata.py). The
	///     content is the same <c>ProtoInventoryGame</c> the FullChar snapshot uses for the player's bag, with
	///     <c>inventory_id.type</c> = 111 (the shop container; the bag is 104) and per-slot <c>InventoryListEntry</c>
	///     items whose <c>ProtoAcgItem</c> tail carries the item's full identity (the Data1..4 quad plus template id and
	///     hash). Shop entries differ from bag entries only in their stat list - (0, 0x200000), (26, count),
	///     (356, perSlot) instead of (0, flags), (23, recordId), (26, count) - and <c>item_id</c> = 0, wares having no
	///     instance GUID.
	/// </remarks>
	public static class ShopContentCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchShopData;

		public const uint ShopEntryFlags = 0x200000;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(ShopContent value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		// From rows. `resolveItem` maps a template id to its Items row (for the quad and hash); unresolvable slots are
		// skipped, since the importer harvests the wares into Items from the same packets and resolution only fails on a
		// hand-made shop with a bogus template.
		public static byte[] Build(uint shopInstance, uint npcHandle, uint maxSize, IEnumerable<ShopItem> items, Func<uint, Item> resolveItem)
		{
			return Build(ShopContents.FromRows(shopInstance, npcHandle, maxSize, items, resolveItem));
		}

		public static ShopContent Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ShopContentCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ShopContent Read(ref SpanReader r)
		{
			r.ExpectU32Be(EntityClass.Shop, "shop entity class");
			var shopInstance = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "vendor entity class");
			var npcHandle = r.ReadU32Be();
			var inv = r.ReadProtobufU32Length<ProtoInventoryGame>();

			var entries = new List<ShopEntry>();
			foreach (var slot in inv.Items)
			{
				var item = slot.Item;
				var data = item?.ItemData;
				if (data?.Key1 == null)
					continue;

				var stats = item!.StatLists.ToDictionary(st => st.Stat, st => st.Value);
				entries.Add(new ShopEntry(slot.Pos, data.Template1, stats.GetValueOrDefault(26u, 1u), stats.GetValueOrDefault(356u), data.Level, data.Hash, data.Key1.Key1, data.Key1.Key2,
					data.Key1.Key3, data.Key1.Key4));
			}

			return new ShopContent(shopInstance, npcHandle, inv.MaxSize, entries);
		}

		internal static void Write(FaolanStream s, ShopContent value)
		{
			{
				var inv = new ProtoInventoryGame
				{
					InventoryId = new ProtoID32
					{
						Type = (uint)ContainerKind.Shop,
						Instance = 0
					},
					MaxSize = value.MaxSize,
					DisabledSlotsCount = 0
				};

				foreach (var entry in value.Entries)
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

				s.WriteUInt32(EntityClass.Shop);
				s.WriteUInt32(value.ShopInstance);
				s.WriteUInt32(EntityClass.Character);
				s.WriteUInt32(value.NpcHandle);
				s.WriteArrayUInt32Length(inv.ToProtobuf());
			}
		}
	}

	/// <summary>
	///     One vendor slot: the ware's position, template and the identity its <c>ProtoAcgItem</c> tail carries.
	/// </summary>
	public sealed record ShopEntry(uint Pos, uint Template, uint Count, uint Stat356, uint TailLevel, uint Hash, uint Data1, uint Data2, uint Data3, uint Data4);

	/// <summary>The decoded DispatchShopData body.</summary>
	public sealed record ShopContent(uint ShopInstance, uint NpcHandle, uint MaxSize, IReadOnlyList<ShopEntry> Entries);

	/// <summary>
	///     Whether a body is a shop window at all.
	/// </summary>
	/// <remarks>
	///     Not a codec: this method index is class-multiplexed, so a caller sweeping a mixed stream cannot dispatch on the
	///     opcode word and has to look at the body. That is why this is a neighbour rather than a third public method on
	///     the codec.
	/// </remarks>
	public static class ShopDataScan
	{
		// A shop-data body: shop ID32 type, char ID32 type, and a content length that lands exactly on the body end.
		public static bool LooksLikeShopData(ReadOnlySpan<byte> body)
		{
			return body.Length > 20 && body.ReadU32Be(0) == EntityClass.Shop && body.ReadU32Be(8) == EntityClass.Character &&
			       body.ReadU32Be(16) == body.Length - 20;
		}
	}

	/// <summary>The shop-window forms, as value factories: a row-to-value mapping.</summary>
	public static class ShopContents
	{
		public static ShopContent FromRows(uint shopInstance, uint npcHandle, uint maxSize, IEnumerable<ShopItem> items, Func<uint, Item> resolveItem)
		{
			ArgumentNullException.ThrowIfNull(items);
			ArgumentNullException.ThrowIfNull(resolveItem);

			var entries = new List<ShopEntry>();
			foreach (var row in items.OrderBy(i => i.Pos))
			{
				var item = resolveItem(row.ItemTemplateId);
				if (item == null)
					continue;

				entries.Add(new ShopEntry(row.Pos, item.Id, row.Count, row.Stat356, row.TailLevel, item.Hash, item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4));
			}

			return new ShopContent(shopInstance, npcHandle, maxSize, entries);
		}
	}
}
