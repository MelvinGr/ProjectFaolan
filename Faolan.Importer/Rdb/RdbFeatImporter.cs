using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// Builds the FEAT catalog (Gitea #251) from the client's RDB, with no key crack and no capture.
	//
	// RDB type 1000020 holds items AND feats. An item template is TEA-encrypted and opens with `RDBH`; a PLAINTEXT
	// record opens with its own TYPE word, so ~20.8% of the 98,048 read without a key - and that share is essentially
	// the feats, because the items are the encrypted part. `--report feat-catalog` prints the split.
	//
	// ⚠️ There is no "TB magic", despite how this reads in a hex dump: 0x000F4254 IS 1000020, which happens to render
	// as `TB\x0f\x00`. Feats, spells (1040005) and items share ONE layout -
	//   [u32 type][u32 id][u32 1000000][...u32 fields...][text][stat-modifier region]
	// - which is why the modifier region has one shared parser (ItemTemplateStatModifiers) rather than a reader per
	// subsystem. See docs/subsystems/feats.md.
	public static class RdbFeatImporter
	{
		// Word [44] on a feat record. The item templates that survive in plaintext carry 0x200; every other value in
		// the catalog is a sub-650-row rump of TEST/debug entries.
		public const uint FeatFlags = 0x10000005;

		// One effect element: 8 fixed descriptor words then (StatId, Value).
		private const int EffectElementLength = 40;
		private const uint ListUnit = 0x3F1;

		private const uint TemplateType = 1000020;

		// The client's own feat TABLE: FeatTable_t::LoadStaticData reads this resource and its entries carry the
		// wire-id -> template-id pair (#467).
		private const uint FeatTableId = 1;
		private const uint FeatTableType = 1000042;
		private const int MapEntryLength = 48;

		// Columns 2/3/4 of an entry: up to three prerequisite wire feat ids.
		private const int PrereqSlots = 3;
		private const int MapHeaderLength = 20;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? proxyBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			// Read the client's feat TABLE first: it gates the sweep below. A template the table POINTS AT is a feat by
			// definition, which is a far better test than the Flags heuristic - Hakotep's Hellfire (3187268) carries
			// Flags 0x5, not 0x10000005, and was silently excluded until the id map was used as the authority.
			var (map, prereqs) = ReadIdMapAndPrereqs(rdb);
			var mappedTemplates = map.Select(m => m.Template).ToHashSet();

			var found = new List<Feat>();
			var featEffects = new Dictionary<uint, IReadOnlyList<ItemTemplateStatModifiers.Modifier>>();
			var featSpellMods = new Dictionary<uint, IReadOnlyList<ItemTemplateStatModifiers.Modifier>>();
			var featSpellStatMods = new Dictionary<uint, IReadOnlyList<SpellStatModifier>>();
			var featSpellGrants = new Dictionary<uint, IReadOnlyList<uint>>();
			var skippedUnnamed = 0;
			var misSlicedElements = 0;

			foreach (var e in rdb.EntriesOfType(TemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // a record we cannot read is not a feat we can silently drop a column of - it is skipped whole
				}

				// Either the client's feat table points at it, or it carries the feat Flags value. The union matters:
				// the table covers 6,593 templates (every feat the WIRE can name) while Flags reaches a wider catalog
				// the table does not enumerate.
				if (body.Length < 48 || Word(body, 0) != TemplateType || (Word(body, 44) != FeatFlags && !mappedTemplates.Contains(e.Id)))
					continue;

				var (name, description) = ReadNameAndDescription(body);
				if (name.Length == 0)
				{
					skippedUnnamed++;
					continue;
				}

				var parsed = ParseName(name);
				var effects = ParseEffects(body);
				featEffects[e.Id] = effects;
				featSpellMods[e.Id] = ParseSpellModifiers(body);
				featSpellStatMods[e.Id] = ParseSpellStatModifiers(body, out var misSliced);
				misSlicedElements += misSliced;
				featSpellGrants[e.Id] = ParseSpellGrants(body);
				found.Add(new Feat
				{
					Id = e.Id,
					Name = name,
					Flags = Word(body, 44),
					TemplateCategory = Word(body, 28),
					Tree = parsed.Tree,
					Group = parsed.Group,
					BaseName = parsed.BaseName,
					Rank = parsed.Rank,
					Description = description
				});
			}

			// The wire-id -> template-id map (#467). Delete+reinsert: it is wholly derived from one RDB resource.
			var mapRows = 0;
			var prereqRows = 0;
			if (map.Count > 0)
			{
				// TWO saves, deliberately. FeatPrereq cascades off FeatIdMap, so deleting the old map rows and adding
				// the new ones in ONE change-tracker run makes EF cascade the delete onto the prereq rows we just
				// added - they vanish on save, silently, and the table ends up empty.
				db.FeatPrereqs.RemoveRange(await db.FeatPrereqs.ToListAsync());
				db.FeatIdMaps.RemoveRange(await db.FeatIdMaps.ToListAsync());
				await db.SaveChangesAsync();
				foreach (var (wire, template, baseIndex, rootFlag) in map)
				{
					db.FeatIdMaps.Add(new FeatIdMap
					{
						WireFeatId = wire,
						TemplateId = template,
						BaseIndex = baseIndex,
						Rank = wire % 100, // 0-based; the other half of the split BaseIndex tops
						RootFlag = rootFlag
					});
					mapRows++;
				}

				// Only edges whose OWNER is a mapped feat: the FK points at FeatIdMap, and an orphan edge would fail
				// the insert rather than being quietly useful.
				var mapped = map.Select(m => m.Wire).ToHashSet();
				foreach (var (wire, sequence, requires) in prereqs)
				{
					if (!mapped.Contains(wire))
						continue;

					db.FeatPrereqs.Add(new FeatPrereq
					{
						WireFeatId = wire,
						Sequence = sequence,
						RequiresWireFeatId = requires
					});
					prereqRows++;
				}
			}

			var existing = await db.Feats.ToDictionaryAsync(f => f.Id);
			var added = 0;
			var updated = 0;
			foreach (var feat in found)
			{
				if (!existing.TryGetValue(feat.Id, out var row))
				{
					db.Feats.Add(feat);
					added++;
					continue;
				}

				if (row.Name == feat.Name && row.Description == feat.Description && row.Tree == feat.Tree && row.Group == feat.Group && row.BaseName == feat.BaseName
					&& row.Rank == feat.Rank && row.Flags == feat.Flags && row.TemplateCategory == feat.TemplateCategory)
					continue;

				row.Name = feat.Name;
				row.Description = feat.Description;
				row.Tree = feat.Tree;
				row.Group = feat.Group;
				row.BaseName = feat.BaseName;
				row.Rank = feat.Rank;
				row.Flags = feat.Flags;
				row.TemplateCategory = feat.TemplateCategory;
				updated++;
			}

			// The SPELL modifiers (#590), the same delete+reinsert and for the same reason. Written before the stat
			// effects only because both scope their delete to every feat PARSED, and doing them together keeps that
			// one rule visible in one place.
			var spellModRows = 0;
			var spellTouched = featSpellMods.Keys.ToHashSet();
			db.FeatSpellModifiers.RemoveRange(await db.FeatSpellModifiers.Where(m => spellTouched.Contains(m.FeatId)).ToListAsync());
			foreach (var (featId, list) in featSpellMods.Where(kv => kv.Value.Count > 0))
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatSpellModifiers.Add(new FeatSpellModifier
					{
						FeatId = featId,
						Sequence = i,
						SpellLineId = list[i].StatId,
						PercentHundredths = (int)list[i].WireAmount,
						EffectKind = list[i].Kind
					});
					spellModRows++;
				}
			}

			// The spell-scoped STAT modifiers and the spell GRANTS (#593), on the same delete+reinsert rule and scoped
			// the same way - to every feat PARSED, not just those that still yield rows.
			var spellStatModRows = 0;
			db.FeatSpellStatModifiers.RemoveRange(await db.FeatSpellStatModifiers.Where(m => spellTouched.Contains(m.FeatId)).ToListAsync());
			foreach (var (featId, list) in featSpellStatMods.Where(kv => kv.Value.Count > 0))
			{
				for (var i = 0; i < list.Count; i++)
				{
					var isLine = list[i].Kind == SpellLineStatModifierKind;
					db.FeatSpellStatModifiers.Add(new FeatSpellStatModifier
					{
						FeatId = featId,
						Sequence = i,
						SpellId = isLine ? null : list[i].TargetId,
						SpellLineId = isLine ? list[i].TargetId : null,
						StatId = list[i].Stat,
						Value = list[i].Value,
						PercentHundredths = list[i].PercentHundredths,
						EffectKind = list[i].Kind
					});
					spellStatModRows++;
				}
			}

			var spellGrantRows = 0;
			db.FeatSpellGrants.RemoveRange(await db.FeatSpellGrants.Where(g => spellTouched.Contains(g.FeatId)).ToListAsync());
			foreach (var (featId, list) in featSpellGrants.Where(kv => kv.Value.Count > 0))
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatSpellGrants.Add(new FeatSpellGrant { FeatId = featId, Sequence = i, SpellId = list[i] });
					spellGrantRows++;
				}
			}

			// Effects are delete+reinsert per feat: the list is wholly derived from the record, so reconciling element
			// by element would only risk leaving a stale tail behind.
			var effectRows = 0;
			var wanted = featEffects.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);

			// Scoped to every feat PARSED, not just those that still yield effects. Keying the delete off the results
			// means a feat whose list becomes empty - because the parse got stricter - keeps its old rows for ever, so
			// a change that REMOVES bad data would leave the bad data sitting there.
			var touched = featEffects.Keys.ToHashSet();
			var stale = await db.FeatEffects.Where(fe => touched.Contains(fe.FeatId)).ToListAsync();
			db.FeatEffects.RemoveRange(stale);
			foreach (var (featId, list) in wanted)
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatEffects.Add(new FeatEffect
					{
						FeatId = featId,
						Sequence = i,
						StatId = (CharacterStatId)list[i].StatId,
						EffectKind = list[i].Kind,
						Value = list[i].WireAmount
					});
					effectRows++;
				}
			}

			if (added > 0 || updated > 0 || effectRows > 0 || stale.Count > 0 || mapRows > 0 || prereqRows > 0 || spellStatModRows > 0 || spellGrantRows > 0)
				await db.SaveChangesAsync();

			// Never silent: a mis-sliced element is data we chose NOT to import, and the count is the only place that
			// says so - see the type-id guard in ParseSpellStatModifiers (Gitea #595).
			if (misSlicedElements > 0)
				logger?.LogWarning("RdbFeatImporter: {MisSliced} spell stat-modifier elements rejected as mis-sliced (a slot held a GameAction type id) - see Gitea #595.", misSlicedElements);

			logger?.LogInformation(
				"RdbFeatImporter: {Found} feat templates ({Unnamed} unnamed, skipped); {Added} added, {Updated} updated, {Effects} effects, {Map} id-map rows, {Prereq} prereq edges, {SpellMods} spell modifiers, {SpellStats} spell stat modifiers, {Grants} spell grants.",
				found.Count, skippedUnnamed, added, updated, effectRows, mapRows, prereqRows, spellModRows, spellStatModRows, spellGrantRows);

			return new Result(found.Count, added, updated, skippedUnnamed, effectRows, mapRows, prereqRows, spellModRows, spellStatModRows, spellGrantRows);
		}

		// Splits a catalog name into its tree / group / base / rank. The client encodes all four in the string:
		//   "Soldier: Optional Training: Thrown Weapons 5" -> (Soldier, Optional Training, Thrown Weapons, 5)
		//   "Core Training: Poison Magic Rating 6"         -> (Core Training, null, Poison Magic Rating, 6)
		//   "Guardian: Optional Training: 2HE (3)"         -> (Guardian, Optional Training, 2HE, 3)
		//   "Light Blade Training 1"                       -> (null, null, Light Blade Training, 1)
		// A name that fits none of it keeps BaseName = the whole name and Rank = 0, so nothing is lost to a parse that
		// did not apply.
		public static Parsed ParseName(string name)
		{
			var parts = name.Split(':', StringSplitOptions.TrimEntries).Where(p => p.Length > 0).ToArray();
			var tail = parts.Length > 0 ? parts[^1] : name.Trim();

			string? tree = null;
			string? group = null;
			if (parts.Length >= 2)
				tree = parts[0];
			if (parts.Length >= 3)
				group = parts[1];

			// The rank is a trailing integer, bare ("... 5") or parenthesised ("... (3)").
			var rank = 0;
			var m = RankSuffix.Match(tail);
			if (m.Success)
			{
				rank = int.Parse(m.Groups["n"].Value);
				tail = tail[..m.Index].TrimEnd();
			}

			return new Parsed(tree, group, tail.Length > 0 ? tail : name.Trim(), rank);
		}

		private static readonly Regex RankSuffix = new(@"\s+(\((?<n>\d{1,3})\)|(?<n>\d{1,3}))$", RegexOptions.Compiled);

		// The record's NAME and DESCRIPTION. TWO shapes exist, and knowing only the first is what left 3,633 plaintext
		// records looking nameless - including every feat the three captured characters hold:
		//
		//   u32 form:  [u32 len][name]                                - a name alone
		//   u16 form:  [u16 nameLen][u16 descLen][name][description]
		//
		// Template 3187268 (the Hellfire hakotep unlocks on tape) is the u16 form: `08 00 4B 00 "Hellfire" "Reduces
		// the casting time of Hellfire Breath, but increases the re-use time."`. Both are scanned and whichever
		// VALIDATES wins; the u16 form is tried first because a length PAIR landing two printable runs back to back is
		// a far stronger signature than one u32 that merely looks plausible.
		//
		// Returns ("", "") when neither fits, so an unreadable record is skipped rather than guessed at.
		public static (string Name, string Description) ReadNameAndDescription(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i++)
			{
				var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i));
				var descLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i + 2));
				if (nameLen is < 2 or > 96 || descLen is < 1 or > 1024 || i + 4 + nameLen + descLen > body.Length)
					continue;

				if (!IsPrintable(body, i + 4, nameLen) || !IsPrintable(body, i + 4 + nameLen, descLen))
					continue;

				return (DecodeText(body, i + 4, nameLen), DecodeText(body, i + 4 + nameLen, descLen));
			}

			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length || !IsPrintable(body, i + 4, (int)len))
					continue;

				return (DecodeText(body, i + 4, (int)len), "");
			}

			return ("", "");
		}

		// LATIN-1 printable, not ASCII. The descriptions carry CP1252 typography - template 3186541 ("Demonic Leech")
		// has a 0x92 curly apostrophe in "The herald's Body and Spirit Wrack combo ..." - and capping at 0x7E rejected
		// the whole record, which is how six of the captured characters' feats stayed missing after the u16 shape was
		// understood. Wire strings in this client are Latin-1 everywhere else too.
		// The strings are CP1252, not Latin-1: the descriptions use typographic punctuation, and 0x91-0x94 / 0x96-0x97 /
		// 0x85 land in Latin-1's C1 CONTROL block rather than on the quotes and dashes they mean. Decoding as Latin-1
		// silently drops them - "the necromancer's Pestilential Blast" comes out as "the necromancers Pestilential
		// Blast". Only that 0x80-0x9F window differs between the two encodings, so it is mapped explicitly rather than
		// by pulling in the CodePages provider for one range.
		private static string DecodeText(byte[] body, int start, int length)
		{
			var chars = new char[length];
			for (var i = 0; i < length; i++)
			{
				var b = body[start + i];
				chars[i] = b switch
				{
					0x82 => '\u201A', 0x84 => '\u201E', 0x85 => '\u2026', 0x8B => '\u2039',
					0x91 => '\u2018', 0x92 => '\u2019', 0x93 => '\u201C', 0x94 => '\u201D',
					0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014', 0x9B => '\u203A',
					_ => (char)b
				};
			}

			return new string(chars);
		}

		private static bool IsPrintable(byte[] body, int start, int length)
		{
			for (var i = start; i < start + length; i++)
			{
				if (body[i] is < 0x20 or 0x7F)
					return false;
			}

			return true;
		}

		// Reads the wire-id -> template-id map out of RDB 1000042 id 1 - the resource FeatTable_t::LoadStaticData
		// loads. Header [u32 type][u32 id][u32 1][u32 count][u32 version], then `count` 48-byte entries whose first
		// two words are (wireId, templateId) and whose 11th is the base index.
		public static (List<(uint Wire, uint Template, uint BaseIndex, uint RootFlag)> Map, List<(uint Wire, int Sequence, uint Requires)> Prereqs)
			ReadIdMapAndPrereqs(RdbReader rdb)
		{
			var map = new List<(uint, uint, uint, uint)>();
			var prereqs = new List<(uint, int, uint)>();
			var matches = rdb.EntriesOfType(FeatTableType).Where(e => e.Id == FeatTableId).ToList();
			if (matches.Count == 0)
				return (map, prereqs);

			var entry = matches[0];
			byte[] body;
			try
			{
				body = rdb.ReadDecompressed(entry);
			}
			catch
			{
				return (map, prereqs);
			}

			return (ParseIdMap(body), ParsePrereqs(body));
		}

		// Split out from the RDB read so it can be tested against bytes without the client install.
		public static List<(uint Wire, uint Template, uint BaseIndex, uint RootFlag)> ParseIdMap(byte[] body)
		{
			var map = new List<(uint, uint, uint, uint)>();
			if (body.Length < MapHeaderLength || Word(body, 0) != FeatTableType)
				return map;

			var count = (int)Word(body, 12);
			if (count <= 0 || MapHeaderLength + ((long)count * MapEntryLength) > body.Length)
				return map;

			for (var i = 0; i < count; i++)
			{
				var at = MapHeaderLength + (i * MapEntryLength);
				var wire = Word(body, at);
				var template = Word(body, at + 4);
				if (wire == 0 || template == 0)
					continue; // a blank slot, not a mapping - carrying it would put a 0 -> 0 row in the table

				map.Add((wire, template, Word(body, at + 40), Word(body, at + 32)));
			}

			return map;
		}

		// The PREREQUISITE edges of the same entries: columns 2/3/4 (offsets +8/+12/+16) are wire feat ids a feat
		// depends on, zero where unused. Returned in column order so the rows keep the client's ordering.
		public static List<(uint Wire, int Sequence, uint Requires)> ParsePrereqs(byte[] body)
		{
			var edges = new List<(uint, int, uint)>();
			if (body.Length < MapHeaderLength || Word(body, 0) != FeatTableType)
				return edges;

			var count = (int)Word(body, 12);
			if (count <= 0 || MapHeaderLength + ((long)count * MapEntryLength) > body.Length)
				return edges;

			for (var i = 0; i < count; i++)
			{
				var at = MapHeaderLength + (i * MapEntryLength);
				var wire = Word(body, at);
				if (wire == 0)
					continue;

				for (var slot = 0; slot < PrereqSlots; slot++)
				{
					var requires = Word(body, at + 8 + (slot * 4));
					if (requires != 0)
						edges.Add((wire, slot, requires));
				}
			}

			return edges;
		}

		// The record's TAIL, after the name: [u32 2][u32 14][u32 ListFrame(n)] then n x 40-byte effect elements whose
		// last two words are (StatId, Value). The eight leading words of an element are a fixed descriptor.
		// Returns an empty list when the tail is not this shape - some records carry no effect block at all.
		// The feat's stat modifiers, via the SHARED template-region parser.
		//
		// Feats, spells and items are the SAME record layout - `[u32 type][u32 id][u32 1000000][...]` - so they share
		// this region and its parser (#367 already uses it for spells). Hand-rolling a second reader here is what
		// produced five successive regressions: a fixed stride, then a kind-range delimiter, then a measured
		// per-kind width table all mis-walked the list, because the records are NOT uniformly strided. Parse anchors
		// on the 6-word signature `4,0,1,0,2,9` inside the 10-word record instead, which is stride-independent.
		//
		// It also carries the KIND, which the hand-rolled version dropped: a 53067 record is a PERCENT modifier, so
		// reading it as flat turns "+2% Critical Rating" into "+200".
		//
		// ⚠️ `ParseStatModifiers`, not `Parse` (Gitea #597). `Parse` is the POSITIONAL pair view - payload words 0 and
		// 1, whatever they mean - and (stat, value) is only where those words sit on `Modify`. Of the 29 kinds the
		// feat catalog carries, five put a stat in word 0 and a magnitude in word 1; the rest were imported as
		// nonsense, the worst of them `DisplayLearnAbility` (Line, 187), whose 110 rows read as `Health += 4232751`
		// because word 0 happened to be 1. `--report feat-effect-kinds` prints the census the gate is built on.
		// The excluded ones are not lost: the spell- and line-scoped kinds have their own tables (#590/#593), and
		// what remains is undecoded rather than dropped.
		public static IReadOnlyList<ItemTemplateStatModifiers.Modifier> ParseEffects(byte[] body)
		{
			return body.Length <= 12
				? []
				: [.. ItemTemplateStatModifiers.ParseStatModifiers(body.AsSpan(12)).Where(m => m.Kind != TagRecordKind)];
		}

		// The feat's SPELL modifiers (#590): the records the stat parser REJECTS, because their payload's first word
		// is a spell-LINE id rather than a stat id and so fails its `statId < 0x1000` bound. `Decomposition` rank 5
		// carries (41000, 3500) - Necromancer line 0 (Flesh to Worms), +35.00% - which is exactly what the client's
		// own tooltip draws.
		//
		// ⚠️ Kind 53075 ONLY. The catalog has ~9 more kinds in this same shape (53209 alone is 3,235 records), and the
		// #591 sweep decoded WHAT EACH ONE POINTS AT without decoding what its payload MEANS. That distinction is the
		// whole reason they stay out: a reference space is checkable (resolve the ref and see whether it hits), a
		// magnitude is not, and importing a value as a percent because it sits where 53075's percent sits is exactly
		// the invention this gate exists to prevent - on a third of the feat catalog.
		//
		// The ref spaces, each resolved against the shipped catalogs and corroborated by the feat's own LDB tooltip:
		//
		//   53209  x3235  a COMBO id (Combos.Id). `Dread Lord` - "Reduces the reuse time of all combos" - carries six,
		//                 every one a DarkTemplar_NN_lifetapbuff combo. value is 0 throughout, so the record is a pure
		//                 reference: a LIST of what the feat applies to, with the magnitude somewhere else.
		//   53062   x475  a spell LINE id (Spell.LineId), the SAME space 53075 uses. `Brutal Maiming` - "Increases the
		//                 chance of the Disable combo inflicting critical damage" - points at 20410, which is the
		//                 Guardian's `Disable` line, by name. Its value (147) is NOT a percent and is undecoded.
		//   53019  x1010  a spell TEMPLATE id (Spell.Id), and a GRANT rather than a modifier: `Spell Unlocking 60-80`
		//                 points at Drain Essence ranks 1-4, Blood Pact and Covenant of Vengeance. Its `value` repeats
		//                 its own KIND (53019), i.e. a sentinel, not a magnitude.
		//   53212   x421  a spell TEMPLATE id, also a grant - and the feat is NAMED for the spell (`Touch of Death`
		//                 -> the spell `Touch of Death`). value 0 throughout.
		//   53054   x593  a spell TEMPLATE id (`Fiendish Hand` -> `Dark Hand`, `Combo - Skewer` -> `Skewered`), but
		//                 with a varying value and SEVERAL records per (feat, spell) - three on Fiendish Hand carrying
		//                 347/348/349. Undecoded.
		//   53030   x962  not a player feat at all - the `Dimach Item k1/k2/k3` crafting entries, all pointing at one
		//                 constant ref (43464) with value 100.
		//
		// So three of the six are GRANTS, not modifiers, which is a second reason the shape is not enough: importing
		// them here would file a spell the feat gives you as a boost to a spell you already have. Survey them with
		// `--report feat-spell-modifiers`, which prints each kind with its feat name and the LDB description.
		public static IReadOnlyList<ItemTemplateStatModifiers.Modifier> ParseSpellModifiers(byte[] body)
		{
			return body.Length <= 12
				? []
				: [.. ItemTemplateStatModifiers.ParseRejected(body.AsSpan(12)).Where(m => m.Kind == SpellModifierKind)];
		}

		// The one effect kind whose (reference, magnitude) payload is decoded: a spell line and a percent x 100.
		private const uint SpellModifierKind = 53075;

		// The feat's spell-scoped STAT modifiers (#593): kinds 53054 `SpellStatModifier` and 53062
		// `SpellLineStatModifier`, the same four-slot payload with the target space swapped.
		//
		// Read BY KEY, never by position - `(Spell|Line, Stat, Value, Amount)` - which is the whole reason these were
		// undecodable while the pair readers were the only view: `SpellStatModifier` puts the SPELL first and the STAT
		// second, so its "magnitude that varied for no reason" was a stat id (#591).
		//
		// `Value` and `Amount` are ALTERNATIVES, not a pair, and the client's own tooltip builder is the evidence: at
		// 014a2714 it renders the `Value` slot through the generic stat formatter (which appends the STAT'S unit
		// suffix) and the `Amount` slot through a copy that appends a hardcoded `"%"` - the literal at 0x01fbcf18, on
		// all three of that branch's print paths. Exactly one of the two is set on all 1068 shipped records, so the
		// pair is stored as two NULLABLE columns rather than a value plus a mode flag.
		public static IReadOnlyList<SpellStatModifier> ParseSpellStatModifiers(byte[] body)
		{
			return ParseSpellStatModifiers(body, out _);
		}

		/// <summary>
		///     As <see cref="ParseSpellStatModifiers(byte[])" />, reporting how many elements were rejected as
		///     mis-sliced so the importer can say so rather than dropping them silently.
		/// </summary>
		public static IReadOnlyList<SpellStatModifier> ParseSpellStatModifiers(byte[] body, out int misSliced)
		{
			misSliced = 0;
			if (body.Length <= 12)
				return [];

			var mods = new List<SpellStatModifier>();
			foreach (var e in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
			{
				if (e.Kind != SpellStatModifierKind && e.Kind != SpellLineStatModifierKind)
					continue;

				// Every slot must be present: a key the type declares but the element does not carry means the element
				// was mis-sliced, and half a modifier is worse than none.
				var target = e.Value(e.Kind == SpellStatModifierKind ? GameActionTypes.KeySpell : GameActionTypes.KeyLine);
				var stat = e.Value(GameActionTypes.KeyStat);
				var value = e.Value(GameActionTypes.KeyValue);
				var amount = e.Value(GameActionTypes.KeyAmount);
				if (target is null or 0 || stat is null or 0 || value is null || amount is null)
					continue;

				// Both set, or neither, is a shape this decode does not cover - drop it rather than guess which wins.
				if (value == 0 == (amount == 0))
					continue;

				// A magnitude that is itself a registered GameAction TYPE id is the next element's opening word, read
				// as ours because this element was narrower than its key list says. `Swift Pathing` (5031395..5031401)
				// is the case in the shipped catalog: its two 53054 elements tile the record end to end at TEN words
				// each, so the `Value` slot lands on the following element's `0xCF3E`. Rejecting on the type-id range
				// is not a special case for that feat - a spell id, a stat id and a magnitude all live nowhere near
				// 0xCF09..0xD026, so a slot that does was never ours. Open as Gitea #595.
				if (GameActionTypes.Get(value.Value) != null || GameActionTypes.Get(amount.Value) != null)
				{
					misSliced++;
					continue;
				}

				mods.Add(new SpellStatModifier(e.Kind, target.Value, (CharacterStatId)stat.Value,
					value == 0 ? null : (int)value.Value, amount == 0 ? null : (int)amount.Value));
			}

			return mods;
		}

		// The spells the feat GRANTS (#593): kind 53019 `Spell`, a one-slot element carrying a spell TEMPLATE id.
		// A grant, not a modifier - see FeatSpellGrant.
		public static IReadOnlyList<uint> ParseSpellGrants(byte[] body)
		{
			return body.Length <= 12
				? []
				: [.. ItemTemplateStatModifiers.ParseElements(body.AsSpan(12))
					.Where(e => e.Kind == SpellGrantKind)
					.Select(e => e.Value(GameActionTypes.KeyValue) ?? 0)
					.Where(id => id != 0)];
		}

		private const uint SpellStatModifierKind = 53054;
		private const uint SpellLineStatModifierKind = 53062;
		private const uint SpellGrantKind = 53019;

		/// <summary>
		///     One parsed kind-53054/53062 element. <paramref name="TargetId" /> is a <c>Spell.Id</c> under 53054 and a
		///     <c>Spell.LineId</c> under 53062 - the kind is what says which.
		/// </summary>
		public readonly record struct SpellStatModifier(uint Kind, uint TargetId, CharacterStatId Stat, int? Value, int? PercentHundredths);

		// Kind 53182 is NOT a stat modifier and must not be imported as one. Its element has the same signature, so
		// the shared parser returns it, but the payload is (index, FOURCC) rather than (statId, amount): the "amount"
		// decodes to four ASCII characters, identical across a whole feat family - 0x4450434E "DPCN" on every
		// Perception feat, 0x5633534D "V3SM" on every Hide In Shadows, 0x454F4854 "EOHT" on the crafting ranks - and
		// the "statId" is a small index (12, 13, 14, 100, 102, 104, 255).
		//
		// It was 1625 of 24946 rows and 480 of the 561 that landed on stats the client cannot name - 86% of all the
		// noise. The other 1145 looked fine only because a small index happens to be a valid stat id; they are just as
		// bogus, which is why the whole KIND is dropped rather than the rows that failed a stat lookup.
		private const uint TagRecordKind = 53182;

		// The effect block's start (past the [2][14][frame] header) and its element COUNT, or (0, 0) when the record
		// carries no effect list. Exposed so `--report feat-effect-widths` measures exactly the bytes the importer
		// walks, rather than a second opinion about where the list begins.
		public static (int Start, int Count) EffectTail(byte[] body)
		{
			var tail = TailStart(body);
			if (tail + 12 > body.Length)
				return (0, 0);

			var frame = Word(body, tail + 8);
			if (frame == 0 || frame % ListUnit != 0)
				return (0, 0);

			var count = (int)(frame / ListUnit) - 1;
			return count <= 0 ? (0, 0) : (tail + 12, count);
		}

		// Where the tail begins: just past the u32-length-prefixed name.
		private static int TailStart(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length)
					continue;

				var printable = true;
				foreach (var b in body.AsSpan(i + 4, (int)len))
				{
					if (b is < 0x20 or > 0x7E)
					{
						printable = false;
						break;
					}
				}

				if (printable)
					return i + 4 + (int)len;
			}

			return body.Length;
		}

		private static uint Word(byte[] body, int offset)
		{
			return offset + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) : 0;
		}

		public readonly record struct Parsed(string? Tree, string? Group, string BaseName, int Rank);

		public readonly record struct Result(int Found, int Added, int Updated, int Unnamed, int Effects, int MapRows, int PrereqEdges, int SpellModifiers, int SpellStatModifiers,
			int SpellGrants);
	}
}
