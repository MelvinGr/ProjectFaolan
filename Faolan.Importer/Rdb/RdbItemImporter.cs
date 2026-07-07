using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Importer.Rdb
{
	// Decrypts the client RDB item-template for every item we hold a key for. The TEA key is the item's
	// ProtoDecryptionKey - the same 4 u32 the server streams in inventory and we store as Item.Data1..4 (see
	// docs/archive/rdb-extraction-plan.md "RDBH cracked"). RDB type 1000020 (item) / 1000620 (recipe), id = Item.Id.
	// The decrypted payload is the full canonical template (12-byte RDB-Id header + binary fields); reversing those
	// fields into stat columns is the next step, so for now this validates the decrypt end-to-end and (optionally)
	// dumps the templates for analysis.
	public static class RdbItemImporter
	{
		private const uint MarkerItem = 1000000;

		// String fields inside a template are encoded `tag 0x21 (u32) + length (u16) + flags (u16) + <length> ASCII
		// bytes`. The length is a u16, NOT a u32 - the next u16 is a flags word that is nonzero whenever the string
		// carries backslash-escaped chars (e.g. an apostrophe `\'`). Reading the length as a u32 folds that flags word
		// into the high bytes, so names like "Aspirant's Cap" came out as a bogus 1.5M length and got skipped (#202) -
		// the ~900 names that worked all happened to have flags == 0. The first such string is the display name;
		// internal asset names (mesh refs like `NPC_..._(W0129)`) contain underscores, so we skip those and take the
		// first underscore-free string. NPC/TEST/internal items carry only an asset name and have no display name - we
		// leave their captured (empty) name untouched.
		private const uint StringTag = 0x21;
		private static readonly uint[] ItemTemplateTypes = [1000020, 1000620];

		public static string ExtractName(ReadOnlySpan<byte> template)
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

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string dumpDir = null)
		{
			using var rdb = new RdbReader(rdbDir);

			// (type, id) -> entry for the template types, skipping empty/sentinel entries.
			var byTypeId = new Dictionary<(uint, uint), RdbReader.Entry>();
			foreach (var type in ItemTemplateTypes)
			foreach (var e in rdb.EntriesOfType(type))
			{
				if (e.Size > 0 && e.File != 0xff)
					byTypeId[(type, e.Id)] = e;
			}

			var items = await db.Items.Where(i => i.Data1 != 0 || i.Data2 != 0 || i.Data3 != 0 || i.Data4 != 0).ToListAsync();

			if (dumpDir != null)
				Directory.CreateDirectory(dumpDir);

			int decrypted = 0, noEntry = 0, headerMismatch = 0, decryptFailed = 0, namesAdded = 0, categoriesAdded = 0, valuesAdded = 0;
			var firstSampleShown = false;

			foreach (var item in items)
			{
				var key = new[]
				{
					item.Data1,
					item.Data2,
					item.Data3,
					item.Data4
				};

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
				if (template.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(0, 4)) != matchedType || BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(4, 4)) != item.Id ||
				    BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(8, 4)) != MarkerItem)
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
				// with strictly-decreasing fieldId after the body header (same parse as Captures/scripts/decode_item_template.py).
				// Some templates' tables don't carry field 74 (left at 0 -> the shop falls back to the level price).
				if (TryReadPropertyField(template.AsSpan(12), 74, out var value) && value != item.Value)
				{
					item.Value = value;
					valuesAdded++;
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

			if (namesAdded > 0 || categoriesAdded > 0 || valuesAdded > 0)
				await db.SaveChangesAsync();

			return new Result(items.Count, decrypted, noEntry, headerMismatch, decryptFailed, namesAdded, categoriesAdded, valuesAdded);
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

		// Reads the value of property-table field `fieldId` from a decrypted template body (the span AFTER the
		// 12-byte RDB-Id header). The property table is the longest run of [fieldId u32][value u32] LE pairs with
		// strictly-decreasing fieldId in (0, 2000] - the same grammar Captures/scripts/decode_item_template.py validated.
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

		// Template strings backslash-escape apostrophes/quotes (e.g. "Aspirant\'s Cap"); undo that for the stored name.
		private static string Unescape(string s)
		{
			return s.Contains('\\')
				? s.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\")
				: s;
		}

		public readonly record struct Result(int Keyed, int Decrypted, int NoEntry, int HeaderMismatch, int DecryptFailed, int NamesAdded, int CategoriesAdded, int ValuesAdded);
	}
}
