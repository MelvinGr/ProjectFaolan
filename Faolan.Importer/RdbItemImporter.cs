using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Decrypts the client RDB item-template for every item we hold a key for. The TEA key is the item's
	// ProtoDecryptionKey - the same 4 u32 the server streams in inventory and we store as Item.Data1..4 (see
	// docs/rdb-extraction-plan.md "RDBH cracked"). RDB type 1000020 (item) / 1000620 (recipe), id = Item.Id.
	// The decrypted payload is the full canonical template (12-byte RDB-Id header + binary fields); reversing those
	// fields into stat columns is the next step, so for now this validates the decrypt end-to-end and (optionally)
	// dumps the templates for analysis.
	public static class RdbItemImporter
	{
		private const uint MarkerItem = 1000000;

		// String fields inside a template are encoded `tag 0x21 (u32) + length (u32) + <length> ASCII bytes`. The
		// first such string is the display name; internal asset names (mesh refs like `NPC_..._(W0129)`) contain
		// underscores, so we skip those and take the first underscore-free string. Items whose display name is a
		// localized-string id (not inline) yield none - we leave their captured name untouched.
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
					var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i + 4, 4));
					if (len >= 1 && len <= 1024 && i + 8 + len <= body.Length)
					{
						var s = body.Slice(i + 8, len);
						if (IsPrintableAscii(s))
						{
							var text = Encoding.ASCII.GetString(s);
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

			var items = await db
				.Items
				.Where(i => i.Data1 != 0 || i.Data2 != 0 || i.Data3 != 0 || i.Data4 != 0)
				.ToListAsync();

			if (dumpDir != null)
				Directory.CreateDirectory(dumpDir);

			int decrypted = 0, noEntry = 0, headerMismatch = 0, decryptFailed = 0, namesAdded = 0;
			var firstSampleShown = false;

			foreach (var item in items)
			{
				var key = new[] { item.Data1, item.Data2, item.Data3, item.Data4 };

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

			if (namesAdded > 0)
				await db.SaveChangesAsync();

			return new Result(items.Count, decrypted, noEntry, headerMismatch, decryptFailed, namesAdded);
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

		public readonly record struct Result(int Keyed, int Decrypted, int NoEntry, int HeaderMismatch, int DecryptFailed, int NamesAdded);
	}
}
