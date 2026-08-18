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

		// The client's FEATCLASS table (#862): `FeatClassTable_c` loads this resource and reports
		// "Could not load featclass info" when it cannot. Same 20-byte header as the id map, then `count` 40-byte
		// entries of ten words - see ParseClassEntries for the columns.
		private const uint FeatClassType = 1000043;
		private const uint FeatClassTableId = 1;
		private const int ClassEntryLength = 40;
		private const int ClassEntryWords = 10;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? proxyBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			// Read the client's feat TABLE first: it gates the sweep below. A template the table POINTS AT is a feat by
			// definition, which is a far better test than the Flags heuristic - Hakotep's Hellfire (3187268) carries
			// Flags 0x5, not 0x10000005, and was silently excluded until the id map was used as the authority.
			var (map, prereqs) = ReadIdMapAndPrereqs(rdb);
			var mappedTemplates = map.Select(m => m.Template).ToHashSet();
			var classEntries = ReadClassEntries(rdb);

			var found = new List<Feat>();
			var featEffects = new Dictionary<uint, IReadOnlyList<ItemTemplateStatModifiers.Modifier>>();
			var featSpellMods = new Dictionary<uint, IReadOnlyList<SpellHitPercentModifier>>();
			var featSpellStatMods = new Dictionary<uint, IReadOnlyList<ItemTemplateStatModifiers.SpellStatModifier>>();
			var featSpellGrants = new Dictionary<uint, IReadOnlyList<uint>>();
			var featComboMods = new Dictionary<uint, IReadOnlyList<ComboModifier>>();
			var featComboAttackMods = new Dictionary<uint, IReadOnlyList<ComboAttackModifier>>();
			var featProcs = new Dictionary<uint, IReadOnlyList<Proc>>();
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
				featSpellStatMods[e.Id] = ItemTemplateStatModifiers.ParseSpellStatModifiers(body, out var misSliced);
				misSlicedElements += misSliced;
				featSpellGrants[e.Id] = ParseSpellGrants(body);
				featComboMods[e.Id] = ParseComboModifiers(body);
				featComboAttackMods[e.Id] = ParseComboAttackModifiers(body);
				featProcs[e.Id] = ParseProcs(body);
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

			// The client's featclass table (#862). Delete+reinsert like the id map: it too is wholly derived from one
			// RDB resource, and it is keyed by the client's own row id so a re-import renumbers nothing. No FK to
			// FeatIdMap, so it needs no ordering against the block above.
			var classRows = 0;
			if (classEntries.Count > 0)
			{
				db.FeatClassEntries.RemoveRange(await db.FeatClassEntries.ToListAsync());
				await db.SaveChangesAsync();
				foreach (var entry in classEntries)
				{
					db.FeatClassEntries.Add(entry);
					classRows++;
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
					// The KIND picks the target column - a line under 53075, a single spell under 53030 - exactly as it
					// does on FeatSpellStatModifier. Never both.
					var isLine = list[i].Kind == LineHitPercentKind;
					db.FeatSpellModifiers.Add(new FeatSpellModifier
					{
						FeatId = featId,
						Sequence = i,
						SpellLineId = isLine ? list[i].TargetId : null,
						SpellId = isLine ? null : list[i].TargetId,
						PercentHundredths = list[i].PercentHundredths,
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
					var isLine = list[i].Kind == ItemTemplateStatModifiers.SpellLineStatModifierKind;
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
			// The combo upgrades (#593), on the same delete+reinsert rule and the same PARSED scope as the three above.
			var comboModRows = 0;
			db.FeatComboModifiers.RemoveRange(await db.FeatComboModifiers.Where(m => spellTouched.Contains(m.FeatId)).ToListAsync());
			foreach (var (featId, list) in featComboMods.Where(kv => kv.Value.Count > 0))
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatComboModifiers.Add(new FeatComboModifier
					{
						FeatId = featId,
						Sequence = i,
						ComboId = list[i].ComboId,
						DamagePercentHundredths = list[i].DamagePercentHundredths,
						CooldownPercentHundredths = list[i].CooldownPercentHundredths,
						EffectKind = list[i].Kind
					});
					comboModRows++;
				}
			}

			// The per-ATTACK combo modifiers (#593), same rule and same scope again.
			var comboAttackModRows = 0;
			db.FeatComboAttackModifiers.RemoveRange(await db.FeatComboAttackModifiers.Where(m => spellTouched.Contains(m.FeatId)).ToListAsync());
			foreach (var (featId, list) in featComboAttackMods.Where(kv => kv.Value.Count > 0))
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatComboAttackModifiers.Add(new FeatComboAttackModifier
					{
						FeatId = featId,
						Sequence = i,
						ComboId = list[i].ComboId,
						AttackSlot = list[i].Slot,
						DpsPercent = list[i].DpsPercent,
						CriticalChanceRating = list[i].CriticalChanceRating,
						HatePercent = list[i].HatePercent,
						DamageTypeId = list[i].DamageTypeId,
						EffectKind = list[i].Kind
					});
					comboAttackModRows++;
				}
			}

			// The PROCS (#593), same rule and same scope again.
			var procRows = 0;
			db.FeatProcs.RemoveRange(await db.FeatProcs.Where(m => spellTouched.Contains(m.FeatId)).ToListAsync());
			foreach (var (featId, list) in featProcs.Where(kv => kv.Value.Count > 0))
			{
				for (var i = 0; i < list.Count; i++)
				{
					db.FeatProcs.Add(new FeatProc
					{
						FeatId = featId,
						Sequence = i,
						ComboId = list[i].ComboId,
						SpellLineId = list[i].SpellLineId,
						SelfSpellId = list[i].SelfSpellId,
						TargetSpellId = list[i].TargetSpellId,
						Trigger = list[i].Trigger,
						Amount = list[i].Amount,
						EffectKind = list[i].Kind
					});
					procRows++;
				}
			}

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
					// The ramp is null on every kind but the interpolated family, so these three columns are null on
					// all but ~340 of the 23k rows (#599).
					var ramp = list[i].Ramp;
					db.FeatEffects.Add(new FeatEffect
					{
						FeatId = featId,
						Sequence = i,
						StatId = (CharacterStatId)list[i].StatId,
						EffectKind = list[i].Kind,
						Value = list[i].WireAmount,
						HighValue = ramp?.HighValue,
						LowLevel = ramp?.LowLevel,
						HighLevel = ramp?.HighLevel
					});
					effectRows++;
				}
			}

			if (added > 0 || updated > 0 || effectRows > 0 || stale.Count > 0 || mapRows > 0 || prereqRows > 0 || spellStatModRows > 0 || spellGrantRows > 0 || comboModRows > 0 || comboAttackModRows > 0 || procRows > 0 || classRows > 0)
				await db.SaveChangesAsync();

			// Never silent: a mis-sliced element is data we chose NOT to import, and the count is the only place that
			// says so - see the type-id guard in ParseSpellStatModifiers (Gitea #595).
			if (misSlicedElements > 0)
				logger?.LogWarning("RdbFeatImporter: {MisSliced} spell stat-modifier elements rejected as mis-sliced (a slot held a GameAction type id) - see Gitea #595.", misSlicedElements);

			logger?.LogInformation(
				"RdbFeatImporter: {Found} feat templates ({Unnamed} unnamed, skipped); {Added} added, {Updated} updated, {Effects} effects, {Map} id-map rows, {Prereq} prereq edges, {SpellMods} spell modifiers, {SpellStats} spell stat modifiers, {Grants} spell grants, {ComboMods} combo upgrades, {ComboAttackMods} combo-attack modifiers, {Procs} procs, {ClassRows} featclass rows.",
				found.Count, skippedUnnamed, added, updated, effectRows, mapRows, prereqRows, spellModRows, spellStatModRows, spellGrantRows, comboModRows, comboAttackModRows, procRows, classRows);

			return new Result(found.Count, added, updated, skippedUnnamed, effectRows, mapRows, prereqRows, spellModRows, spellStatModRows, spellGrantRows, comboModRows, comboAttackModRows, procRows, classRows);
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

		private static readonly Regex RankSuffix = new Regex(@"\s+(\((?<n>\d{1,3})\)|(?<n>\d{1,3}))$", RegexOptions.Compiled);

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

		// The client's FEATCLASS table (RDB 1000043 id 1) - what a class may train, and what the client trains for
		// itself (#862). Read here rather than in its own importer because it shares this one's RdbReader and is the
		// same subsystem's data.
		private static List<FeatClassEntry> ReadClassEntries(RdbReader rdb)
		{
			var matches = rdb.EntriesOfType(FeatClassType).Where(e => e.Id == FeatClassTableId).ToList();
			if (matches.Count == 0)
				return [];

			try
			{
				return ParseClassEntries(rdb.ReadDecompressed(matches[0]));
			}
			catch
			{
				return [];
			}
		}

		/// <summary>
		///     Parses the featclass table's body. Split out from the RDB read so it can be tested against bytes
		///     without the client install, exactly like <see cref="ParseIdMap" />.
		/// </summary>
		/// <remarks>
		///     `[u32 type][u32 id][u32 1][u32 count][u32 version]` then `count` 40-byte entries of ten words:
		///     <c>[rowId][class][level][wireFeatId][autotrain][treeId][column][flag][tier][pointsRequired]</c>. The
		///     class word carries the <see cref="ConanClass" /> value directly - all twelve appear - and 0 for the
		///     188 rows that name no class.
		///     <para>
		///         The client's own parser (<c>FUN_0078e2b0</c>) accepts version 2 or 3 and checks the 40-byte stride
		///         on version 2; the shipped table is version 3. Word 4 is the one that matters - it is what
		///         <c>FUN_0078cdc0</c> tests before calling the autotrain walk.
		///     </para>
		/// </remarks>
		public static List<FeatClassEntry> ParseClassEntries(byte[] body)
		{
			var entries = new List<FeatClassEntry>();
			if (body.Length < MapHeaderLength || Word(body, 0) != FeatClassType)
				return entries;

			var count = (int)Word(body, 12);
			if (count <= 0 || MapHeaderLength + ((long)count * ClassEntryLength) > body.Length)
				return entries;

			for (var i = 0; i < count; i++)
			{
				var at = MapHeaderLength + (i * ClassEntryLength);
				var w = new uint[ClassEntryWords];
				for (var c = 0; c < ClassEntryWords; c++)
					w[c] = Word(body, at + (c * 4));

				if (w[0] == 0 || w[3] == 0)
					continue; // a blank slot, not an entry - carrying it would put a row naming no feat in the table

				entries.Add(new FeatClassEntry
				{
					Id = w[0],
					Class = w[1] == 0 ? null : (ConanClass)w[1],
					Level = w[2],
					FeatId = w[3],
					Autotrain = w[4] != 0,
					TreeId = w[5],
					TreeColumn = w[6],
					Flag = w[7],
					Tier = w[8],
					TreePointsRequired = w[9]
				});
			}

			return entries;
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
		// ⚠️ Kind 53075 ONLY. The catalog has ~9 more kinds in this same shape (53209 alone is 3,235 records, and has
		// its own parser below), and the
		// #591 sweep decoded WHAT EACH ONE POINTS AT without decoding what its payload MEANS. That distinction is the
		// whole reason they stay out: a reference space is checkable (resolve the ref and see whether it hits), a
		// magnitude is not, and importing a value as a percent because it sits where 53075's percent sits is exactly
		// the invention this gate exists to prevent - on a third of the feat catalog.
		//
		// The ref spaces, each resolved against the shipped catalogs and corroborated by the feat's own LDB tooltip:
		//
		//   53209  x3235  a COMBO id (Combos.Id). `Dread Lord` - "Reduces the reuse time of all combos" - carries six,
		//                 every one a DarkTemplar_NN_lifetapbuff combo. Its payload is DECODED and imported now, as
		//                 FeatComboModifier - see ParseComboModifiers. The "value 0 throughout" that made it look like
		//                 a pure reference was key 66 on a cooldown-only feat; the magnitude was in key 67, which the
		//                 pair view cannot reach.
		//   53062   x475  a spell LINE id (Spell.GuiStackingLine), the SAME space 53075 uses. `Brutal Maiming` - "Increases the
		//                 chance of the Disable combo inflicting critical damage" - points at 20410, which is the
		//                 Guardian's `Disable` line, by name. Its value (147) is NOT a percent and is undecoded.
		//   53019  x1010  a spell TEMPLATE id (Spell.Id), and a GRANT rather than a modifier: `Spell Unlocking 60-80`
		//                 points at Drain Essence ranks 1-4, Blood Pact and Covenant of Vengeance. Its `value` repeats
		//                 its own KIND (53019), i.e. a sentinel, not a magnitude.
		//   53212   x421  a PROC: the pair view read its `Spell` slot (key 88) and called it a grant because the feat
		//                 is named for that spell (`Touch of Death` -> the spell `Touch of Death`). It also carries a
		//                 COMBO (key 187) to hang the proc off, a second proc spell for the target, a trigger and a
		//                 magnitude - decoded and imported now, with 53070, as FeatProc. See ParseProcs.
		//   53054   x593  a spell TEMPLATE id (`Fiendish Hand` -> `Dark Hand`, `Combo - Skewer` -> `Skewered`), but
		//                 with a varying value and SEVERAL records per (feat, spell) - three on Fiendish Hand carrying
		//                 347/348/349. Undecoded.
		//   53030   x962  the per-SPELL twin of 53075: key 187 is a `Spell.Id` (238 of 240 distinct refs resolve)
		//                 where 53075 carries a line, and `Value` the same signed percent x100. IMPORTED as a
		//                 FeatSpellModifier with SpellId set - see ParseSpellModifiers below.
		//                 ⚠️ This kind used to be written off here as "not a player feat at all - the `Dimach Item`
		//                 crafting entries, all pointing at one constant ref (43464) with value 100". That was the
		//                 first SAMPLE, not the census: the Dimach rows sort first by feat id, and behind them are
		//                 239 other targets and 55 distinct values including a -20/-40/-60% ladder.
		//
		// So three of the six are GRANTS, not modifiers, which is a second reason the shape is not enough: importing
		// them here would file a spell the feat gives you as a boost to a spell you already have. Survey them with
		// `--report feat-spell-modifiers`, which prints each kind with its feat name and the LDB description.
		public static IReadOnlyList<SpellHitPercentModifier> ParseSpellModifiers(byte[] body)
		{
			if (body.Length <= 12)
				return [];

			var mods = new List<SpellHitPercentModifier>();
			foreach (var e in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
			{
				if (e.Kind != LineHitPercentKind && e.Kind != SpellHitPercentKind)
					continue;

				// Read BY KEY, and by the key the KIND says: 53075 carries its target in the line slot (1) and 53030
				// in the spell slot (187). The pair view lands on both by luck of position - the target is payload
				// word 0 either way - which is exactly the coincidence that makes it unsafe to rely on.
				var target = e.Value(e.Kind == LineHitPercentKind ? GameActionTypes.KeyLine : GameActionTypes.KeySpellHitPercentTarget);
				var percent = e.Value(GameActionTypes.KeyValue);
				if (target is null or 0 || percent is null)
					continue;

				mods.Add(new SpellHitPercentModifier(e.Kind, target.Value, (int)percent.Value));
			}

			return mods;
		}

		/// <summary>
		///     One parsed kind-53075/53030 element: the target - a <c>Spell.GuiStackingLine</c> under 53075 and a
		///     <c>Spell.Id</c> under 53030 - and the signed percent ×100 both carry in <c>Value</c>.
		/// </summary>
		public readonly record struct SpellHitPercentModifier(uint Kind, uint TargetId, int PercentHundredths);

		// The (reference, magnitude) pair whose payload is decoded, in its two target spaces: a spell LINE (53075,
		// #590) and a single SPELL (53030, #593). Same three slots, same signed percent x100.
		private const uint LineHitPercentKind = 53075;
		private const uint SpellHitPercentKind = 53030;

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

		// The COMBOS a feat upgrades (#593): kind 53209 `UpgradeCombo`, `[187 combo, 66, 67]` - 3,235 records, the
		// largest single block in the feat catalog and the last of the "decoded shape, undecoded payload" kinds the
		// #591 sweep left behind.
		//
		// Keys 66 and 67 are named out of the CLIENT, not inferred from the feat descriptions: the item-tooltip
		// builder `FUN_0149e450` reads `GameActionStat_e` 0x315 and 0x316 - which the key-index table maps to key 66
		// and key 67 - and branches on each one's SIGN to pick between the localised `UpgradeCombo_IncreaseDamage` /
		// `_DecreaseDamage` and `UpgradeCombo_IncreaseCooldown` / `_DecreaseCooldown` strings. So 66 is the combo's
		// DAMAGE delta and 67 its REUSE-TIME delta, both signed, both rendered as `value × 0.01` with a `%` suffix -
		// hundredths of a percent, the same scale kind 53075 uses. See docs/re/gameactionlist-grammar.md.
		//
		// ⚠️ Read BOTH slots, and do not fall back to the pair view: it returns (combo, 66) and so reports every
		// cooldown feat as a no-op with `value = 0`, which is exactly what the catalog looked like before this.
		public static IReadOnlyList<ComboModifier> ParseComboModifiers(byte[] body)
		{
			if (body.Length <= 12)
				return [];

			var mods = new List<ComboModifier>();
			foreach (var e in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
			{
				if (e.Kind != UpgradeComboKind)
					continue;

				// Every slot must be present - a mis-sliced element carrying half a payload is worse than none - and
				// a record that moves NEITHER axis is a reference with no magnitude, which nothing can consume.
				var combo = e.Value(GameActionTypes.KeyUpgradeComboTarget);
				var damage = e.Value(GameActionTypes.KeyUpgradeComboDamage);
				var cooldown = e.Value(GameActionTypes.KeyUpgradeComboCooldown);
				if (combo is null or 0 || damage is null || cooldown is null || (damage == 0 && cooldown == 0))
					continue;

				mods.Add(new ComboModifier(e.Kind, combo.Value, (int)damage.Value, (int)cooldown.Value));
			}

			return mods;
		}

		/// <summary>One parsed kind-53209 element: the combo, and its two signed hundredths-of-a-percent deltas.</summary>
		public readonly record struct ComboModifier(uint Kind, uint ComboId, int DamagePercentHundredths, int CooldownPercentHundredths);

		// One WEAPON ATTACK of a combo (#593): kind 53250 `ChangeInheritedWeaponAttack`,
		// `[187 combo, 84 attack, 66 dps, 67 crit, 68 hate, 69 damageType]` - 315 records over 12 feats, and the last
		// of the kinds #593 tabulated.
		//
		// The sibling of ParseComboModifiers, not a variant of it: 53209 moves the combo as a whole and this moves
		// one of its two ATTACKS, along three axes 53209 has no slot for. Every slot is named out of the client's own
		// tooltip branch at `014a1dc5` - see GameActionTypes and docs/re/gameactionlist-grammar.md.
		//
		// ⚠️ The SCALES differ from 53209's even though keys 66/67 are the same two GameActionStat slots: 53250's key
		// 66 is a WHOLE percent (raw int + `%`) and its key 67 a crit-chance RATING (`× 0.025`), where 53209 renders
		// both `× 0.01`. Carrying 53209's units across would report `Fierce Opening`'s +30% as +0.30%.
		public static IReadOnlyList<ComboAttackModifier> ParseComboAttackModifiers(byte[] body)
		{
			if (body.Length <= 12)
				return [];

			var mods = new List<ComboAttackModifier>();
			foreach (var e in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
			{
				if (e.Kind != ComboAttackKind)
					continue;

				var combo = e.Value(GameActionTypes.KeyComboAttackTarget);
				var slot = e.Value(GameActionTypes.KeyComboAttackSlot);
				var dps = e.Value(GameActionTypes.KeyComboAttackDps);
				var crit = e.Value(GameActionTypes.KeyComboAttackCriticalChance);
				var hate = e.Value(GameActionTypes.KeyComboAttackHate);
				var damageType = e.Value(GameActionTypes.KeyComboAttackDamageType);

				// A mis-sliced element carrying half a payload is worse than none, and a record that moves no axis at
				// all is a reference with no magnitude, which nothing can consume.
				if (combo is null or 0 || slot is null || dps is null || crit is null || hate is null || damageType is null)
					continue;

				if (dps == 0 && crit == 0 && hate == 0 && damageType == 0)
					continue;

				// The client REFUSES to render any other selector - its branch returns rather than falling through -
				// so a third value is a mis-slice, not a third attack we do not model yet.
				if (slot is not (0 or 1))
					continue;

				mods.Add(new ComboAttackModifier(e.Kind, combo.Value, (ComboAttackSlot)slot.Value, (int)dps.Value, (int)crit.Value, (int)hate.Value, damageType.Value));
			}

			return mods;
		}

		/// <summary>One parsed kind-53250 element - see <see cref="FeatComboAttackModifier" /> for each field's unit.</summary>
		public readonly record struct ComboAttackModifier(uint Kind, uint ComboId, ComboAttackSlot Slot, int DpsPercent, int CriticalChanceRating, int HatePercent, uint DamageTypeId);

		// The PROCS a feat attaches (#593): 53212 `AddComboProc` `[88, 236, 187 combo, 117, 237]` and 53070
		// `AddSpellLineProc` `[88, 236, 1 line, 117, 237, 73]` - one shape with the target space swapped, the same
		// pairing 53075/53030 and 53054/53062 have, so they share a table.
		//
		// The pair view is what hid these: it returns payload words 0 and 1, i.e. (key 88, key 236), so 53212 read as
		// "a spell template id, also a grant - the feat is NAMED for the spell, value 0 throughout". The zero was the
		// SECOND proc spell, empty on all but one record, and the combo it hangs off was three slots further along.
		//
		// The two spell slots are named by the client's own tooltip: it tests key 88 and key 236 for a valid spell
		// independently and renders `AddComboProcSelf` / `AddComboProcOther` / `AddComboProcBoth` accordingly, so 88
		// is the CARRIER's proc spell and 236 the TARGET's. See docs/re/gameactionlist-grammar.md.
		public static IReadOnlyList<Proc> ParseProcs(byte[] body)
		{
			if (body.Length <= 12)
				return [];

			var procs = new List<Proc>();
			foreach (var e in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
			{
				if (e.Kind != ComboProcKind && e.Kind != SpellLineProcKind)
					continue;

				var isCombo = e.Kind == ComboProcKind;
				var target = e.Value(isCombo ? GameActionTypes.KeyProcComboTarget : GameActionTypes.KeyLine);
				var selfSpell = e.Value(GameActionTypes.KeyProcSelfSpell);
				var targetSpell = e.Value(GameActionTypes.KeyProcTargetSpell);
				var trigger = e.Value(GameActionTypes.KeyProcTrigger);
				var amount = e.Value(GameActionTypes.KeyProcAmount);

				// A proc with nothing to cast is not a proc, and one with nothing to hang off cannot be attached -
				// either way the element was mis-sliced rather than meaningful.
				if (target is null or 0 || trigger is null || amount is null || ((selfSpell ?? 0) == 0 && (targetSpell ?? 0) == 0))
					continue;

				procs.Add(new Proc(e.Kind, isCombo ? target.Value : null, isCombo ? null : target.Value, Nullable(selfSpell), Nullable(targetSpell), trigger.Value,
					(int)amount.Value));
			}

			return procs;

			static uint? Nullable(uint? slot)
			{
				return slot is null or 0
					? null
					: slot;
			}
		}

		/// <summary>
		///     One parsed kind-53212/53070 element. Exactly one of <paramref name="ComboId" /> /
		///     <paramref name="SpellLineId" /> is set - the kind says which - and at least one of the two proc spells.
		/// </summary>
		public readonly record struct Proc(uint Kind, uint? ComboId, uint? SpellLineId, uint? SelfSpellId, uint? TargetSpellId, uint Trigger, int Amount);

		private const uint SpellGrantKind = 53019;
		private const uint UpgradeComboKind = 53209;
		private const uint ComboProcKind = 53212;
		private const uint SpellLineProcKind = 53070;
		private const uint ComboAttackKind = 53250;


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
			int SpellGrants, int ComboModifiers, int ComboAttackModifiers, int Procs, int ClassRows);
	}
}
