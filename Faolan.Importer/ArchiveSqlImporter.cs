using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// One-time(ish) import of the OLD C++ server's ability/spell DEFINITIONS from the archive SQL (Archive/, e.g.
	// the extracted aocdatabase.sql) - data that is NOT in the captures: `p_abilities` (ability templates + identity
	// quad) -> Abilities, and `const_abilitys` (class/level -> ability+spell id lists) -> ClassAbilities. Run with
	// `--archive <dir-containing-aocdatabase.sql>`. Upserts (no deletes), so it is safe to re-run.
	public static class ArchiveSqlImporter
	{
		// The ability "slot" (0x2052 grant leading word) lives in the OLD archive sqlite `abilities` table, not in
		// `p_abilities`, so we carry it here keyed by ability id (mirrors the AddAbilitySlot migration backfill).
		private static readonly Dictionary<uint, uint> AbilitySlots = new Dictionary<uint, uint>
		{
			[269272] = 106, // Rest
			[769094] = 108, // Normal Attack Left
			[769095] = 109, // Normal Attack Right
			[769099] = 112, // Normal Attack Front
			[3316611] = 1250, // toggle combat
			[3675584] = 259 // switch weapon
		};

		public static async Task<(int Abilities, int ClassAbilities)> ImportAsync(IDatabaseContext db, string sqlDir)
		{
			var sql = await File.ReadAllTextAsync(Path.Combine(sqlDir, "aocdatabase.sql"));

			// p_abilities: name, id, level, data0..4
			var existingAbilities = await db.Abilities.ToDictionaryAsync(a => a.Id);
			foreach (var r in ParseInserts(sql, "p_abilities"))
			{
				var id = U(r[1]);
				if (!existingAbilities.TryGetValue(id, out var a))
				{
					a = new Ability { Id = id };
					db.Abilities.Add(a);
					existingAbilities[id] = a;
				}

				a.Name = r[0] ?? string.Empty;
				a.Level = B(r[2]);
				a.Data0 = U(r[3]);
				a.Data1 = U(r[4]);
				a.Data2 = U(r[5]);
				a.Data3 = U(r[6]);
				a.Data4 = U(r[7]);
				if (AbilitySlots.TryGetValue(id, out var slot))
					a.Slot = slot;
			}

			// const_abilitys: class, level, abilitys, abilitysNew, spells, spellsNew
			var existingClass = await db.ClassAbilities.ToDictionaryAsync(c => (c.Class, c.Level));
			foreach (var r in ParseInserts(sql, "const_abilitys"))
			{
				var key = (U(r[0]), U(r[1]));
				if (!existingClass.TryGetValue(key, out var c))
				{
					c = new ClassAbility
					{
						Class = key.Item1,
						Level = key.Item2
					};
					db.ClassAbilities.Add(c);
					existingClass[key] = c;
				}

				c.Abilities = r[2] ?? string.Empty;
				c.AbilitiesNew = r.Length > 3
					? r[3] ?? string.Empty
					: string.Empty;
				c.Spells = r.Length > 4
					? r[4] ?? string.Empty
					: string.Empty;
				c.SpellsNew = r.Length > 5
					? r[5] ?? string.Empty
					: string.Empty;
			}

			await db.SaveChangesAsync();
			return (existingAbilities.Count, existingClass.Count);
		}

		// Tolerant parser for MySQL `INSERT INTO `table` ... VALUES (..),(..);` dumps (single- or multi-row),
		// returning each tuple's fields with quotes stripped, `\'`/`\\` unescaped, and NULL -> null.
		internal static List<string[]> ParseInserts(string sql, string table)
		{
			var result = new List<string[]>();
			var marker = "INSERT INTO `" + table + "`";
			var idx = 0;
			while ((idx = sql.IndexOf(marker, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
			{
				var valuesPos = sql.IndexOf("VALUES", idx, StringComparison.OrdinalIgnoreCase);
				if (valuesPos < 0)
					break;

				var p = valuesPos + "VALUES".Length;
				var end = FindStatementEnd(sql, p);
				ParseTuples(sql, p, end, result);
				idx = end;
			}

			return result;
		}

		private static byte B(string s)
		{
			return (byte)Math.Min(U(s), byte.MaxValue);
		}

		private static int FindStatementEnd(string sql, int start)
		{
			var inQuote = false;
			for (var i = start; i < sql.Length; i++)
			{
				var c = sql[i];
				if (c == '\\')
				{
					i++;
					continue;
				}

				if (c == '\'')
					inQuote = !inQuote;
				else if (c == ';' && !inQuote)
					return i;
			}

			return sql.Length;
		}

		private static string Finish(StringBuilder sb, bool isNull)
		{
			var s = sb.ToString();
			return isNull && s.Equals("NULL", StringComparison.OrdinalIgnoreCase)
				? null
				: s;
		}

		private static void ParseTuples(string sql, int start, int end, List<string[]> result)
		{
			var i = start;
			while (i < end)
			{
				while (i < end && sql[i] != '(')
					i++;
				if (i >= end)
					break;

				i++;

				var fields = new List<string>();
				var sb = new StringBuilder();
				var inQuote = false;
				var isNull = true;

				while (i < end)
				{
					var c = sql[i];
					if (inQuote)
					{
						if (c == '\\' && i + 1 < end)
						{
							sb.Append(sql[i + 1]);
							i += 2;
							continue;
						}

						if (c == '\'')
						{
							inQuote = false;
							i++;
							continue;
						}

						sb.Append(c);
						i++;
						continue;
					}

					if (c == '\'')
					{
						inQuote = true;
						isNull = false;
						i++;
						continue;
					}

					if (c == ',' || c == ')')
					{
						fields.Add(Finish(sb, isNull));
						sb.Clear();
						isNull = true;
						i++;
						if (c == ')')
							break;

						continue;
					}

					if (!char.IsWhiteSpace(c))
						sb.Append(c);
					i++;
				}

				if (fields.Count > 0)
					result.Add(fields.ToArray());
			}
		}

		private static uint U(string s)
		{
			return string.IsNullOrEmpty(s) ? 0u :
				uint.TryParse(s, out var v) ? v : (uint)(long.TryParse(s, out var l)
					? l
					: 0);
		}
	}
}
