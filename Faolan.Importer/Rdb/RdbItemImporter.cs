using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Decrypts the client RDB item-template for every item we hold a key for. The TEA key is the item's
	// ProtoDecryptionKey - the same 4 u32 the server streams in inventory and we store as Item.Key (Key1..4) (see
	// docs/rdb/rdb-extraction-plan.md "RDBH cracked"). RDB type 1000020 (item) / 1000620 (recipe), id = Item.Id.
	// The decrypted payload is the full canonical template (12-byte RDB-Id header + binary fields); reversing those
	// fields into stat columns is the next step, so for now this validates the decrypt end-to-end and (optionally)
	// dumps the templates for analysis.
	public static class RdbItemImporter
	{
		private const uint BagPropertyField = 104; // = ContainerKind.Bag (0x68)
		private const uint MarkerItem = 1000000;

		// Bag capacity = property field 104 (= the Bag container kind, 0x68) for the normal backpack bag, or field 1331
		// for a RESOURCE bag. Unlike Value/attributes these sit in the property table's non-monotonic tail, so
		// TryReadPropertyField (strictly-decreasing) can't reach them. Scan for the first [fieldId][value] u32 LE pair
		// whose value is a plausible slot count (1..512). Validated: the normal bags (Burlap 24 / Bag of Holding 20 /
		// Veteran's Bag 35 / Max Inventory 216) via 104, and the Max Resource Bag (102) via 1331. Non-bag templates
		// virtually never carry these fields, and the value is only ever used for an item in the matching bag slot, so
		// a stray match is inert.
		// The highest field-499 value: it is 0-indexed (0=Mundane .. 5=Legendary), so the max is the number of tiers
		// above Mundane = Legendary - Mundane = 5.
		private const uint MaxQualityFieldValue = (uint)ItemQuality.Legendary - (uint)ItemQuality.Mundane;
		private const uint ResourceBagPropertyField = 1331; // the resource-bag capacity field (no ContainerKind analogue)

		// String fields inside a template are encoded `tag 0x21 (u32) + length (u16) + flags (u16) + <length> ASCII
		// bytes`. The length is a u16, NOT a u32 - the next u16 is a flags word that is nonzero whenever the string
		// carries backslash-escaped chars (e.g. an apostrophe `\'`). Reading the length as a u32 folds that flags word
		// into the high bytes, so names like "Aspirant's Cap" came out as a bogus 1.5M length and got skipped (#202) -
		// the ~900 names that worked all happened to have flags == 0. The first such string is the display name;
		// internal asset names (mesh refs like `NPC_..._(W0129)`) contain underscores, so we skip those and take the
		// first underscore-free string. NPC/TEST/internal items carry only an asset name and have no display name - we
		// leave their captured (empty) name untouched.
		private const uint StringTag = 0x21;

		// The 5 primary-attribute wire stat ids field 972 is validated to hold (Str/Con/Int/Wis/Dex) - the guard that
		// keeps the attribute-bonus import to the decode-first-clean slice (docs/rdb/item-template-stats.md, #3).
		private static readonly HashSet<uint> AttributeStatIds = [804u, 806u, 808u, 810u, 814u];
		private static readonly uint[] ItemTemplateTypes = [1000020, 1000620];

		public static string? ExtractName(ReadOnlySpan<byte> template)
		{
			var body = template.Length > 12
				? template[12..]
				: default;
			var i = 0;
			while (i + 8 <= body.Length)
			{
				if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i, 4)) == StringTag)
				{
					// length is a u16; body[i+6..i+8] is a flags word (escaping marker) we don't need.
					var len = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(i + 4, 2));
					if (len is >= 1 and <= 1024 && i + 8 + len <= body.Length)
					{
						var s = body.Slice(i + 8, len);
						if (IsPrintableAscii(s))
						{
							var text = Unescape(Encoding.ASCII.GetString(s));
							if (!text.Contains('_'))
								return text;

							i += 8 + len;
							continue;
						}
					}
				}

				i += 4;
			}

			return null;
		}

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? dumpDir = null, string? cdnBase = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);

			// (type, id) -> entry for the template types, skipping empty/sentinel entries. CDN-only (file==0xFF)
			// templates are kept when a mirror is reachable - RdbReader.ReadDecrypted pulls their blob through it.
			var byTypeId = new Dictionary<(uint, uint), RdbReader.Entry>();
			foreach (var type in ItemTemplateTypes)
			foreach (var e in rdb.EntriesOfType(type))
			{
				if (e.Size > 0 && rdb.CanRead(e))
					byTypeId[(type, e.Id)] = e;
			}

			var items = await db.Items.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0).ToListAsync();

			if (dumpDir != null)
				Directory.CreateDirectory(dumpDir);

			int decrypted = 0, noEntry = 0, headerMismatch = 0, decryptFailed = 0, namesAdded = 0, categoriesAdded = 0, valuesAdded = 0, qualitiesAdded = 0, statsAdded = 0, bagsAdded = 0;
			var firstSampleShown = false;

			foreach (var item in items)
			{
				var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };

				RdbReader.Entry entry = default;
				uint matchedType = 0;
				foreach (var type in ItemTemplateTypes)
				{
					if (byTypeId.TryGetValue((type, item.Id), out entry))
					{
						matchedType = type;
						break;
					}
				}

				if (matchedType == 0)
				{
					noEntry++;
					continue;
				}

				byte[] template;
				try
				{
					template = rdb.ReadDecrypted(entry, key);
				}
				catch
				{
					decryptFailed++;
					continue;
				}

				// Validate the 12-byte RDB-Id header: type + id must match what we asked for, marker == 1000000.
				if (template.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(0, 4)) != matchedType ||
				    BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(4, 4)) != item.Id || BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(8, 4)) != MarkerItem)
				{
					headerMismatch++;
					continue;
				}

				decrypted++;

				// The AH search CATEGORY is the first u32 of the template body (right after the 12-byte RDB-Id header).
				// Validated byte-for-byte against 170 captured AH listings (#181). Lets a player's listing be filed
				// under the item-type search node the client queries by, for EVERY item - not just captured ones.
				var category = BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(12, 4));
				if (category != item.SearchCategory)
				{
					item.SearchCategory = category;
					categoriesAdded++;
				}

				// The item base VALUE = property-table field 74 (validated: vendor sell = round(value/10) per unit
				// against the captured sell anchors, #137). The property table is the run of [fieldId][value] u32 pairs
				// with strictly-decreasing fieldId after the body header (same parse as tools/capture/decode_item_template.py).
				// Some templates' tables don't carry field 74 (left at 0 -> the shop falls back to the level price).
				if (TryReadPropertyField(template.AsSpan(12), 74, out var value) && value != item.Value)
				{
					item.Value = value;
					valuesAdded++;
				}

				// The item QUALITY/rarity tier = property field 499 - the client's 0-indexed tooltip tier (0=Mundane ..
				// 5=Legendary), stored directly as ItemQuality (whose values ARE those ids). The strict-decreasing
				// property parse reaches 499 for most gear, but on some templates it sits OUTSIDE the main decreasing
				// run (e.g. the max-capacity bags carry 499=5 at a position the strict parser can't reach - the same
				// blind spot bag slots hit, #397), so fall back to a direct field scan.
				var found = TryReadPropertyField(template.AsSpan(12), 499, out var qualityRaw) && qualityRaw <= MaxQualityFieldValue;
				if (!found)
					found = TryReadQualityAnywhere(template.AsSpan(12), out qualityRaw);
				if (found && item.Quality != (ItemQuality)qualityRaw)
				{
					item.Quality = (ItemQuality)qualityRaw;
					qualitiesAdded++;
				}

				// The VALIDATED primary-attribute modifier (docs/rdb/item-template-stats.md, #3): field 972 = the wire
				// stat id (only ever 804/806/808/810/814 = Str/Con/Int/Wis/Dex across all 1964 templates), field 971 =
				// the amount (+1/+2/+3). Stored on the matching Item.*Bonus column - the (attribute, amount) foundation
				// the MaxStatAggregator sums over equipped gear. Same property-table parse as field 74. (The
				// secondary-modifier list + the vital/HP bonus need a reference tooltip to decode, so they are NOT
				// imported here.)
				if (TryReadPropertyField(template.AsSpan(12), 972, out var statId) && AttributeStatIds.Contains(statId) && TryReadPropertyField(template.AsSpan(12), 971, out var amount) &&
				    amount is > 0 and <= 100 && SetAttributeBonus(item, statId, (int)amount))
					statsAdded++;

				// BAG capacity: property field 104 (= the Bag container kind 0x68) holds the extra backpack slots a bag
				// adds when equipped (the "+N slots" tooltip) - Burlap 24 / Bag of Holding 20 / Veteran's Bag 35. It sits
				// in the table's non-monotonic tail (after the strictly-decreasing run), so it needs the dedicated scan.
				// Only real bags carry it; used server-side only for the item equipped in the bag slot (equip slot 3).
				if (TryReadBagSlots(template.AsSpan(12), BagPropertyField, out var bagSlots) && item.BagExtraSlots != bagSlots)
				{
					item.BagExtraSlots = bagSlots;
					bagsAdded++;
				}

				// RESOURCE-bag capacity: property field 1331 (the resource-bag analogue of field 104, same non-monotonic
				// tail). Only real resource bags carry it; used server-side only for the item in the resource-bag slot
				// (equip slot 26). Max Resource Bag = 102.
				if (TryReadBagSlots(template.AsSpan(12), ResourceBagPropertyField, out var resourceSlots) && item.ResourceBagExtraSlots != resourceSlots)
				{
					item.ResourceBagExtraSlots = resourceSlots;
					bagsAdded++;
				}

				// Fill the display name only when we don't already have one (captured names are authoritative).
				if (string.IsNullOrWhiteSpace(item.Name))
				{
					var name = ExtractName(template);
					if (!string.IsNullOrEmpty(name))
					{
						item.Name = name;
						namesAdded++;
					}
				}

				if (dumpDir != null)
					await File.WriteAllBytesAsync(Path.Combine(dumpDir, $"{matchedType}_{item.Id}.bin"), template);

				if (!firstSampleShown)
				{
					Console.WriteLine($"  sample: id={item.Id} \"{item.Name}\" type={matchedType} templateBytes={template.Length}");
					Console.WriteLine($"          payload[12..]= {Convert.ToHexString(template.AsSpan(12, Math.Min(48, template.Length - 12)))}");
					firstSampleShown = true;
				}
			}

			// The *Bonus columns were set on tracked Item entities above, so they save with everything else. (The old
			// ItemStat-row replace is gone: at most one primary attribute per item and SetAttributeBonus clears the
			// other four, so a changed/moved modifier can't leave a stale value.)
			if (namesAdded > 0 || categoriesAdded > 0 || valuesAdded > 0 || qualitiesAdded > 0 || statsAdded > 0 || bagsAdded > 0)
				await db.SaveChangesAsync();

			if (bagsAdded > 0)
				Console.WriteLine($"  bag capacities (field 104) set on {bagsAdded} item(s).");

			return new Result(items.Count, decrypted, noEntry, headerMismatch, decryptFailed, namesAdded, categoriesAdded, valuesAdded, qualitiesAdded, statsAdded);
		}

		private static bool IsPrintableAscii(ReadOnlySpan<byte> s)
		{
			foreach (var c in s)
			{
				if (c < 32 || c > 126)
					return false;
			}

			return true;
		}

		// Sets the item's primary-attribute bonus for wire stat id `statId` (804/806/808/810/814) to `amount`, and
		// clears the other four (a template carries at most one primary attribute, so a modifier that MOVED to a
		// different attribute leaves no stale column). Returns whether anything actually changed (drives the counter +
		// the save). Mirrors how Value/Quality are written onto the tracked Item.
		private static bool SetAttributeBonus(Item item, uint statId, int amount)
		{
			int? str = null, con = null, @int = null, wis = null, dex = null;
			switch (statId)
			{
				case 804:
					str = amount;
					break;
				case 806:
					con = amount;
					break;
				case 808:
					@int = amount;
					break;
				case 810:
					wis = amount;
					break;
				case 814:
					dex = amount;
					break;
			}

			if (item.StrengthBonus == str && item.ConstitutionBonus == con && item.IntelligenceBonus == @int && item.WisdomBonus == wis && item.DexterityBonus == dex)
				return false;

			item.StrengthBonus = str;
			item.ConstitutionBonus = con;
			item.IntelligenceBonus = @int;
			item.WisdomBonus = wis;
			item.DexterityBonus = dex;
			return true;
		}

		private static bool TryReadBagSlots(ReadOnlySpan<byte> body, uint fieldId, out uint slots)
		{
			slots = 0;
			for (var i = 0; i + 8 <= body.Length; i += 4)
			{
				if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i, 4)) != fieldId)
					continue;

				var v = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i + 4, 4));
				if (v is >= 1 and <= 512)
				{
					slots = v;
					return true;
				}
			}

			return false;
		}

		// Reads the value of property-table field `fieldId` from a decrypted template body (the span AFTER the
		// 12-byte RDB-Id header). The property table is the longest run of [fieldId u32][value u32] LE pairs with
		// strictly-decreasing fieldId in (0, 2000] - the same grammar tools/capture/decode_item_template.py validated.
		// Returns false when no such run contains the field (some template kinds don't carry it).
		private static bool TryReadPropertyField(ReadOnlySpan<byte> body, uint fieldId, out uint value)
		{
			value = 0;
			var words = body.Length / 4;
			var bestLen = 0;
			var bestValue = 0u;
			var bestFound = false;
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0;
				var haveLast = false;
				var len = 0;
				var found = false;
				var fv = 0u;
				for (var j = start; j + 1 < words; j++)
				{
					var fid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j * 4, 4));
					if (fid == 0 || fid > 2000 || (haveLast && fid >= last))
						break;

					var val = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice((j + 1) * 4, 4));
					if (fid == fieldId)
					{
						found = true;
						fv = val;
					}

					last = fid;
					haveLast = true;
					len++;
					j++; // consumed the value word too
				}

				if (len > bestLen)
				{
					bestLen = len;
					bestFound = found;
					bestValue = fv;
				}
			}

			if (bestFound)
				value = bestValue;
			return bestFound;
		}

		// Direct scan for field 499 (item quality, 0-indexed) at any 4-byte-aligned position, taking the following word
		// as the value and accepting only the 0..5 field range. Mirrors TryReadBagSlots: the fallback for templates
		// whose 499 is outside the strict-decreasing property run (#397).
		private static bool TryReadQualityAnywhere(ReadOnlySpan<byte> body, out uint value)
		{
			value = 0;
			for (var i = 0; i + 8 <= body.Length; i += 4)
			{
				if (BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i, 4)) != 499)
					continue;

				var v = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i + 4, 4));
				if (v <= MaxQualityFieldValue)
				{
					value = v;
					return true;
				}
			}

			return false;
		}

		// Template strings backslash-escape apostrophes/quotes (e.g. "Aspirant\'s Cap"); undo that for the stored name.
		private static string Unescape(string s)
		{
			return s.Contains('\\')
				? s.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\")
				: s;
		}

		public readonly record struct Result(
			int Keyed,
			int Decrypted,
			int NoEntry,
			int HeaderMismatch,
			int DecryptFailed,
			int NamesAdded,
			int CategoriesAdded,
			int ValuesAdded,
			int QualitiesAdded,
			int StatsAdded);
	}
}
