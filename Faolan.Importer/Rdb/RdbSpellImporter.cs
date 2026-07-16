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
	// RdbItemImporter.ExtractName), CastTime, Range, DamageRating, DurationMs and (additively) Level. Records with no
	// name are skipped (asset/aura junk). EffectSpell/ManaCost/min-max damage stay untouched - those really are
	// rank/level-COMPUTED and absent from this record. Idempotent: re-running only rewrites changed values. (#236)
	//
	// Field map (dumped over all 21,502 records, #367): 8=duration, 123=rank, 125=level, 287=range, 294=cast,
	// 347/129/349=splash radius/percent/max-targets, 348=damage rating, 551=a rank-scaling MAGNITUDE, 79=icon (an id
	// into type 1010008 PNG), 321=effect-set (an id into type 1010214, animation/VFX XML only). The record carries NO
	// stat IDs: 551 says "400" but never "400 of WHAT", so a buff's stat identity is server-GDB-only, same as
	// mana/damage/recast (docs/stat-engine.md). Don't hunt stat deltas in here again.
	public static class RdbSpellImporter
	{
		private const uint CastTimeField = 0x126; // 294 - cast time in centiseconds (value * 10 = milliseconds)
		private const uint DamageRatingField = 0x15C; // 348 - spell damage rating * 100 (Ice 3000 -> 30.00%)
		private const uint DurationField = 0x008; // 8 - buff/effect duration in CENTISECONDS (×10ms); stored ×10 as ms. Rend 700=7s, Exhausted 10000=100s; 0 = none (#274)

		// 125 - the level requirement. Verified against the independent wiki scrape: identical for every rank >= 2
		// (Runed Flesh 25/40/55/70, Pestilential Blast 20/35/50/65/80) and MORE COMPLETE than it - the wiki has
		// Pestilential Blast R7 at Level 0, the RDB says 80. 19,239 of 20,822 rows had no level at all, so this is
		// nearly all upside (coverage 1,583 -> 8,202). (#367)
		//
		// Written ADDITIVELY - only where the row has no level yet. The two sources disagree at RANK 1 (Runed Flesh R1:
		// RDB 10 vs wiki 12; Pestilential R1: RDB 1 vs wiki 2) and which is right is unproven, so neither gets to
		// overrule the other on the strength of that.
		// ⚠️ ORDERING: "additive" makes whoever runs FIRST win, it does not make the scrape authoritative.
		// wiki-spell-levels only fills rows at Level == 0 too, so on a DB where this module runs first the RDB value
		// sticks and the wiki silently skips it. Today's faolan.db was scraped first (R1s are 12/2, the wiki values),
		// but a from-scratch rebuild that runs `--only rdb-spells` before the default plan would land 10/1 instead.
		// Harmless while the disagreement is +-2 levels at rank 1; revisit if either source is ever proven right.
		private const uint LevelField = 0x07D;
		private const uint RangeField = 0x11F; // 287 - target range * 100 (Ice Strike 2500 -> 25 metres)
		private const uint SpellDataType = 1040005;

		public static async Task<(int Seen, int Inserted, int NamesSet, int CastTimesSet, int LevelsSet)> ImportAsync(IDatabaseContext db, string rdbDir, string? cdnBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);

			var byId = (await db.Spells.ToListAsync()).ToDictionary(s => s.Id);
			var toAdd = new List<Spell>();

			var seen = 0;
			var inserted = 0;
			var namesSet = 0;
			var castTimesSet = 0;
			var levelsSet = 0;
			foreach (var entry in rdb.EntriesOfType(SpellDataType))
			{
				if (entry.Size == 0 || !rdb.CanRead(entry))
					continue; // empty sentinel, or CDN-only with no reachable mirror / a negative-cached miss

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
					continue; // too short, or a CDN miss (empty)

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
				var level = fields.TryGetValue(LevelField, out var lv) && lv <= byte.MaxValue
					? (byte)lv
					: (byte)0;

				if (!byId.TryGetValue(entry.Id, out var spell))
				{
					spell = new Spell
					{
						Id = entry.Id,
						Name = name,
						CastTime = castTime,
						Range = range,
						DamageRating = damageRating,
						DurationMs = durationMs,
						Level = level
					};
					byId[entry.Id] = spell;
					toAdd.Add(spell);
					inserted++;
					namesSet++;
					if (level != 0)
						levelsSet++;
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

				// Additive: only fill a level the row doesn't have. A scraped value always wins (see LevelField).
				if (spell.Level == 0 && level != 0)
				{
					spell.Level = level;
					levelsSet++;
				}
			}

			if (toAdd.Count > 0)
				db.Spells.AddRange(toAdd);
			await db.SaveChangesAsync();

			logger?.LogInformation("RdbSpellImporter: {Seen} named spell records, {Inserted} new, {NamesSet} names set, {CastTimesSet} cast times set.", seen, inserted, namesSet, castTimesSet);
			return (seen, inserted, namesSet, castTimesSet, levelsSet);
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
