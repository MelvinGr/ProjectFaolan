using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// Backfills spell stats from the client RDB spell-data records (RDB type 1040005, keyed by spell id; plain
	// IOz1/zlib - NOT RDBH-encrypted, so no per-spell key needed, unlike item templates). Each record is a typed
	// container: a 12-byte RDB-Id header ([type][id][..]) followed by a run of [fieldId u32][value u32] LE pairs
	// in STRICTLY-INCREASING fieldId order (the inverse of the item-template table, which is strictly decreasing -
	// see RdbItemImporter.TryReadPropertyField). Validated against the in-client Ice Strike tooltip + 6 spells:
	//   field 0x126 (294) = cast time in centiseconds (value*10 = ms): Ice 200->2.0s, ShockStrike 250->2.5s,
	//     Fire of Gehenna 200->2.0s, Hellfire Stream 100->1.0s, Shocblast 300->3.0s, Inferno of Amher 250->2.5s.
	//   field 0x11F (287) = target range * 100 (Ice 2500 -> 25); field 0x34C (844 -> no; 348) = spell damage
	//     rating * 100 (Ice 3000 -> 30.00%). (Mana/min-max damage are rank/level-COMPUTED, not base fields, so
	//     they are not backfillable from this record.)
	// Catalogs the FULL spell list from the RDB: every type-1040005 record that carries a display name is upserted
	// into the Spell table with its Name (the 0x21 string, same framing as item templates - see
	// RdbItemImporter.ExtractName) and CastTime. Records with no name are skipped (asset/aura junk). Existing rows
	// keep their other columns (Level/EffectSpell/ManaCost/damage - those are rank/level-computed and not in this
	// base record); only Name + CastTime are written. Idempotent: re-running only rewrites changed values. (#236)
	public static class RdbSpellImporter
	{
		private const uint CastTimeField = 0x126; // 294 - cast time in centiseconds (value * 10 = milliseconds)
		private const uint DamageRatingField = 0x15C; // 348 - spell damage rating * 100 (Ice 3000 -> 30.00%)
		private const uint DurationField = 0x008; // 8 - buff/effect duration in CENTISECONDS (×10ms); stored ×10 as ms. Rend 700=7s, Exhausted 10000=100s; 0 = none (#274)
		private const uint RangeField = 0x11F; // 287 - target range * 100 (Ice Strike 2500 -> 25 metres)
		private const uint SpellDataType = 1040005;

		public static async Task<(int Seen, int Inserted, int NamesSet, int CastTimesSet)> ImportAsync(IDatabaseContext db, string rdbDir, ILogger logger = null)
		{
			using var rdb = new RdbReader(rdbDir);

			var byId = (await db.Spells.ToListAsync()).ToDictionary(s => s.Id);
			var toAdd = new List<Spell>();

			var seen = 0;
			var inserted = 0;
			var namesSet = 0;
			var castTimesSet = 0;
			foreach (var entry in rdb.EntriesOfType(SpellDataType))
			{
				if (entry.Size == 0 || entry.File == 0xff)
					continue; // empty sentinel or CDN-only (not in the local rdbdata files)

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue; // not an IOz1/zlib record we can read
				}

				if (body.Length < 12)
					continue;

				var name = RdbItemImporter.ExtractName(body);
				if (string.IsNullOrEmpty(name))
					continue; // no display name -> not a player-facing spell record

				seen++;

				var fields = ReadIncreasingFields(body.AsSpan(12));
				var castTime = fields.TryGetValue(CastTimeField, out var cs)
					? (ushort)Math.Min(cs * 10, ushort.MaxValue)
					: (ushort)0;
				var range = fields.TryGetValue(RangeField, out var r)
					? r / 100f
					: 0f;
				var damageRating = fields.TryGetValue(DamageRatingField, out var dr)
					? dr / 100f
					: 0f;
				var durationMs = fields.TryGetValue(DurationField, out var du)
					? du * 10
					: 0u; // centiseconds -> ms

				if (!byId.TryGetValue(entry.Id, out var spell))
				{
					spell = new Spell
					{
						Id = entry.Id,
						Name = name,
						CastTime = castTime,
						Range = range,
						DamageRating = damageRating,
						DurationMs = durationMs
					};
					byId[entry.Id] = spell;
					toAdd.Add(spell);
					inserted++;
					namesSet++;
					if (castTime != 0)
						castTimesSet++;
					continue;
				}

				if (spell.Name != name)
				{
					spell.Name = name;
					namesSet++;
				}

				if (spell.CastTime != castTime)
				{
					spell.CastTime = castTime;
					castTimesSet++;
				}

				spell.Range = range;
				spell.DamageRating = damageRating;
				spell.DurationMs = durationMs;
			}

			if (toAdd.Count > 0)
				db.Spells.AddRange(toAdd);
			await db.SaveChangesAsync();

			logger?.LogInformation("RdbSpellImporter: {Seen} named spell records, {Inserted} new, {NamesSet} names set, {CastTimesSet} cast times set.", seen, inserted, namesSet, castTimesSet);
			return (seen, inserted, namesSet, castTimesSet);
		}

		// Reads the spell-data field table from a record body (the span AFTER the 12-byte RDB-Id header): the LONGEST
		// run of [fieldId u32][value u32] LE pairs with strictly-INCREASING fieldId in (0, 2000], starting at any
		// offset - the spell-data grammar (the inverse of the item-template's strictly-decreasing table). Returns the
		// fieldId -> value map of that best run.
		private static Dictionary<uint, uint> ReadIncreasingFields(ReadOnlySpan<byte> body)
		{
			var words = body.Length / 4;
			var best = new Dictionary<uint, uint>();
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0;
				var haveLast = false;
				var cur = new Dictionary<uint, uint>();
				for (var j = start; j + 1 < words; j++)
				{
					var fid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j * 4, 4));
					if (fid == 0 || fid > 2000 || (haveLast && fid <= last))
						break;

					cur[fid] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice((j + 1) * 4, 4));
					last = fid;
					haveLast = true;
					j++; // consumed the value word too
				}

				if (cur.Count > best.Count)
					best = cur;
			}

			return best;
		}
	}
}
