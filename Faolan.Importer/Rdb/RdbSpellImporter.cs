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
	//   field 0x11F (287) = target range * 100 (Ice 2500 -> 25); fields 347/348/349 = the SPLASH triple
	//     (radius / damage percent * 100 / max targets), on 215 records - NOT the damage rating they were read as
	//     until #626.
	// Catalogs the FULL spell list from the RDB: every type-1040005 record that carries a display name is upserted
	// into the Spell table with its Name (the 0x21 string, same framing as item templates - see
	// RdbItemImporter.ExtractName), CastTime, Range, the splash triple, DurationMs, ManaCost and (additively) Level.
	// EffectSpell/min-max damage stay untouched - those really are rank/level-COMPUTED and absent from this
	// record. Idempotent: re-running only rewrites changed values. (#236)
	//
	// ⚠️ "Mana is rank/level-COMPUTED, not a base field, so it is not backfillable" (the claim this comment used to
	// make) was wrong, and wrong in an instructive way: mana IS level-scaled, but the SCALING is what the client does
	// with the field, not a reason the field is absent. Field 132 is usually the cost as a PERCENT of the caster's
	// mana pool, which is level-scaled by construction - see Spell.ManaCost (#629).
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
	// Field map (dumped over all 21,502 records, #367): 8=duration, 123=GUI Stacking Order, 125=level, 287=range,
	// 294=cast, 347/348/349=the splash triple (radius/damage/max-targets, #626), 447=channel tick interval (#349),
	// 551=Stacking Order, 79=icon (an id
	// into type 1010008 PNG), 321=effect-set (an id into type 1010214, animation/VFX XML only), 128=GUI Stacking Line
	// (#590/#634 - see Spell.GuiStackingLine).
	//
	// ⚠️ "This FIELD RUN carries no stat ids" was too strong, and #590 is why it is worth re-testing rather than
	// repeating: field **847 names an ATTRIBUTE** - 716 records carrying exactly 808 (Intelligence, x482), 810
	// (Wisdom, x224) and 804 (Strength, x10), which is the stat-id space, and it splits along sensible lines (the
	// Demonologist's nukes on Intelligence, the curses and DoTs on Wisdom). It is now READ, into
	// Spell.ScalingAttribute, as a per-spell OVERRIDE of the client's per-CLASS default (#592,
	// docs/rdb/spell-scaling.md).
	//
	// ⚠️ 551 is NOT a "rank-scaling magnitude" - that was our label for an unread number, and the client's own
	// stat-name table calls it `Stacking Order` (#633). The narrower claim it was supporting still stands on its own:
	// a buff's MAGNITUDES are not in this run, same as damage/recast (docs/subsystems/stat-engine.md).
	//
	// The run is 111 distinct fields wide and the importer reads 13 of them (`--report rdb-field-coverage`). Several
	// unread ones are on MORE records than the read ones - 54 is on 20,186 of 21,502 - so "we parse this table" is
	// not the same as "we have this table".
	//
	// The `[2][9]` REGION of the same record is the exception, and is imported below: explicit (kind, statId, amount)
	// records, the same shape item templates carry (docs/rdb/spell-stat-effects.md). It covers potions/elixirs and a
	// slice of the class buffs - 866 of 20,847 spells - not the buffs seen in retail captures, which carry no region
	// at all.
	public static class RdbSpellImporter
	{
		private const uint CastTimeField = 0x126; // 294 - cast time in centiseconds (value * 10 = milliseconds)
		// 347/348/349 - the SPLASH triple, named by the client's own stat-name table (LDB cat 615): Splash Radius /
		// Splash Damage / Splash damage max targets. All three sit on the same 215 records, so they are read together;
		// see Spell.SplashRadius for the units and for why 348 is NOT the "damage rating" it was imported as (#626).
		private const uint SplashRadiusField = 0x15B;
		private const uint SplashDamageField = 0x15C;
		private const uint SplashMaxTargetsField = 0x15D;

		// 447 - how often a CHANNELED spell ticks, in centiseconds (value * 10 = milliseconds), same unit as the cast
		// time and duration fields. The client names it `ChannelingInterval`; 521 records carry it, one of which is
		// the developers' "Oysteins Channeltest".
		private const uint ChannelingIntervalField = 0x1BF;
		private const uint DurationField = 0x008; // 8 - buff/effect duration in CENTISECONDS (×10ms); stored ×10 as ms. Rend 700=7s, Exhausted 10000=100s; 0 = none (#274)

		// How far into a leading number the shared ARCHETYPE block runs (#581/#296). The archetype combo lines are
		// 20000-20008 - `Soldier_` 20002-20005, `Rogue_` 20006-20007, `Priest_` 20008 - so three archetypes sit under
		// the one leading `20`, which is a BLOCK there rather than the Guardian class. Every class's OWN lines are far
		// above it: Guardian 20401-20406, Halberdier 20407-20410, Barbarian 18401-18409, Ranger 39401-39412.
		private const uint ArchetypeBlockWidth = 8;

		private const uint LdbType = 1030002; // #402: the localized-string DB, cat 50210 = spell names

		// 132 - the mana cost, carried RAW because the client reads it in two modes: a PERCENTAGE of the caster's
		// mana pool, or (when the flags word below says so) an absolute cost in wire hundredths. Recovered from the
		// client's own tooltip path (#629); see Spell.ManaCost for the decompiled function.
		private const uint ManaCostField = 0x084;

		// Byte 52 of the record - word 13, its FLAGS word, and the one the client keeps at `spell + 0xC8`. Bit 2 says
		// how to read the mana cost: set = a percent of the caster's pool, clear = an absolute cost in wire hundredths
		// (#630, Spell.ManaCostIsPercent). It is NOT a property-table field - it sits in the fixed struct ahead of the
		// ascending run, which is why three sweeps over that run found nothing.
		private const int FlagsWordOffset = 52;
		private const int ManaCostIsPercentBit = 2;

		// 186 - the client's `Mount` stat id (0x0BA): on a spell record it names the mount CREATURE the summon
		// spawns, so a record carrying it IS a mount summon. 155 records, 122 distinct creatures - the whole
		// mount catalog, lifted into Mounts (#627). See Mount.CreatureId.
		private const uint MountField = 0x0BA;

		// 53261 StartServerSideScryScriptWithParams - the GameAction a spell performs when its effect is not a stat
		// change but a server-side SCRIPT. On a pet summon its ScriptParams string is the whole summon (#640,
		// Spell.SummonSpawnHash). Other spells use the same action for other scripts, which is why the PARAMS decide
		// whether a record is a summon, not the action id.
		private const uint StartServerSideScryScriptWithParamsKind = 53261;

		// 499 - the QUALITY/rarity tier, the same field (and the same 0-indexed ItemQuality ladder) an item template
		// carries; on a mount record it is the ONLY field separating "Swift <horse>" from "<horse>". Read into
		// Mount.Quality - NOT as a speed, see Mount.Quality's remarks.
		private const uint QualityField = 0x1F3;

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
		//
		// ⚠️ NOT field 54, which the client's stat-name table also calls `Level` and which is on 20,186 records to
		// this one's 7,645 - a coverage gap that reads like a bug (#629). It is not one. Field 54 takes 23 values
		// over the whole catalog and 19,957 of them are 1 or 80, where 125 takes 43 values that track the real
		// unlock ladder: Gate of Hell 70, every rank-6 spell 80, `Ice Strike` 7/20/35/50/65/80 across its ranks.
		// Field 54 is a content BAND (pre- vs post-cap), not a requirement, and 45 named fields is exactly how many
		// chances there are to import the wrong one - see the field-name warning in docs/rdb/spell-record-fields.md.
		private const uint LevelField = 0x07D;

		// 123 - `GUI Stacking Order`, the spell's precedence within its stacking line. Read as the RANK until #634,
		// which it only correlates with; the real rank is the "(Rank N)" suffix in the name.
		private const uint StackingOrderField = 0x07B;
		// 128 - `GUI Stacking Line`, class-encoded (ConanClass x 1000 + index) in the common case; see
		// Spell.GuiStackingLine for why it is carried raw.
		private const uint StackingLineField = 0x080;
		private const uint RangeField = 0x11F; // 287 - target range * 100 (Ice Strike 2500 -> 25 metres)

		// 847 - the spell's scaling ATTRIBUTE as a wire stat id (808 Intelligence / 810 Wisdom / 804 Strength); see
		// Spell.ScalingAttribute. A per-spell OVERRIDE of the client's per-class default, so most records carry none.
		private const uint ScalingAttributeField = 0x34F;
		private const uint SpellDataType = 1040005;

		// The client's ability/effect catalog (docs/rdb/rdb-type-catalog.md), 146 records. It carries the consumable
		// potion effects, which have no 1040005 record at all - see FillDurationsFromEffectCatalog.
		private const uint EffectCatalogType = 1000621;

		// #271: the two GameAction element types that SET a base recast (docs/re/gameaction-keylists.tsv). Their
		// `Change...` twins (53031 / 53069) are modifiers, not a base, and are deliberately not read here.
		private const uint SetSpellRecastTimeKind = 53226;
		private const uint SetSpellLineRecastTimeKind = 53077;

		public static async Task<(int Seen, int Inserted, int NamesSet, int CastTimesSet, int LevelsSet, int Mounts, int EffectDurations)> ImportAsync(IDatabaseContext db, string rdbDir, string? cdnBase = null,
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
			foreach (var e in rdb.EntriesOfType(LdbType).Where(rdb.CanRead))
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
			var statEffects = new Dictionary<uint, Dictionary<uint, (uint Kind, int Amount, LevelRamp? Ramp)>>();
			// #627: (summon spell id -> mount creature id + tier) from field 186, synced into Mounts after the save.
			var mounts = new Dictionary<uint, (uint CreatureId, ItemQuality? Quality)>();
			// #640: (spell id -> pet summon params) off the 53261 element's ScriptParams string. Written onto the Spell
			// row below; this is what makes "which spells summon a pet" a catalog READ instead of a hand-typed list.
			var summons = new Dictionary<uint, SummonParams>();
			// #271: the base recast, off the record's GameAction ELEMENT region. Resolved only after the loop, because
			// deciding which element applies to a spell needs its GuiStackingLine, which this same loop sets.
			// `recastOwn` is what a record says when IT is cast; the two `assigned` maps are what every record says
			// about a target, so a rank carrying no element of its own still inherits its family's timer.
			var recastOwn = new Dictionary<uint, List<(uint Kind, uint Target, uint Value)>>();
			var assignedBySpell = new Dictionary<uint, uint>();
			var assignedByLine = new Dictionary<uint, uint>();
			var recastDisagreements = 0;

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
				var splashRadius = fields.TryGetValue(SplashRadiusField, out var sr)
					? sr / 100f
					: 0f;
				var splashDamage = fields.TryGetValue(SplashDamageField, out var sd)
					? sd / 100f
					: 0f;
				var splashMaxTargets = fields.GetValueOrDefault(SplashMaxTargetsField);
				var channelingIntervalMs = fields.TryGetValue(ChannelingIntervalField, out var ci)
					? ci * 10
					: 0u; // centiseconds -> ms
				var areaRadius = 0u; // metres, filled from the area-targeting elements below (#349)
				var durationMs = fields.TryGetValue(DurationField, out var du)
					? du * 10
					: 0u; // centiseconds -> ms
				var level = fields.TryGetValue(LevelField, out var lv) && lv <= byte.MaxValue
					? (byte)lv
					: (byte)0;
				var stackingLine = fields.GetValueOrDefault(StackingLineField);
				// Only the three attribute stat ids the field is ever observed to take are accepted. It is an
				// override read straight into a combat formula, so an unexpected value must land as "no override"
				// rather than as a stat id nothing scales off (#592).
				var scalingAttribute = fields.TryGetValue(ScalingAttributeField, out var sa) && IsAttributeStat(sa)
					? sa
					: 0u;
				var stackingOrder = fields.TryGetValue(StackingOrderField, out var rk) && rk <= byte.MaxValue
					? (byte)rk
					: (byte)0;
				// #629: carried RAW, both modes in the one column - clamping the flat rows into a percent range would
				// silently invent a cost, and picking the mode is the reader's job (CombatResolver.ResolveSpellManaCost).
				var manaCost = fields.TryGetValue(ManaCostField, out var mc) && mc <= ushort.MaxValue
					? (ushort)mc
					: (ushort)0;
				var manaCostIsPercent = body.Length >= FlagsWordOffset + 4 &&
				                        (body.ReadU32Le(FlagsWordOffset) >> ManaCostIsPercentBit & 1) == 1;

				// #627: a record carrying field 186 IS a mount summon, and the value is the creature it seats the
				// player on. Collected here rather than in a pass of its own precisely because it is a column lift
				// off a record this loop already decompressed and parsed.
				if (fields.TryGetValue(MountField, out var mountCreature) && mountCreature != 0)
					mounts[entry.Id] = (mountCreature, fields.TryGetValue(QualityField, out var q) && q <= (uint)ItemQuality.Legendary
						? (ItemQuality)q
						: null);

				// #367: buff/debuff stat deltas live in the same [2][9] region as item templates. The record's KIND is
				// carried through, not dropped: a spell mixes flat and PERCENT records in one region, and reading a
				// percent one as flat turns "+2% Critical Rating" into "+200" (ItemTemplateStatModifiers).
				// #271: the base recast. It is an ACTION the spell performs when cast - "set the recast of X" - so it
				// is filed under its TARGET, not under the record that carries it: `Mesmeric Gaze (Rank 4)` sets the
				// recast of ranks 1-4, and `Pestilential Blast` carries no element at all, which is exactly the 0 its
				// retail tooltip shows. Only the two SETTERS are read; ChangeRecastTime / ChangeLineRecastTime are
				// modifiers on a base the client already holds (docs/re/spell-recast-source.md).
				foreach (var element in ItemTemplateStatModifiers.ParseElements(body.AsSpan(12)))
				{
					// #640: a PET SUMMON spells itself out in the ScriptParams of its server-side-script element -
					// `spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781`. Lifted here for the same reason as
					// the mount above: a column read off a record this loop already has open.
					if (element.Kind == StartServerSideScryScriptWithParamsKind && SummonParams.TryParse(element.Text(GameActionTypes.KeyEventString), out var petSummon))
						summons[entry.Id] = petSummon;

					// #349: the AoE blast radius. It is an area-TARGETING element's Radius key, not a property field -
					// which is why the field run has none. Largest wins on a record carrying several (a cone plus a
					// ring): the radius the spell can reach is the outer one.
					if (GameActionTypes.AreaTargetKinds.Contains(element.Kind) && element.Value(GameActionTypes.KeyRadius) is { } radius)
						areaRadius = Math.Max(areaRadius, radius);

					var value = element.Value(GameActionTypes.KeyValue) ?? 0;
					uint target;
					if (element.Kind == SetSpellRecastTimeKind)
						target = element.Value(GameActionTypes.KeySpell) ?? 0;
					else if (element.Kind == SetSpellLineRecastTimeKind)
						target = element.Value(GameActionTypes.KeyLine) ?? 0;
					else
						continue;

					if (value == 0 || target == 0)
						continue;

					(recastOwn.TryGetValue(entry.Id, out var mine)
						? mine
						: recastOwn[entry.Id] = []).Add((element.Kind, target, value));

					// ⚠️ Records DISAGREE about a target - 338 spells and 12 lines are named with two different
					// values, because a rank arms its whole family and the ranks do not share a number (Embodiment
					// of Terror: rank 1's own record says 90 s, rank 2's says 80 s for that same rank 1). The
					// resolution below prefers what the spell's OWN record says, so this map only ever decides for
					// a spell that carries no element at all, and there the longest wins: under-gating a cast is
					// the failure that matters.
					var map = element.Kind == SetSpellRecastTimeKind
						? assignedBySpell
						: assignedByLine;
					if (map.TryGetValue(target, out var existing))
					{
						if (existing != value)
							recastDisagreements++;
						if (existing >= value)
							continue;
					}

					map[target] = value;
				}

				var mods = ItemTemplateStatModifiers.Parse(body.AsSpan(12));
				if (mods.Count > 0)
				{
					var deltas = new Dictionary<uint, (uint Kind, int Amount, LevelRamp? Ramp)>();
					foreach (var m in mods)
						deltas[m.StatId] = (m.Kind, (int)m.WireAmount, m.Ramp); // last write wins on a repeated stat
					statEffects[entry.Id] = deltas;
				}

				summons.TryGetValue(entry.Id, out var summon);

				if (!byId.TryGetValue(entry.Id, out var spell))
				{
					spell = new Spell
					{
						Id = entry.Id,
						Name = name,
						CastTime = castTime,
						Range = range,
						SplashRadius = splashRadius,
						SplashDamage = splashDamage,
						SplashMaxTargets = splashMaxTargets,
						ChannelingIntervalMs = channelingIntervalMs,
						AreaRadius = areaRadius,
						DurationMs = durationMs,
						Level = level,
						GuiStackingOrder = stackingOrder,
						GuiStackingLine = stackingLine,
						ScalingAttribute = scalingAttribute,
						ManaCost = manaCost,
						ManaCostIsPercent = manaCostIsPercent,
						SummonSpawnHash = summon?.Token,
						SummonIsCompanion = summon?.Companion ?? false,
						SummonPetBuff = summon?.PetBuff,
						SummonPointCost = summon?.PointCost
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
				spell.SplashRadius = splashRadius;
				spell.SplashDamage = splashDamage;
				spell.SplashMaxTargets = splashMaxTargets;
				spell.ChannelingIntervalMs = channelingIntervalMs;
				spell.AreaRadius = areaRadius;
				spell.DurationMs = durationMs;

				// The RDB leads on level, over both the scrapes and the captures (see LevelField).
				if (level != 0 && spell.Level != level)
				{
					spell.Level = level;
					levelsSet++;
				}

				// Both stacking fields are set unconditionally, unlike level: the RDB is their ONLY source, so there is
				// no other value to defer to and a 0 here means the record genuinely carries none (#590, #634).
				spell.GuiStackingOrder = stackingOrder;
				spell.GuiStackingLine = stackingLine;
				spell.ScalingAttribute = scalingAttribute;
				spell.ManaCost = manaCost;
				spell.ManaCostIsPercent = manaCostIsPercent;

				// #640: set unconditionally, like the stacking fields - the RDB is the only source, and a record with
				// no summon element genuinely is not a summon.
				spell.SummonSpawnHash = summon?.Token;
				spell.SummonIsCompanion = summon?.Companion ?? false;
				spell.SummonPetBuff = summon?.PetBuff;
				spell.SummonPointCost = summon?.PointCost;
			}

			// #271: apply the recast now that every row's GuiStackingLine is set. The spell's OWN record leads - it
			// is that spell's own statement about itself, and it is what the retail tooltip shows - and a record
			// naming the spell (or its line) from outside only fills in for a rank that carries no element, which is
			// how `Unimaginable Terror` rank 1 inherits the 45 s its rank-2 sibling arms on their shared line.
			// Never CLEARED, only set: a combo's recast comes from the ellatha list instead (no combo record carries
			// either element), so zeroing the column here would wipe it.
			var recastsSet = 0;
			foreach (var spell in byId.Values)
			{
				var recast = 0u;
				if (recastOwn.TryGetValue(spell.Id, out var own))
					foreach (var (kind, target, value) in own)
						if ((kind == SetSpellRecastTimeKind && target == spell.Id) ||
						    (kind == SetSpellLineRecastTimeKind && target == spell.GuiStackingLine))
							recast = value;

				if (recast == 0)
					recast = assignedBySpell.GetValueOrDefault(spell.Id);
				if (recast == 0 && spell.GuiStackingLine != 0)
					recast = assignedByLine.GetValueOrDefault(spell.GuiStackingLine);

				if (recast == 0 || spell.RecastCentiseconds == recast)
					continue;

				spell.RecastCentiseconds = recast;
				recastsSet++;
			}

			var effectDurations = FillDurationsFromEffectCatalog(rdb, byId, logger);

			if (toAdd.Count > 0)
				db.Spells.AddRange(toAdd);
			await db.SaveChangesAsync();

			var classSpellsAdded = await SyncClassMembership(db, byId.Values, logger);

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
					Amount = effect.Amount,
					// Null on every kind but the interpolated family (#599).
					HighAmount = effect.Ramp?.HighValue,
					LowLevel = effect.Ramp?.LowLevel,
					HighLevel = effect.Ramp?.HighLevel
				});
				effectRows++;
			}

			// #627: the mount catalog is wholly DERIVED from field 186, so the RDB is authoritative for the table as a
			// whole - a row whose record no longer carries the field is gone, not stale. Rows whose summon spell did
			// not make it into the catalog (a nameless dev record) are dropped rather than inserted as an FK orphan.
			var stillThere = await db.Mounts.ToListAsync();
			foreach (var row in stillThere.Where(m => !mounts.ContainsKey(m.Id)))
				db.Mounts.Remove(row);
			var mountRows = stillThere.ToDictionary(m => m.Id);
			var mountsWritten = 0;
			foreach (var (spellId, (creatureId, quality)) in mounts)
			{
				if (!byId.ContainsKey(spellId))
					continue; // the summon spell is not catalogued (nameless dev record) - Mount.Id FKs Spells

				if (!mountRows.TryGetValue(spellId, out var row))
					db.Mounts.Add(new Mount
					{
						Id = spellId,
						CreatureId = creatureId,
						Quality = quality
					});
				else
				{
					row.CreatureId = creatureId;
					row.Quality = quality;
				}

				mountsWritten++;
			}

			await db.SaveChangesAsync();

			logger?.LogInformation(
				"RdbSpellImporter: {Seen} named spell records, {Inserted} new, {NamesSet} names set, {CastTimesSet} cast times set, {StatSpells} spells with {StatRows} stat effects, {Mounts} mounts, {Recasts} recasts set ({Disagreements} cross-record disagreements), {ClassSpells} class<->spell rows, {EffectDurations} durations from the effect catalog.",
				seen, inserted, namesSet, castTimesSet, statEffects.Count, effectRows, mountsWritten, recastsSet, recastDisagreements, classSpellsAdded, effectDurations);
			return (seen, inserted, namesSet, castTimesSet, levelsSet, mountsWritten, effectDurations);
		}

		/// <summary>
		///     Fill the class↔spell ACCESS map from the spell's own <c>GuiStackingLine</c> (#296). A stacking line
		///     encoded as <c>ConanClass × 1000 + lineIndex</c> names the class the line belongs to, so the client's
		///     own catalog answers "which class may cast this" for all twelve classes at once.
		/// </summary>
		/// <remarks>
		///     This replaces an AoC-fandom-wiki scrape, whose importer asserted the opposite - "the client RDB spell
		///     record has no class field (class progression is server-side), so the community wiki is the source".
		///     It has one, and the two AGREE on 342 of the 343 rows where both spoke, which is what says the field
		///     really is class membership rather than something that correlates with it.
		///     <para>
		///         ⚠️ ADDITIVE, never destructive. The capture-sourced rows carry two facts this cannot see -
		///         <see cref="ClassSpell.IsStarter" /> (the create kit is a LIST, not a level query) and
		///         <see cref="ClassSpell.GrantLevel" /> (the level a capture SHOWS the grant at) - so an existing row
		///         is left exactly as it is. A spell already attributed to a DIFFERENT class is counted and logged
		///         rather than silently doubled up: one class-encoded line means one class, so a second attribution
		///         is a disagreement to look at, not a spell two classes share.
		///     </para>
		/// </remarks>
		private static async Task<int> SyncClassMembership(IDatabaseContext db, IEnumerable<Spell> spells, ILogger? logger)
		{
			var known = (await db.ClassSpells.ToListAsync())
				.GroupBy(c => c.SpellId)
				.ToDictionary(g => g.Key, g => g.Select(c => c.Class).ToList());

			// #581: which stacking LINE belongs to which archetype, from the combos that drive its spells. Per line
			// rather than per spell, because a line's spells are the weapon variants of one combo family and only some
			// of them have a catalogued combo - reading it per spell left the un-named variants of line 20008 (three
			// `Censuring Strike` rows) attributed to Guardian while their siblings went to the Priest classes.
			var lineOf = await db.Spells.Where(s => s.GuiStackingLine > 0).Select(s => new
			{
				s.Id,
				s.GuiStackingLine
			}).ToDictionaryAsync(s => s.Id, s => s.GuiStackingLine);

			var archetypeOfLine = (await db.Combos.Where(c => c.Name != null).Select(c => new
				{
					c.Name,
					c.GameActionId
				}).ToListAsync())
				.Select(c => (Line: lineOf.GetValueOrDefault(c.GameActionId), Archetype: ConanArchetypes.FromNamePrefix(c.Name!)))
				.Where(x => x is { Line: > 0, Archetype: not null })
				.GroupBy(x => x.Line)
				.Where(g => g.Select(x => x.Archetype).Distinct().Count() == 1) // a line naming two archetypes is not evidence
				.ToDictionary(g => g.Key, g => g.First().Archetype!.Value);

			// The leading numbers that hold at least one archetype line. Every OTHER line under such a number is in the
			// same shared block, so it is not that number's class either - we simply cannot say whose it is.
			var sharedBlocks = archetypeOfLine.Keys.Select(l => l / 1000).ToHashSet();

			var added = 0;
			var conflicts = 0;
			var shared = 0;
			var skipped = 0;
			foreach (var spell in spells)
			{
				if (spell.GuiStackingLine == 0 || !Enum.IsDefined((ConanClass)(spell.GuiStackingLine / 1000)))
					continue; // no line, or a line whose high part is not a class (50000 and 440061 - see Spell.GuiStackingLine)

				// ⚠️ `line / 1000` is NOT always a class, and this is exactly the trap #296 called out on the COMBO side
				// and this side never got. The archetype-shared combos sit in the `20000`-`20008` block - `Soldier_`
				// 20002-20005, `Rogue_` 20006-20007, `Priest_` 20008 - so THREE different archetypes share the leading
				// `20`, which is a BLOCK there and not Guardian. Reading it as a class shipped Guardian all 22 of the
				// Rogue and Priest archetype spells (Cunning Strike I-VI, Vicious Strike I-II, Censuring Strike I-III),
				// while no other class held a single archetype spell.
				//
				// Recognised from the DATA rather than by hardcoding the range: a LINE whose combos are named for an
				// archetype is that archetype's, and its spells go to all three of the archetype's classes. Another
				// line under the same leading number is in the same shared block, so it is left UNATTRIBUTED rather
				// than handed to the class that number would name - "grant too little rather than guess wrong", as the
				// combo prefixes put it. That is lines 20000 and 20001 (one spell each, `Redoubt` among them): the
				// block is shared, and nothing says which archetype they are for.
				var archetypeLine = archetypeOfLine.TryGetValue(spell.GuiStackingLine, out var archetype);
				if (!archetypeLine && sharedBlocks.Contains(spell.GuiStackingLine / 1000) && spell.GuiStackingLine % 1000 <= ArchetypeBlockWidth)
				{
					skipped++;
					continue;
				}

				var classes = archetypeLine
					? ConanArchetypes.ClassesOf(archetype)
					: [(ConanClass)(spell.GuiStackingLine / 1000)];

				if (archetypeLine)
					shared++;

				foreach (var @class in classes)
				{
					if (known.TryGetValue(spell.Id, out var already))
					{
						if (already.Contains(@class))
							continue;

						// Only a LINE-derived reading can disagree - an archetype fan-out is three classes by design.
						if (!archetypeLine)
						{
							conflicts++;
							logger?.LogWarning("RdbSpellImporter: spell {Spell} '{Name}' is on line {Line} ({Class}) but is already mapped to {Other} - adding the RDB's reading alongside it.",
								spell.Id, spell.Name, spell.GuiStackingLine, @class, string.Join('/', already));
						}
					}

					db.ClassSpells.Add(new ClassSpell
					{
						SpellId = spell.Id,
						Class = @class
					});
					added++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation(
				"RdbSpellImporter: {Added} class<->spell rows from the class-encoded stacking line ({Shared} archetype-shared spells fanned out to three classes each, {Skipped} left unattributed in a shared block, {Conflicts} disagreed with an existing mapping).",
				added, shared, skipped, conflicts);
			return added;
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

		// The five wire stat ids that are character ATTRIBUTES. Field 847 is only ever one of Strength / Intelligence
		// / Wisdom in the shipped data; the other two are accepted because the field's meaning is "an attribute", and
		// a Constitution- or Dexterity-scaled spell would be a datum, not a parse failure.
		private static bool IsAttributeStat(uint statId) =>
			(CharacterStatId)statId is CharacterStatId.Strength or CharacterStatId.Constitution
				or CharacterStatId.Intelligence or CharacterStatId.Wisdom or CharacterStatId.Dexterity;

		// Reads the spell-data field table from a record body (the span AFTER the 12-byte RDB-Id header): the LONGEST
		// run of [fieldId u32][value u32] LE pairs with strictly-INCREASING fieldId in (0, 2000], starting at any
		// offset - the spell-data grammar (the inverse of the item-template's strictly-decreasing table). Returns the
		// fieldId -> value map of that best run.
		// `internal` because --report spell-line-ids needs the SAME parse - a second copy of a grammar this fiddly
		// (a longest strictly-increasing run, not a fixed offset) is how a report quietly stops agreeing with the
		// importer, exactly as RdbItemImporter.TryReadPropertyField already learned.
		/// <summary>
		///     Fills <see cref="Spell.DurationMs" /> from the client's ABILITY/EFFECT catalog (RDB type 1000621) for
		///     rows the spell catalog (1040005) does not carry a duration for (#659).
		/// </summary>
		/// <remarks>
		///     A buff can be catalogued as a `Spells` row without having a 1040005 record at all: the captures
		///     catalogue whatever retail applies (<c>CaptureSpellImporter</c>), and the consumable-potion effects live
		///     in the 146-record ability/effect catalog instead. `XP Boost (4h)` (5058659) is the worked case - no
		///     spell record, so nothing ever set its duration, while its own effect record states 1,440,000 cs and its
		///     description says "for 4 hours".
		///     <para>
		///         ⚠️ It only ever FILLS an empty row, never overwrites one: where both catalogs hold a record, the
		///         spell record is the spell's own statement about itself.
		///     </para>
		/// </remarks>
		private static int FillDurationsFromEffectCatalog(RdbReader rdb, Dictionary<uint, Spell> byId, ILogger? logger)
		{
			var filled = 0;
			foreach (var entry in rdb.EntriesOfType(EffectCatalogType))
			{
				if (entry.Size == 0 || !rdb.CanRead(entry) || !byId.TryGetValue(entry.Id, out var spell) || spell.DurationMs > 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				if (body.Length < 12 || !ReadTaggedFields(body.AsSpan(12)).TryGetValue(DurationField, out var centiseconds) || centiseconds == 0)
					continue;

				logger?.LogInformation("RdbSpellImporter: spell {Spell} '{Name}' has no {SpellType} record; its {EffectType} effect record states {Centiseconds} cs.",
					spell.Id, spell.Name, SpellDataType, EffectCatalogType, centiseconds);
				spell.DurationMs = centiseconds * 10;
				filled++;
			}

			return filled;
		}

		/// <summary>
		///     The ABILITY/EFFECT catalog's field table: the same strictly-increasing field run as
		///     <see cref="ReadIncreasingFields" />, but its records interleave a TAG byte between each field id and its
		///     value - <c>[fieldId u32][tag u8][value u32]</c>, a 9-byte stride - and the run does not start on a word
		///     boundary, so the spell reader cannot see it at all.
		/// </summary>
		/// <remarks>
		///     Pinned on `XP Boost (4h)` (5058659) by three independent readings of the same run: field 8 = 1,440,000 cs
		///     against a description that says "for 4 hours"; field 54 = 1, as every one of its `(4h)` siblings carries;
		///     and field 79 = 5,061,875, which lands inside the icon block its four siblings occupy (5,061,874/82/83/84).
		///     A stride or alignment that were off would break all three, not one.
		/// </remarks>
		internal static Dictionary<uint, uint> ReadTaggedFields(ReadOnlySpan<byte> body)
		{
			const int stride = 9;
			var best = new Dictionary<uint, uint>();
			for (var start = 0; start + stride <= body.Length; start++)
			{
				uint last = 0;
				var haveLast = false;
				var cur = new Dictionary<uint, uint>();
				for (var at = start; at + stride <= body.Length; at += stride)
				{
					var fid = body.ReadU32Le(at);
					if (fid == 0 || fid > 2000 || (haveLast && fid <= last))
						break;

					cur[fid] = body.ReadU32Le(at + 5);
					last = fid;
					haveLast = true;
				}

				if (cur.Count > best.Count)
					best = cur;
			}

			return best;
		}

		internal static Dictionary<uint, uint> ReadIncreasingFields(ReadOnlySpan<byte> body)
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
