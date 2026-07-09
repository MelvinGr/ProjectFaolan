using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Rdb
{
	// Imports the client RDB LuaArchive (type 1010223) into the LuaScripts table. The archive is a single record: a
	// 12-byte RDB-Id header, then `08 <ver>` and a repeated `12 <len>{ 08 <scriptId> 10 <type> 1a <len> <luaSource> }`
	// (protobuf-style). ~1992 tiny scripts (player:HasTag/AddTag/SpawnItem/SolveGoal/...). See
	// docs/archive/rdb-extraction-plan.md, protocol_conversation_routing.
	public static class RdbLuaImporter
	{
		private const uint LuaArchiveType = 1010223;

		public static async Task<int> ImportAsync(DatabaseContext db, string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var entry = rdb.EntriesOfType(LuaArchiveType).FirstOrDefault(e => e.Size > 0);
			if (entry.Size == 0)
				return 0;

			var dec = rdb.ReadDecompressed(entry);
			var scripts = Parse(dec);

			db.LuaScripts.RemoveRange(db.LuaScripts);
			await db.SaveChangesAsync();
			foreach (var (id, source) in scripts)
			{
				db.LuaScripts.Add(new LuaScript
				{
					Id = id,
					Source = source
				});
			}

			await db.SaveChangesAsync();

			return scripts.Count;
		}

		// Parses the LuaArchive body (after the 12-byte RDB-Id header) into (scriptId, source) pairs.
		public static List<(uint Id, string Source)> Parse(byte[] dec)
		{
			var body = dec.AsSpan(12);
			var p = 0;
			var outv = new List<(uint, string)>();

			// optional leading field1 (08 <varint>)
			if (p < body.Length && body[p] == 0x08)
			{
				p++;
				ReadVarint(body, ref p);
			}

			while (p < body.Length && body[p] == 0x12) // repeated field2 entries
			{
				p++;
				var entryLen = (int)ReadVarint(body, ref p);
				var entryEnd = p + entryLen;
				uint id = 0;
				string source = null;
				while (p < entryEnd)
				{
					var tag = body[p++];
					switch (tag)
					{
						case 0x08: // field1: script id
						{
							id = (uint)ReadVarint(body, ref p);
							break;
						}

						case 0x10: // field2: type
						{
							ReadVarint(body, ref p);
							break;
						}

						case 0x1a: // field3: source string
						{
							var slen = (int)ReadVarint(body, ref p);
							source = Encoding.UTF8.GetString(body.Slice(p, slen));
							p += slen;
							break;
						}

						default:
						{
							p = entryEnd; // unknown - skip to entry end
							break;
						}
					}
				}

				if (source != null)
					outv.Add((id, source));
			}

			return outv;
		}

		private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int p)
		{
			ulong v = 0;
			var shift = 0;
			while (true)
			{
				var x = b[p++];
				v |= (ulong)(x & 0x7f) << shift;
				if ((x & 0x80) == 0)
					break;

				shift += 7;
			}

			return v;
		}
	}
}
