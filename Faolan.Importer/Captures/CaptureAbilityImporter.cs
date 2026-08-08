using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests the player's starting abilities from the retail 0x2052 ability-grant packets (the real wire format,
	// NOT the C++ 0x4d scheme; see Faolan.Core.Network/Codecs/Combat/AddSpecialAbilityCodec.cs). The grant body carries
	// the ability's Slot (template word) + Data0..4 identity quad; names are client-side (resolved by id).
	//
	// CLASS-AWARE: each create-flow capture is attributed to its character's class via the ChangeClass editor command
	// it contains (numericValue/2 = class id; same convention as HandleCommandPacket).
	//
	// NB there is no Class=0 "generic" row, whatever this comment used to claim: BOTH the CK_ClassAbility_Class CHECK
	// and GetClassAbilities' `ca.Class == @class` filter reject it, so a class-0 row would be unwritable AND unread.
	// The universal kit is MATERIALISED per class instead - all 12 rows - by DeriveClassAbilitiesFromCreatesAsync.
	// (The same stale "Class 0 = generic" claim is on ClassSpell; GetStartingSpells does honour `cs.Class == 0` there,
	// but the CHECK still forbids writing one, so it is equally dead.)
	public static class CaptureAbilityImporter
	{
		private const uint ChangeClassSubOpcode = ScryScriptCodec.Opcode; // client->server character-editor script callback
		private const uint ComboBookSubOpcode = ComboBookCodec.Opcode;

		// A saved action-bar slot holding a COMBO (ShortcutBarReplyCodec's record `type`). The bar is the only
		// per-character combo evidence for a character above the create flow.
		private const uint ComboShortcutType = 3;
		private const uint AddSpecialAbility = (uint)GameCharInterfaceMethods.DispatchAddSpecialAbility;

		// The RDB combo-catalog name-prefix -> class map. Unlike abilities, combos have no per-character grant to derive
		// from (retail never re-emits a ComboBookIIR on login - the combo book rides the 0x2076 world-entry snapshot
		// instead; see ImportCharacterCombosAsync), so class membership comes from the catalog's name prefix (e.g.
		// "Herald_..." = HeraldOfXotli). Hakotep's HoX lvl-24 0x2076 combo book validated that a catalog prefix reliably
		// identifies the class, so these LITERAL class-name prefixes are trusted - and they are exactly the 8 archetypes
		// that HAVE combos (matching the ellatha combos list), so casters (Necro/Demo/Priest/Tempest) correctly get none.
		// Matched case-insensitively (the catalog has both "BearShaman_" and "Bearshaman_").
		//
		// The ARCHETYPE prefixes are handled beside this list rather than in it (#581): `Soldier_` / `Rogue_` /
		// `Priest_` name an archetype, so each fans out to its three classes through `ConanArchetypes`.
		//
		// Every OTHER ambiguous prefix stays deliberately EXCLUDED (grant too little, not too much) - it maps to
		// multiple or uncertain classes and needs a taxonomy + validation: the Tortage starters (Slave_/Oar_),
		// feat-tree trees (Reaver/Liberator/Halberdier/Lich/Warmonk/Masterthief/SneakAttack), NPC combos
		// (combo_npc_*), racials (Cimmerian/Stygian/Khitan/Aquilonian), weapon-forms and one-off abilities. #581.
		private static readonly (string Prefix, ConanClass Class)[] ClassComboPrefixes =
		[
			("Guardian_", ConanClass.Guardian), ("Conqueror_", ConanClass.Conqueror), ("DarkTemplar_", ConanClass.DarkTemplar), ("Barbarian_", ConanClass.Barbarian), ("Ranger_", ConanClass.Ranger),
			("Assassin_", ConanClass.Assassin), ("BearShaman_", ConanClass.BearShaman), ("Herald_", ConanClass.HeraldOfXotli) // capture-validated: Hakotep HoX lvl-24 0x2076 combo book
		];

		// Derives the class<->ability ACCESS (ClassAbility) from the per-character grants (CharacterAbility x Character):
		// a row per (class, ability) that a captured character of that class holds, with the ability's PER-CLASS unlock
		// level = the MIN level of that class's holders (create-flow chars land its universal kit at L1; gameplay-only
		// abilities land at their high char levels -> naturally gated out of a fresh char). The data-driven replacement
		// for IsStartingAbility + the single Ability.Class - true multi-class (Spellweaving lands under every captured
		// caster class, not a wrong "generic"). The ability RDB template carries no class/level, so this is the source.
		public static async Task<(int Inserted, int Updated)> DeriveClassAbilitiesAsync(IDatabaseContext db, ILogger? logger = null)
		{
			var grants = await db
				.CharacterAbilities.Join(db.Characters, ca => ca.CharacterId, c => c.Id, (ca, c) => new
				{
					ca.AbilityId,
					c.Level,
					c.Class
				})
				.ToListAsync();

			// Mount abilities are excluded: they come with the MOUNT, not with a class (#303). The two characters that
			// hold them are a level-80 Necromancer and a level-80 Demonologist - who are also the only two in the
			// corpus that own a mount summon spell at all, so attributing them to those classes was mount OWNERSHIP
			// showing through as if it were class kit. A level-80 Barbarian who buys a horse would have got nothing,
			// and a mountless Necro six abilities for a mount they do not have. CharacterLoadout grants them from
			// ownership instead.
			var abilities = await db.Abilities.ToListAsync();
			var abilityIds = abilities.Where(a => !a.IsMountAbility).Select(a => a.Id).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));

			// Skip characters with an invalid class (0/1): create-flow chars captured BEFORE class selection carry an
			// unset class the importer records as-is - real classes are the 12 defined Class enum values. Without this
			// guard those pollute the access map with a phantom "class 1" holding the universal kit.
			var inserted = 0;
			var updated = 0;
			foreach (var g in grants.Where(x => abilityIds.Contains(x.AbilityId) && Enum.IsDefined(x.Class)).GroupBy(x => (x.Class, x.AbilityId)))
			{
				var minLevel = (byte)Math.Clamp(g.Min(x => (int)x.Level), 1, 255);
				if (existing.TryGetValue(g.Key, out var row))
				{
					// LOWER only. The level means "the earliest level this class is observed to have this ability", so
					// the answer across two sources is the min of both - and this pass's evidence is weaker than the
					// create pass's: a level-80 character holding an ability says nothing about when it unlocked, while
					// a create dump IS the level-1 grant burst. Assigning unconditionally let the weaker source win and
					// stamped the universal kit at the holder's level (23/80/24/80) for the four classes we have a
					// character for, hiding it from every fresh character of those classes.
					if (row.Level > minLevel)
					{
						row.Level = minLevel;
						updated++;
					}
				}
				else
				{
					db.ClassAbilities.Add(new ClassAbility
					{
						Class = g.Key.Class,
						AbilityId = g.Key.AbilityId,
						Level = minLevel
					});
					inserted++;
				}
			}

			if (inserted > 0 || updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureAbilityImporter: derived {Inserted} ClassAbility rows ({Updated} levels updated) from per-character grants.", inserted, updated);
			return (inserted, updated);
		}

		// The number of distinct classes an ability must be held by before "every eligible class holds it" is allowed to
		// mean UNIVERSAL rather than "the two characters we happen to have are both casters". Two is exactly the pair of
		// level-80 mages in the corpus, so two would make Spellweaving, the pet bar and the mount abilities universal -
		// each of which we can independently say is NOT (Spellweaving is caster kit, the pet bar is Necro/Demo, and a
		// mount ability comes with the MOUNT, docs/subsystems/mounts.md). Three is the first threshold those three fail.
		private const int UniversalWitnessClasses = 3;

		// Widens the abilities a LEVELLED character earns - Search, Hide, Path of Asura - from the handful of classes we
		// have a character for onto all twelve (#303).
		//
		// The problem this fixes: `DeriveClassAbilitiesAsync` can only see the four classes the corpus has a levelled
		// character for (Assassin 23, HoX 24, Necro 80, Demo 80). Every ability those four earned above the create burst
		// therefore lands on those four classes ONLY, so a level-80 Barbarian gets no Search, no Hide and no recall - not
		// because retail withholds them but because nobody captured a Barbarian past level 1. That is a hole in the
		// CORPUS being served to the client as if it were a rule.
		//
		// The rule, derived rather than hand-listed: an ability is universal when **every class that could have shown it
		// to us did**. Concretely, for each ability, take the lowest level any holder had it at; the classes ELIGIBLE to
		// witness it are those with a character at or above that level; and it is universal when the holders are exactly
		// the eligible classes and there are at least `UniversalWitnessClasses` of them. Then it is materialised on all
		// twelve at that lowest observed level.
		//
		// Why the eligibility test rather than "held by every class we have a character for": a level-5 Barbarian
		// imported tomorrow must not un-universalise Search - it is below the level Search is ever seen at, so it is not
		// evidence of absence. It only counts against an ability it is high enough to have earned.
		//
		// ⚠️ The level is an UPPER BOUND, not the unlock level: the assassin was 23 when captured, so all we know is
		// "by 23". Granting at 23 is the conservative direction - never earlier than retail was observed to.
		public static async Task<(int Inserted, int Abilities)> DeriveUniversalLevelledAbilitiesAsync(IDatabaseContext db, ILogger? logger = null)
		{
			var holders = await db
				.CharacterAbilities.Join(db.Characters, ca => ca.CharacterId, c => c.Id, (ca, c) => new
				{
					ca.AbilityId,
					c.Level,
					c.Class
				})
				.ToListAsync();
			holders = holders.Where(h => Enum.IsDefined(h.Class)).ToList();
			if (holders.Count == 0)
				return (0, 0);

			// The levelled roster: the highest level we have a character at, per class. A class is eligible to witness an
			// ability if this reaches the level the ability is first seen at.
			var topLevelByClass = holders.GroupBy(h => h.Class).ToDictionary(g => g.Key, g => g.Max(h => (int)h.Level));

			// Mount abilities are out of scope here too (see DeriveClassAbilitiesAsync): they are not something a
			// LEVELLED character earns, so they must never be widened onto all twelve classes either. The three-witness
			// guard below already rejects them today, but only because the corpus happens to hold exactly two mount
			// owners - which is a coincidence to depend on, not a rule.
			var abilityIds = (await db.Abilities.ToListAsync()).Where(a => !a.IsMountAbility).Select(a => a.Id).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));

			var inserted = 0;
			var universal = new List<uint>();
			foreach (var ability in holders.Where(h => abilityIds.Contains(h.AbilityId)).GroupBy(h => h.AbilityId))
			{
				var minLevel = (byte)Math.Clamp(ability.Min(h => (int)h.Level), 1, 255);
				var held = ability.Select(h => h.Class).ToHashSet();
				var eligible = topLevelByClass.Where(x => x.Value >= minLevel).Select(x => x.Key).ToHashSet();
				if (held.Count < UniversalWitnessClasses || !eligible.SetEquals(held))
					continue;

				universal.Add(ability.Key);
				foreach (var cls in Enum.GetValues<ConanClass>())
				{
					if (existing.TryGetValue((cls, ability.Key), out var row))
					{
						// Never RAISE a level: a class that already earns it earlier keeps its own evidence.
						if (row is { Level: > 0 } && row.Level > minLevel)
							row.Level = minLevel;

						continue;
					}

					db.ClassAbilities.Add(new ClassAbility
					{
						Class = cls,
						AbilityId = ability.Key,
						Level = minLevel
					});
					existing[(cls, ability.Key)] = null!;
					inserted++;
				}
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureAbilityImporter: {Abilities} abilities are universal across the levelled roster -> {Inserted} new ClassAbility rows.", universal.Count, inserted);
			return (inserted, universal.Count);
		}

		// Seeds the UNIVERSAL level-1 ability kit onto every class (ClassAbility), from the create captures: the
		// 0x2052 grants that follow a create. Rest needs to be granted to all classes, same with the
		// directional attacks, etc. - these are basic combat/movement/UI abilities, not class kit (#296).
		//
		// The kit is the UNION across all 12 create dumps, granted to all 12 classes - NOT each dump's own subset.
		// That is what the data says: all 12 dumps together yield only **8 distinct** ability ids (Rest, Upper Left /
		// Upper Right / Overhead Attack, Toggle Combat, Switch Weapons, Crouch, Dash). If any were class-specific the
		// union would be bigger, so every class grants a subset of ONE shared kit and the per-class differences are
		// capture noise - a dump that missed a packet - not design. Taking each dump's own subset left Conqueror
		// without Rest (its dump never shows the grant) while Guardian and the necro had it: an artefact, and exactly
		// the kind of hole that then reads as intentional.
		//
		// A dump carries ~14 0x2052 PACKETS but only ~7 DISTINCT ids - the grants repeat - so count ids, not packets;
		// the id-keyed dictionary collapses the duplicates for free. All 8 are already in the Abilities catalog.
		//
		// DetectClass resolves all 12 classes, but only to prove a dump IS a class create (a dump we can't attribute
		// might be some other capture) - the kit itself is class-independent.
		//
		// This exists because DeriveClassAbilitiesAsync below can only see classes we imported a CHARACTER for, and we
		// have exactly four (Assassin/Necro/HoX/Demo). That left the map in a state that was both wrong and
		// unreproducible: DarkTemplar had ZERO abilities despite its create dump carrying 14 grants, while seven other
		// classes held 7-8 rows apiece derived from characters that no longer exist - a from-scratch rebuild would
		// simply have lost them, since that pass only ever inserts/updates. Sourcing from the captures makes the whole
		// table reproducible and is the same trick starter-spells uses for spells: take the class from the capture,
		// not from a character that may never have been imported.
		//
		// Level 1: a create grant IS the level-1 kit by definition. DeriveClassAbilitiesAsync still runs afterwards and
		// contributes the gameplay-only abilities of the classes we do have characters for, at their real levels.
		public static async Task<(int Inserted, int Classes)> DeriveClassAbilitiesFromCreatesAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> createCaptures, ILogger? logger = null)
		{
			var kit = new HashSet<uint>();
			var dumps = 0;
			foreach (var capture in createCaptures)
			{
				if (DetectClass(capture) is not { } cls || !Enum.IsDefined((ConanClass)cls))
					continue; // can't confirm this is a class create

				var grants = new Dictionary<uint, (uint, uint, uint, uint, uint, uint)>();
				foreach (var packet in capture)
					TryReadAddSpecialAbility(packet, grants);

				if (grants.Count == 0)
					continue; // capture never reached the grant burst

				dumps++;
				kit.UnionWith(grants.Keys);
			}

			if (kit.Count == 0)
				return (0, 0);

			var abilityIds = (await db.Abilities.Select(a => a.Id).ToListAsync()).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));
			var inserted = 0;
			var corrected = 0;

			foreach (var cls in Enum.GetValues<ConanClass>())
				foreach (var abilityId in kit)
				{
					if (!abilityIds.Contains(abilityId))
						continue; // a grant for an ability we never cataloged would be a catalog gap, not a fake id. Does not fire today: the kit is 8 distinct ids and all 8 are cataloged.

					// The kit is level 1 BY DEFINITION - a create dump is the grant burst a level-1 character receives -
					// so an existing row carrying a higher level is wrong and is corrected down, not left alone. This
					// used to `continue` on any existing row, ceding the level to DeriveClassAbilitiesAsync below, which
					// derives it from the CHARACTERS we imported. No create-flow character is in that table (only four
					// leveled ones), so for exactly their four classes the min-of-holders was the leveled character's
					// own level and the universal kit was stamped 23/80/24/80 instead of 1. A level-1 Assassin then
					// matched NO abilities, was granted nothing, and - because the create flow keyed enter-world on the
					// client barring a grant - never entered the world at all (#433, fixed separately).
					if (existing.TryGetValue((cls, abilityId), out var row))
					{
						if (row is not { Level: > 1 })
							continue;

						row.Level = 1;
						corrected++;
						continue;
					}

					db.ClassAbilities.Add(new ClassAbility
					{
						Class = cls,
						AbilityId = abilityId,
						Level = 1 // the universal kit is level 1 by definition
					});
					existing[(cls, abilityId)] = null!;
					inserted++;
				}

			await db.SaveChangesAsync();
			logger?.LogInformation(
				"CaptureAbilityImporter: universal kit of {Kit} abilities (from {Dumps} create dumps) -> {Inserted} new class-ability rows across {Classes} classes ({Corrected} rows corrected back to level 1).", kit.Count,
				dumps, inserted, Enum.GetValues<ConanClass>().Length, corrected);
			return (inserted, kit.Count);
		}

		// Derives the class<->combo ACCESS map (ClassCombo) from the combo catalog's class-name prefixes - the combo
		// analogue of DeriveClassAbilitiesAsync (which derives from grants; combos have none). Combo.Level supplies the
		// per-class unlock level. Idempotent.
		//
		// ⭐ The name prefix is the WEAKER of the two sources and runs second (#581). The client states the answer:
		// `Combos.FeatId` is the WIRE feat id of the feat that GATES the combo, and `FeatGatedClasses` resolves it to
		// a class or an archetype out of the feat's own name. Where both speak they agree on 134 combos with ZERO
		// conflicts, which is what makes the feat authoritative for the handful where they differ.
		public static async Task<(int Inserted, int Updated)> DeriveClassCombosAsync(IDatabaseContext db, ILogger? logger = null)
		{
			var combos = await db
				.Combos.Select(c => new
				{
					c.Id,
					c.Name,
					c.Level,
					c.FeatId
				})
				.ToListAsync();
			var existing = await db.ClassCombos.ToDictionaryAsync(x => (x.Class, x.ComboId));

			var gating = await FeatGating.LoadAsync(db);

			var inserted = 0;
			var updated = 0;
			var removed = 0;
			var fromFeat = 0;
			var levelFromFeat = 0;
			foreach (var combo in combos)
			{
				if (combo.Name == null)
					continue;

				// 1. the GATING FEAT, when it names a class or an archetype. Direct client evidence, and the only
				//    source that can be NARROWER than the name: `Priest_05_damagedetaunt_Censuring_Strike_I_2hb`
				//    reads as all three priests by its prefix, and its feat says `Bear Shaman Class Feat 001` - which
				//    is the class that actually wields a two-handed blunt.
				var classes = gating.ClassesOf(combo.FeatId);
				var byFeat = classes.Count > 0;
				if (byFeat)
					fromFeat++;
				else
					// 2. an ARCHETYPE-shared combo (`Soldier_`, `Rogue_`, `Priest_`) belongs to all THREE classes of
					//    that archetype, so it fans out where a class-named prefix names one. It is the half no class
					//    prefix could ever match, and the half the stacking-line route (#296) refuses too, because
					//    those combos sit in the shared `20000`-`20008` block that reads as class 20 and is not one.
					classes = ConanArchetypes.FromNamePrefix(combo.Name) is { } archetype
						? ConanArchetypes.ClassesOf(archetype)
						: ClassComboPrefixes.Where(p => combo.Name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p.Class).ToArray();

				// The LEVEL is a second, separate question, and `Combos.Level <= 1` is the catalog's SENTINEL for
				// "not stated" rather than level one (docs/subsystems/combos.md). The gating feat answers that too
				// wherever every levelled combo it gates carries ONE level - which is exactly the archetype
				// `X: Class Specific N` feats. `SneakAttack_*` is the case: sentinel level, gated by
				// `Rogue: Class Specific 1`, whose three `Rogue_05_…` combos put it at level 5.
				var level = combo.Level;
				if (level <= 1 && gating.LevelOf(combo.FeatId) is { } lent)
				{
					level = lent;
					levelFromFeat++;
				}

				foreach (var cls in classes)
				{
					if (existing.TryGetValue((cls, combo.Id), out var row))
					{
						if (row.Level != level)
						{
							row.Level = level;
							updated++;
						}
					}
					else
					{
						db.ClassCombos.Add(new ClassCombo
						{
							Class = cls,
							ComboId = combo.Id,
							Level = level
						});
						inserted++;
					}
				}

				// A feat-derived answer REPLACES whatever a previous run's prefix guess wrote, so a narrowing actually
				// narrows. Only ever applied when the feat spoke - the prefix rule stays purely additive, because it
				// cannot tell "I have no opinion" from "no class", and a run against a DB whose Feats are not imported
				// yet would otherwise delete the whole map.
				if (!byFeat)
					continue;

				foreach (var (key, row) in existing)
					if (key.ComboId == combo.Id && !classes.Contains(key.Class))
					{
						db.ClassCombos.Remove(row);
						removed++;
					}
			}

			if (inserted > 0 || updated > 0 || removed > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation(
				"CaptureAbilityImporter: derived {Inserted} ClassCombo rows ({Updated} levels updated, {Removed} removed) - {FromFeat} combos attributed by their GATING FEAT, {LevelFromFeat} taking the feat's level over the catalog sentinel; the rest from validated catalog prefixes.",
				inserted, updated, removed, fromFeat, levelFromFeat);
			return (inserted, updated);
		}

		// The UNIVERSAL combo kit: the combos every created character is granted regardless of class, written as a
		// ClassCombo row per class (the combo twin of DeriveClassAbilitiesFromCreatesAsync). Idempotent.
		//
		// This is what the prefix derivation above structurally cannot produce. `DeriveClassCombosAsync` keys on the 8
		// CLASS-name prefixes, but the universal kit is named by RACE - `Cimmerian_Dance_combo1..3`,
		// `Khitan_…`, `Stygian_…`, `Aquilonian_…`: the 4 playable races' dance emotes, 12 combos (Gitea #416).
		//
		// ⚠️ Racially NAMED, universally GRANTED - the names invite a race-keyed grant and that is wrong. Across the
		// corpus 19 characters covering every class receive the SAME 12 (all four races' dances), so the grant follows
		// neither race nor class. That is also why this takes the INTERSECTION across create dumps where
		// DeriveClassAbilitiesFromCreatesAsync takes the union: a create burst carries the class's own combos too (the
		// Ranger's 14, the HoX's 21), and a union would hand every class the Ranger's.
		public static async Task<(int Inserted, int Kit)> DeriveUniversalCombosFromCreatesAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> createCaptures,
			ILogger? logger = null)
		{
			HashSet<uint>? kit = null;
			var dumps = 0;
			foreach (var capture in createCaptures)
			{
				if (DetectClass(capture) is not { } cls || !Enum.IsDefined((ConanClass)cls))
					continue; // can't confirm this is a class create

				var byCharacter = new Dictionary<uint, HashSet<uint>>();
				foreach (var packet in capture)
					TryReadCharacterGrant(packet, byCharacter, true);

				var granted = byCharacter.Values.SelectMany(x => x).ToHashSet();
				if (granted.Count == 0)
					continue; // capture never reached the combo burst

				dumps++;
				if (kit == null)
					kit = granted;
				else
					kit.IntersectWith(granted);
			}

			// One dump can't establish "universal" - it is indistinguishable from that class's own kit.
			if (kit is not { Count: > 0 } || dumps < 2)
			{
				logger?.LogInformation("CaptureAbilityImporter: no universal combo kit ({Dumps} usable create dumps).", dumps);
				return (0, 0);
			}

			var levels = await db.Combos.Where(c => kit.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Level);
			var existing = (await db.ClassCombos.Select(x => new
			{
				x.Class,
				x.ComboId
			})
				.ToListAsync()).Select(x => (x.Class, x.ComboId)).ToHashSet();
			var inserted = 0;

			foreach (var cls in Enum.GetValues<ConanClass>())
				foreach (var comboId in kit)
				{
					if (!levels.TryGetValue(comboId, out var level))
						continue; // a grant for a combo we never cataloged would be a catalog gap, not a fake id

					if (!existing.Add((cls, comboId)))
						continue; // already known; DeriveClassCombosAsync owns the level for those

					db.ClassCombos.Add(new ClassCombo
					{
						Class = cls,
						ComboId = comboId,
						Level = level
					});
					inserted++;
				}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureAbilityImporter: universal kit of {Kit} combos (intersection of {Dumps} create dumps) -> {Inserted} new ClassCombo rows across {Classes} classes.",
				kit.Count, dumps, inserted, Enum.GetValues<ConanClass>().Length);
			return (inserted, kit.Count);
		}

		// rawPackets = ALL captures. Catalogs every ability's Id + Slot/Data identity seen on the wire (the Data quad is
		// also its RDB 1000020 TEA key, so RdbAbilityImporter can name it). The class<->ability ACCESS and per-class
		// unlock LEVEL are NOT set here - they are derived from the per-character grants (DeriveClassAbilitiesAsync).
		public static async Task<(int Seen, int Inserted, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var grants = new Dictionary<uint, (uint Slot, uint D0, uint D1, uint D2, uint D3, uint D4)>();
			foreach (var packet in rawPackets)
				TryReadAddSpecialAbility(packet, grants);

			if (grants.Count == 0)
				return (0, 0, 0);

			var ids = grants.Keys.ToList();
			var existing = await db.Abilities.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

			var inserted = 0;
			var updated = 0;
			foreach (var (id, g) in grants)
			{
				if (!existing.TryGetValue(id, out var a))
				{
					a = new Ability
					{
						Id = id,
						// ⚠️ NOT named here (#638). Six human-readable names used to be baked in from the retired archive
						// sqlite, claiming to be "the only name source for these ability ids". They were not, and they
						// were WRONG: the client calls 769094 "Upper Left Attack", not "Normal Attack Left". The names
						// come from the client's own templates (`rdb-abilities` / `rdb-ldb-abilities`), which name all
						// six; an ability left blank by a captures-only run is honestly unnamed rather than mis-named.
						Name = string.Empty
					};
					db.Abilities.Add(a);
					existing[id] = a;
					inserted++;
				}
				else
					updated++;

				a.Slot = g.Slot;
				a.Data0 = g.D0;
				a.Key.Key1 = g.D1;
				a.Key.Key2 = g.D2;
				a.Key.Key3 = g.D3;
				a.Key.Key4 = g.D4;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureAbilityImporter: {Seen} ability grants seen, {Inserted} new, {Updated} updated.", grants.Count, inserted, updated);
			return (grants.Count, inserted, updated);
		}

		// Seeds each captured character's granted ABILITY set (CharacterAbility rows) from the 0x2052 grants, keyed by
		// the grant's RpcHeader receiver instance (= character id) - the ability twin of CaptureSpellImporter's learned-
		// spell seeding. Abilities are NOT in the snapshot known-list, so this is the authoritative per-character set
		// (the action bar is only shortcuts). Only characters already in the DB are touched; idempotent.
		public static async Task<(int Characters, int Inserted)> ImportCharacterAbilitiesAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, false);

			var (characters, inserted) = await SeedPerCharacterAsync(db, byCharacter,
				async cid => (await db.CharacterAbilities.Where(a => a.CharacterId == cid).Select(a => a.AbilityId).ToListAsync()).ToHashSet(), (cid, id) =>
					db.CharacterAbilities.Add(new CharacterAbility
					{
						CharacterId = cid,
						AbilityId = id
					}));

			logger?.LogInformation("CaptureAbilityImporter: per-character ability sets for {C} characters, {I} new CharacterAbility rows.", characters, inserted);
			return (characters, inserted);
		}

		// Seeds each captured character's COMBO book (CharacterCombo rows) from the ComboBookIIR grants (0x2000 carrier,
		// sub-opcode 0xA8BBEB7F), keyed by the RpcHeader receiver instance (= character id). Combos are class/level
		// content the client's combo book gains per level; a max-level imported character never dings, so this persisted
		// set is what GrantCharacterCombos re-emits on login. Only characters already in the DB are touched; idempotent.
		public static async Task<(int Characters, int Inserted)> ImportCharacterCombosAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, true);

			// SECOND source: the character's own ACTION BAR. A ComboBookIIR only ever reaches a character during the
			// create flow / early levels, so it covers exactly the characters the level filter skips and NONE of the
			// ones we import - which is why this used to seed nothing at all. A saved bar, though, names combos
			// directly: a type-3 shortcut IS a combo (see ShortcutBarReplyCodec), and every captured one resolves to a
			// catalogued combo. It is a floor rather than the whole book - only what the player kept on the bar - but
			// it is that character's real, evidenced set, and GrantCharacterCombos unions it with the class/level map.
			//
			// Runs off CharacterShortcuts, so this module must run AFTER character-state (declared in its DependsOn).
			foreach (var row in await db.CharacterShortcuts.Where(s => s.ShortcutType == ComboShortcutType).Select(s => new { s.CharacterId, s.TargetId }).ToListAsync())
			{
				if (!await db.Combos.AnyAsync(c => c.Id == row.TargetId))
					continue; // a bar slot pointing at something we have not catalogued as a combo

				(byCharacter.TryGetValue(row.CharacterId, out var set)
					? set
					: byCharacter[row.CharacterId] = []).Add(row.TargetId);
			}

			var (characters, inserted) = await SeedPerCharacterAsync(db, byCharacter,
				async cid => (await db.CharacterCombos.Where(c => c.CharacterId == cid).Select(c => c.ComboId).ToListAsync()).ToHashSet(), (cid, id) => db.CharacterCombos.Add(new CharacterCombo
				{
					CharacterId = cid,
					ComboId = id
				}));

			logger?.LogInformation("CaptureAbilityImporter: combo books for {C} characters, {I} new CharacterCombo rows.", characters, inserted);
			return (characters, inserted);
		}

		// The character class chosen in this capture, read from its ChangeClass editor callback (client->server
		// 0xA36D3B74, body = [ack u32][instance u32][reserved u8][pbLen u32][protobuf: f3 varint=value, f4 string=token]).
		// The editor sends class as 2x the real id (see HandleCommandPacket), so the class = value/2. Returns null when
		// the capture carries no ChangeClass (e.g. a login capture) so its grants stay generic by default.
		// Every packet of a create capture paired with the class that was SELECTED when it arrived (#581).
		//
		// ⚠️ `DetectClass` returns the FIRST `ChangeClass` in the capture and every caller filed the whole capture's
		// grants under it. That is wrong whenever the player browsed the editor's class list, because retail sends the
		// clicked class's spell/combo book each time so the UI can show it - so one capture legitimately carries
		// several classes' books. The damage is visible in the shipped data: `ClassSpells` gives GUARDIAN all four
		// archetype combo lines - Soldier 20002-20005 AND Rogue 20006-20007 AND Priest 20008, 22 rows a Guardian
		// cannot have - and no other class has a single archetype-line spell. One capture, one attribution.
		//
		// Walking in ORDER fixes it without needing to know how many classes were browsed: the class is whatever the
		// last callback selected, and packets before the first one have none.
		internal static IEnumerable<(uint? Class, byte[] Packet)> PacketsWithSelectedClass(IReadOnlyList<byte[]> capture)
		{
			uint? current = null;
			foreach (var packetBytes in capture)
			{
				try
				{
					if (TryReadChangeClass(packetBytes, out var classId, out _))
						current = classId;
				}
				catch
				{
					// not a parseable editor callback
				}

				yield return (current, packetBytes);
			}
		}

		// The distinct classes a create capture SELECTED, in order of first appearance. More than one means the player
		// browsed the editor, and that a per-capture attribution would have merged their books.
		internal static IReadOnlyList<uint> SelectedClasses(IReadOnlyList<byte[]> capture)
		{
			var seen = new List<uint>();
			foreach (var (cls, _) in PacketsWithSelectedClass(capture))
				if (cls is { } c && (seen.Count == 0 || seen[^1] != c) && !seen.Contains(c))
					seen.Add(c);

			return seen;
		}

		// How many COMBO grants one packet carries - the report's counter, so it reads the same reader the import does.
		internal static int CountComboGrants(byte[] packet)
		{
			var one = new Dictionary<uint, HashSet<uint>>();
			TryReadCharacterGrant(packet, one, true);
			return one.Values.Sum(x => x.Count);
		}

		internal static uint? DetectClass(IReadOnlyList<byte[]> capture)
		{
			foreach (var packetBytes in capture)
			{
				try
				{
					if (TryReadChangeClass(packetBytes, out var classId, out _))
						return classId;
				}
				catch
				{
					// not a parseable editor callback
				}
			}

			return null;
		}

		private static ulong ReadVarint(FaolanStream s)
		{
			ulong value = 0;
			var shift = 0;
			while (true)
			{
				var b = s.ReadByte();
				value |= (ulong)(b & 0x7f) << shift;
				if ((b & 0x80) == 0)
					break;

				shift += 7;
			}

			return value;
		}

		// Shared per-character seeding: for every (characterId -> id set), skip characters not in the DB and ids already
		// recorded, add the rest via the supplied factory, save once. Returns (characters touched, rows inserted).
		private static async Task<(int Characters, int Inserted)> SeedPerCharacterAsync(IDatabaseContext db, Dictionary<uint, HashSet<uint>> byCharacter, Func<uint, Task<HashSet<uint>>> loadExisting,
			Action<uint, uint> add)
		{
			if (byCharacter.Count == 0)
				return (0, 0);

			var characters = 0;
			var inserted = 0;
			foreach (var (characterId, ids) in byCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var existing = await loadExisting(characterId);
				var added = false;
				foreach (var id in ids)
				{
					if (!existing.Add(id))
						continue;

					add(characterId, id);
					inserted++;
					added = true;
				}

				if (added)
					characters++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			return (characters, inserted);
		}

		// 0x2052 body (no length prefix): [slot][id][id][0][1][data0][data1..4][data1..4][constA][constB]x2[1][0][u8].
		private static void TryReadAddSpecialAbility(byte[] packetBytes, IDictionary<uint, (uint, uint, uint, uint, uint, uint)> grants)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Method != AddSpecialAbility)
					return;

				var body = packet.BodyReader().ReadRemainingArray();

				// The 0x2052 grant is exactly what AddSpecialAbilityCodec builds/parses (slot, id, data0, key1..4) - use its
				// parser (which validates the full 81-byte shape) instead of re-reading the words by hand.
				var ability = AddSpecialAbilityCodec.Parse(body);
				if (ability.Id == 0)
					return;

				grants[ability.Id] = (ability.Slot, ability.Data0, ability.Key.Key1, ability.Key.Key2, ability.Key.Key3, ability.Key.Key4);
			}
			catch
			{
				// not a parseable 0x2052 ability grant
			}
		}

		// Mirrors HandleCommandPacket: a 0x2000 IIR whose sub-opcode is 0xA36D3B74 (editor script callback) and whose
		// body is [ack u32][instance u32][reserved u8][pbLen u32][protobuf: f3 varint=value, f4 string=token]. True only
		// for the ChangeClass token, returning value/2 as the class id (the editor sends class as 2x the real id).
		private static bool TryReadChangeClass(byte[] packetBytes, out uint classId, out string token)
		{
			classId = 0;
			token = null!;
			var packet = Rpc.Parse(packetBytes);
			var s = packet.BodyReader();
			if (s.Length < 21)
				return false;

			s.ReadUInt32(); // IIR length
			if (s.ReadUInt32() != ChangeClassSubOpcode) // sub-opcode
				return false;

			s.ReadUInt32(); // ack
			s.ReadUInt32(); // client instance
			s.ReadByte(); // reserved
			var pbLength = s.ReadUInt32();
			var pbEnd = s.Position + pbLength;

			uint numericValue = 0;
			while (s.Position < pbEnd && s.Position < s.Length)
			{
				var tag = s.ReadByte();
				var field = tag >> 3;
				var wireType = tag & 0x07;
				switch (wireType)
				{
					case 0:
					{
						var v = ReadVarint(s);
						if (field == 3)
							numericValue = (uint)v;
						break;
					}

					case 2:
					{
						// field 4 = token name, field 5 = value string; only field 4 is the command token. (Earlier
						// this captured EVERY length-delimited field, so the trailing field overwrote the token.)
						var str = s.ReadStringByteLength();
						if (field == 4)
							token = str;
						break;
					}

					default:
					{
						return false; // unexpected wire type - not a clean editor callback
					}
				}
			}

			if (token != "ChangeClass")
				return false;

			classId = numericValue / 2;
			return true;
		}

		// Reads one per-character grant into byCharacter, keyed by the RpcHeader receiver (= character id): an ability
		// grant (0x2052, ability id = body word 1, doubled) or a ComboBookIIR combo (0x2000 carrier, sub 0xA8BBEB7F,
		// comboId at body offset 18 after [len][subop][marker][instance][u16 addflag]).
		private static void TryReadCharacterGrant(byte[] packetBytes, IDictionary<uint, HashSet<uint>> byCharacter, bool isCombo)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				var body = packet.BodyReader().ReadRemainingArray();

				uint id;
				if (isCombo)
				{
					if (packet.Method != (uint)GameAgentMethods.PassBlob || body.Length < 22 || body.ReadU32Be(4) != ComboBookSubOpcode)
						return;

					id = ComboBookCodec.Parse(body.AsSpan(4)).ComboId;
				}
				else
				{
					// The same 0x2052 grant AddSpecialAbilityCodec parses; take its validated id.
					if (packet.Method != AddSpecialAbility)
						return;

					uint id2;
					try
					{
						id2 = AddSpecialAbilityCodec.Parse(body).Id;
					}
					catch (CodecParseException)
					{
						return; // not a well-formed grant
					}

					id = id2;
				}

				if (id == 0)
					return;

				var characterId = packet.Receiver.Instance;
				(byCharacter.TryGetValue(characterId, out var set)
					? set
					: byCharacter[characterId] = []).Add(id);
			}
			catch
			{
				// not a parseable per-character grant
			}
		}
	}
}
