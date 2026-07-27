using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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
	// RdbItemImporter.ExtractName), CastTime, Range, DamageRating, DurationMs and (additively) Level.
	// EffectSpell/ManaCost/min-max damage stay untouched - those really are rank/level-COMPUTED and absent from this
	// record. Idempotent: re-running only rewrites changed values. (#236)
	//
	// #402: a record with NO embedded name is NOT automatically junk. A handful of real player-facing spells (e.g.
	// the "Set's Ruin" necro family 222682-222685, "Warlord's March", ...) carry no 0x21 string in their 1040005
	// body - their name lives ONLY in the LDB (RDB 1030002, cat 50210). This is NOT about the display name (the
	// client resolves spell names from its OWN RDB by id; we never send them - the recovered Name is for our
	// tooling). It is about catalog MEMBERSHIP: CharacterSpell.SpellId FKs Spells, and ImportKnownSpells SKIPS any
	// learned id not in the catalog (CaptureCharacterImporter), so an uncataloged spell was dropped from the
	// character's KnownSpellList entirely - missing from the spellbook, not merely unnamed. So a nameless record is
	// still catalogued when the LDB knows it as a real spell name - EXCEPT the LDB's own dev/PvP-internal markers
	// ("XX"/"CTF"-tagged names,
	// see IsDevMarker), which are test/flag buffs, not player spells. The row is inserted with an EMPTY name; the
	// rdb-ldb-names module (RdbLdbNameImporter, run after) cribs the English LDB name onto it. Of 681 nameless
	// records: 189 have no LDB name at all and 467 are dev markers - both stay dropped (asset/aura junk); the
	// remaining 25 are real player spells absent from every other source (the "Set's Ruin"/"Set's Cold Hatred"
	// families, "War Lord's March", "Mass Ignition", ...).
	//
	// Field map (dumped over all 21,502 records, #367): 8=duration, 123=rank, 125=level, 287=range, 294=cast,
	// 347/129/349=splash radius/percent/max-targets, 348=damage rating, 551=a rank-scaling MAGNITUDE, 79=icon (an id
	// into type 1010008 PNG), 321=effect-set (an id into type 1010214, animation/VFX XML only). This FIELD RUN carries
	// no stat ids - 551 says "400" but never "400 of WHAT" - so a buff's magnitudes are server-GDB-only here, same as
	// mana/damage/recast (docs/subsystems/stat-engine.md). Don't hunt stat deltas among the fields again.
	//
	// The `[2][9]` REGION of the same record is the exception, and is imported below: explicit (kind, statId, amount)
	// records, the same shape item templates carry (docs/rdb/spell-stat-effects.md). It covers potions/elixirs and a
	// slice of the class buffs - 866 of 20,847 spells - not the buffs seen in retail captures, which carry no region
	// at all.
	public static class RdbSpellImporter
	{
		private const uint CastTimeField = 0x126; // 294 - cast time in centiseconds (value * 10 = milliseconds)
		private const uint DamageRatingField = 0x15C; // 348 - spell damage rating * 100 (Ice 3000 -> 30.00%)
		private const uint DurationField = 0x008; // 8 - buff/effect duration in CENTISECONDS (×10ms); stored ×10 as ms. Rend 700=7s, Exhausted 10000=100s; 0 = none (#274)

		private const uint LdbType = 1030002; // #402: the localized-string DB, cat 50210 = spell names

		// 125 - the level requirement. Verified against the independent wiki scrape: identical for every rank >= 2
		// (Runed Flesh 25/40/55/70, Pestilential Blast 20/35/50/65/80) and MORE COMPLETE than it - the wiki has
		// Pestilential Blast R7 at Level 0, the RDB says 80. 19,239 of 20,822 rows had no level at all, so this is
		// nearly all upside (coverage 1,583 -> 8,202). (#367)
		//
		// PRECEDENCE: the RDB LEADS on level data - over the ellatha/wiki scrapes AND over captures (decided
		// 2026-07-16: rdb is leading, so if a wiki import says something else, take rdb; rdb is also leading over
		// captures). So this OVERWRITES whatever is there. The RDB and the scrapes agree on 7,135 of the 7,645 spells
		// it knows (93%), so the 185 rows it changes are a small correction to a cross-validated field.
		//
		// Nothing needs protecting from this any more. `starter-spells` used to encode the create-granted kit by
		// PINNING those spells to Level 1 (and pushing every other class level-1 row to 2), so an overwrite here would
		// have silently un-granted a new character's starters. That was always a conflation: retail grants the necro
		// Ice Strike (Rank 1) at create even though its requirement really is level 7 - and the RDB and ellatha AGREE
		// on 7, so the pin was overwriting a CORRECT level with a sentinel. The starter kit is now a capture-proven
		// list on ClassSpell.IsStarter, independent of level, and Spells.Level means only "the level requirement".
		private const uint LevelField = 0x07D;

		// 123 - the spell's RANK. Named in the #367 field dump but never lifted until now; the rank used to come from
		// the "(Rank N)" suffix in the name, and with the ellatha scrape gone the RDB is the only source.
		private const uint RankField = 0x07B;
		private const uint RangeField = 0x11F; // 287 - target range * 100 (Ice Strike 2500 -> 25 metres)
		private const uint SpellDataType = 1040005;

		public static async Task<(int Seen, int Inserted, int NamesSet, int CastTimesSet, int LevelsSet)> ImportAsync(IDatabaseContext db, string rdbDir, string? cdnBase = null,
			ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);

			// #402: the LDB (cat 50210) roster of spell-name ids, used to recover real player spells whose 1040005
			// record embeds no name. The id set is language-independent (every localized page shares it). We collect
			// ALL of an id's localized names, because the dev/PvP-internal "XX"/"CTF" marker is reliable ONLY in
			// certain languages - e.g. id 3977071 is "Bloqué" in French but "XX_Blocked" in English, so a first-page
			// (French) check would miss it. A real spell carries the marker in NO language; a dev buff carries it in
			// at least one. The localized (English) display name is cribbed on later by RdbLdbNameImporter.
			var ldbSpellNames = new Dictionary<uint, List<string>>();
			foreach (var e in rdb.EntriesOfType(LdbType).Where(x => x.IsLocal))
			{
				byte[] page;
				try
				{
					page = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (LdbStringIndex.CategoryOf(page) != LdbStringIndex.SpellNameCategory)
					continue;

				foreach (var entry in LdbStringIndex.Parse(page))
					(ldbSpellNames.TryGetValue(entry.Id, out var names)
						? names
						: ldbSpellNames[entry.Id] = []).Add(entry.Value);
			}

			var byId = (await db.Spells.ToListAsync()).ToDictionary(s => s.Id);
			var toAdd = new List<Spell>();
			// #367: (spellId -> stat deltas) from the [2][9] region, synced into SpellStatEffects after the spells save.
			// Inner map dedups a stat the region lists twice (last == authoritative write wins, as for items).
			var statEffects = new Dictionary<uint, Dictionary<uint, (uint Kind, int Amount)>>();

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
				{
					// #402: no embedded name. Recover it ONLY if the LDB knows it as a real (non-dev) spell name;
					// insert with an empty name and let RdbLdbNameImporter crib the English name on. Otherwise it is
					// asset/aura junk and stays dropped.
					if (!ldbSpellNames.TryGetValue(entry.Id, out var ldbNames) || ldbNames.Any(IsDevMarker))
						continue;

					name = ""; // provisional: rdb-ldb-names fills the localized name (a raw name here would block that)
				}

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
				var rank = fields.TryGetValue(RankField, out var rk) && rk <= byte.MaxValue
					? (byte)rk
					: (byte)0;

				// #367: buff/debuff stat deltas live in the same [2][9] region as item templates. The record's KIND is
				// carried through, not dropped: a spell mixes flat and PERCENT records in one region, and reading a
				// percent one as flat turns "+2% Critical Rating" into "+200" (ItemTemplateStatModifiers).
				var mods = ItemTemplateStatModifiers.Parse(body.AsSpan(12));
				if (mods.Count > 0)
				{
					var deltas = new Dictionary<uint, (uint Kind, int Amount)>();
					foreach (var m in mods)
						deltas[m.StatId] = (m.Kind, (int)m.WireAmount); // last write wins on a repeated stat
					statEffects[entry.Id] = deltas;
				}

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
						Level = level,
						Rank = rank
					};
					byId[entry.Id] = spell;
					toAdd.Add(spell);
					inserted++;
					if (!string.IsNullOrEmpty(name)) // #402: an LDB-recovered row is inserted nameless (filled later)
						namesSet++;
					if (level != 0)
						levelsSet++;
					if (castTime != 0)
						castTimesSet++;
					continue;
				}

				// #402: never clobber an existing (possibly LDB-cribbed) name with the empty provisional name a
				// nameless-record recovery carries.
				if (!string.IsNullOrEmpty(name) && spell.Name != name)
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

				// The RDB leads on level, over both the scrapes and the captures (see LevelField).
				if (level != 0 && spell.Level != level)
				{
					spell.Level = level;
					levelsSet++;
				}

				// Rank is set unconditionally, unlike level: the RDB is its ONLY source, so there is no other value
				// to defer to and a 0 here means the record genuinely carries no rank.
				spell.Rank = rank;
			}

			if (toAdd.Count > 0)
				db.Spells.AddRange(toAdd);
			await db.SaveChangesAsync();

			// #367: sync the SpellStatEffect rows now that every affected spell exists (FK). Delete-then-insert per
			// spell is idempotent and matches the region being authoritative for a spell's stat deltas.
			var affected = statEffects.Keys.ToList();
			var stale = await db.SpellStatEffects.Where(e => affected.Contains(e.SpellId)).ToListAsync();
			if (stale.Count > 0)
				db.SpellStatEffects.RemoveRange(stale);
			var effectRows = 0;
			foreach (var (spellId, deltas) in statEffects)
			foreach (var (statId, effect) in deltas)
			{
				db.SpellStatEffects.Add(new SpellStatEffect
				{
					SpellId = spellId,
					StatId = (CharacterStatId)statId,
					EffectKind = effect.Kind,
					Amount = effect.Amount
				});
				effectRows++;
			}

			await db.SaveChangesAsync();

			logger?.LogInformation(
				"RdbSpellImporter: {Seen} named spell records, {Inserted} new, {NamesSet} names set, {CastTimesSet} cast times set, {StatSpells} spells with {StatRows} stat effects.", seen, inserted,
				namesSet, castTimesSet, statEffects.Count, effectRows);
			return (seen, inserted, namesSet, castTimesSet, levelsSet);
		}

		// #402: the game's dev/PvP-internal naming conventions for the buffs that live in cat 50210 but are NOT
		// player-facing spells - flag/test/spawner/wrapper buffs. Their name begins with an "XX" or "CTF" tag
		// (as a whole token: "XX", "XX_", "XX ", "XX-", "CTF_", ... - not the start of a real word), case-insensitive,
		// or the row is unnamed. A record nameless in its own 1040005 body is recovered only if NONE of its localized
		// names is such a marker.
		private static bool IsDevMarker(string ldbName)
		{
			var t = ldbName.Trim();
			if (t.Length == 0)
				return true;

			return HasTagPrefix(t, "XX") || HasTagPrefix(t, "CTF");

			// The tag is a standalone leading token: <tag> optionally followed by a non-letter (separator), so it
			// matches "XX_Foo"/"XX Foo"/"XX-Foo"/"XX" but never a real name that merely starts with those letters.
			static bool HasTagPrefix(string s, string tag)
			{
				return s.StartsWith(tag, StringComparison.OrdinalIgnoreCase) && (s.Length == tag.Length || !char.IsLetter(s[tag.Length]));
			}
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
					var fid = body.ReadU32Le(j * 4);
					if (fid == 0 || fid > 2000 || (haveLast && fid <= last))
						break;

					cur[fid] = body.ReadU32Le((j + 1) * 4);
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
