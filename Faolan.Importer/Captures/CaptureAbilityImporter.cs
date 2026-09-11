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
	/// <summary>
	///     Harvests the player's abilities from the retail DispatchAddSpecialAbility (0x2052) grant packets and derives
	///     the class-to-ability and class-to-combo access maps.
	/// </summary>
	/// <remarks>
	///     The grant body carries the ability's Slot (template word) and the Data0..4 identity quad; names are
	///     client-side, resolved by id (see <see cref="AddSpecialAbilityCodec" />). Each create-flow capture is attributed
	///     to its character's class via the ChangeClass editor command it contains (numericValue/2 = class id; the same
	///     convention as HandleCommandPacket).
	///     There is no Class=0 "generic" row: both the CK_ClassAbility_Class check and GetClassAbilities' class filter
	///     reject it, so the universal kit is materialised per class - all 12 rows - by
	///     <see cref="DeriveClassAbilitiesFromCreatesAsync" />.
	/// </remarks>
	public static class CaptureAbilityImporter
	{
		private const uint ChangeClassSubOpcode = ScryScriptCodec.Opcode; // client->server character-editor script callback
		private const uint ComboBookSubOpcode = ComboBookCodec.Opcode;

		// A saved action-bar slot holding a combo (ShortcutBarReplyCodec's record `type`). The bar is the only
		// per-character combo evidence for a character above the create flow.
		private const uint ComboShortcutType = 3;
		private const uint AddSpecialAbility = (uint)GameCharInterfaceMethods.DispatchAddSpecialAbility;

		// How many of a tree's members must already be attributed before the tree may lend its class to the rest
		// (InheritTreeClassesAsync). Three, so a single stray row cannot define a tree.
		private const int MinTreeEvidence = 3;

		// Combo names whose leading `_`-segment is not a feat tree - the NPC/monster/generic families. Excluded from
		// tree inheritance outright: `combo_npc_*` splits to `combo`, which would then be the same "tree" as the
		// player-facing `Combo_Charge_*` family.
		private static readonly string[] NonTreeComboPrefixes = ["combo_npc_", "monster", "generic"];

		// The RDB combo-catalog name-prefix -> class map. Combos have no per-character grant to derive from (retail never
		// re-emits a ComboBookIIR on login - the combo book rides the 0x2076 world-entry snapshot instead; see
		// ImportCharacterCombosAsync), so class membership comes from the catalog's name prefix (e.g. "Herald_" =
		// HeraldOfXotli). These are exactly the 8 archetypes that have combos, so the casters (Necro/Demo/Priest/Tempest)
		// correctly get none. Matched case-insensitively (the catalog has both "BearShaman_" and "Bearshaman_").
		// The archetype prefixes (`Soldier_` / `Rogue_` / `Priest_`) are handled beside this list rather than in it: each
		// fans out to its three classes through `ConanArchetypes`. Every other ambiguous prefix stays deliberately
		// excluded (grant too little, not too much): the Tortage starters (Slave_/Oar_), feat-tree names
		// (Reaver/Liberator/Halberdier/Lich/Warmonk/Masterthief/SneakAttack), NPC combos (combo_npc_*), racials
		// (Cimmerian/Stygian/Khitan/Aquilonian), weapon-forms and one-off abilities map to multiple or uncertain classes.
		private static readonly (string Prefix, ConanClass Class)[] ClassComboPrefixes =
		[
			("Guardian_", ConanClass.Guardian), ("Conqueror_", ConanClass.Conqueror), ("DarkTemplar_", ConanClass.DarkTemplar), ("Barbarian_", ConanClass.Barbarian), ("Ranger_", ConanClass.Ranger),
			("Assassin_", ConanClass.Assassin), ("BearShaman_", ConanClass.BearShaman), ("Herald_", ConanClass.HeraldOfXotli) // capture-validated against a HoX 0x2076 combo book
		];

		/// <summary>
		///     Derives the class-to-ability access map (ClassAbility) from the per-character grants
		///     (CharacterAbility x Character).
		/// </summary>
		/// <remarks>
		///     One row per (class, ability) that a captured character of that class holds, with the per-class unlock level
		///     being the minimum level of that class's holders: create-flow characters land the universal kit at level 1,
		///     while gameplay-only abilities land at their holders' high levels and are naturally gated out of a fresh
		///     character. This is the multi-class source of access (Spellweaving lands under every captured caster class);
		///     the ability RDB template carries no class or level.
		/// </remarks>
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

			// Mount abilities are excluded: they come with the mount, not with a class. Attributing them to the classes of
			// the characters that happen to own a mount would be mount ownership showing through as class kit;
			// CharacterLoadout grants them from ownership instead.
			var abilities = await db.Abilities.ToListAsync();
			var abilityIds = abilities.Where(a => !a.IsMountAbility).Select(a => a.Id).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));

			// Skip characters with an invalid class (0/1): create-flow characters captured before class selection carry an
			// unset class the importer records as-is - real classes are the 12 defined Class enum values. Without this
			// guard those pollute the access map with a phantom "class 1" holding the universal kit.
			var inserted = 0;
			var updated = 0;
			foreach (var g in grants.Where(x => abilityIds.Contains(x.AbilityId) && Enum.IsDefined(x.Class)).GroupBy(x => (x.Class, x.AbilityId)))
			{
				var minLevel = (byte)Math.Clamp(g.Min(x => (int)x.Level), 1, 255);
				if (existing.TryGetValue(g.Key, out var row))
				{
					// Lower only. The level means "the earliest level this class is observed to have this ability", so
					// the answer across two sources is the min of both - and this pass's evidence is weaker than the
					// create pass's: a levelled character holding an ability says nothing about when it unlocked, while
					// a create dump is the level-1 grant burst. Assigning unconditionally would let the weaker source
					// stamp the universal kit at the holder's level, hiding it from every fresh character of that class.
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
		// mean universal. Two classes can be a pair of casters that share class kit (Spellweaving, the pet bar) or a pair
		// of mount owners (a mount ability comes with the mount, docs/subsystems/mounts.md); three is the first threshold
		// those cases fail.
		private const int UniversalWitnessClasses = 3;

		/// <summary>
		///     Widens the abilities a levelled character earns - Search, Hide, Path of Asura - from the classes the corpus
		///     has a levelled character for onto all twelve.
		/// </summary>
		/// <remarks>
		///     <see cref="DeriveClassAbilitiesAsync" /> can only see the classes the corpus has a levelled character for,
		///     so every ability earned above the create burst would otherwise land on those classes only: a hole in the
		///     corpus served to the client as if it were a rule.
		///     An ability is universal when every class that could have shown it did: for each ability, take the lowest
		///     level any holder had it at; the classes eligible to witness it are those with a character at or above that
		///     level; the ability is universal when the holders are exactly the eligible classes and there are at least
		///     <see cref="UniversalWitnessClasses" /> of them. It is then materialised on all twelve at that lowest
		///     observed level.
		///     The eligibility test, rather than "held by every class we have a character for", means a low-level
		///     character of a new class cannot un-universalise an ability it is too low to have earned: it only counts
		///     against an ability it is high enough to have.
		///     The level is an upper bound, not the unlock level: a holder captured at 23 only proves "by 23". Granting at
		///     that level is the conservative direction - never earlier than retail was observed to.
		/// </remarks>
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
			// levelled character earns, so they must never be widened onto all twelve classes. The witness guard below
			// is not a substitute - it only rejects them while the corpus holds few mount owners.
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
						// Never raise a level: a class that already earns it earlier keeps its own evidence.
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

		/// <summary>
		///     Seeds the universal level-1 ability kit onto every class (ClassAbility) from the create captures: the
		///     0x2052 grants that follow a create (Rest, the directional attacks, Toggle Combat, Switch Weapons, Crouch,
		///     Dash - basic combat/movement/UI abilities, not class kit).
		/// </summary>
		/// <remarks>
		///     The kit is the union across all create dumps, granted to all 12 classes - not each dump's own subset. The
		///     dumps together yield only a handful of distinct ids; if any were class-specific the union would be bigger,
		///     so every class grants a subset of one shared kit and the per-class differences are capture noise (a dump
		///     that missed a packet), not design. Taking each dump's own subset would leave a class without Rest because
		///     its dump never shows the grant.
		///     A dump carries the same grant repeatedly, so ids are counted, not packets; the id-keyed dictionary collapses
		///     the duplicates. DetectClass is used only to prove a dump is a class create - the kit itself is
		///     class-independent.
		///     Sourcing the kit from the captures rather than from imported characters makes the table reproducible from
		///     scratch: <see cref="DeriveClassAbilitiesAsync" /> can only see classes a character was imported for, and
		///     runs afterwards to contribute those classes' gameplay-only abilities at their real levels. A create grant is
		///     the level-1 kit by definition.
		/// </remarks>
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
						continue; // a grant for an ability we never cataloged would be a catalog gap, not a fake id

					// The kit is level 1 by definition - a create dump is the grant burst a level-1 character receives -
					// so an existing row carrying a higher level is wrong and is corrected down, not left alone. Ceding
					// the level to DeriveClassAbilitiesAsync would stamp the kit at an imported levelled character's own
					// level, and a fresh character of that class would then match no abilities at all.
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

		/// <summary>
		///     Derives the class-to-combo access map (ClassCombo) - the combo analogue of
		///     <see cref="DeriveClassAbilitiesAsync" /> (which derives from grants; combos have none). Combo.Level supplies
		///     the per-class unlock level. Idempotent.
		/// </summary>
		/// <remarks>
		///     Three sources, strongest first: the gating feat (`Combos.FeatId` is the wire feat id of the feat that gates
		///     the combo, and <see cref="FeatGating" /> resolves it to a class or an archetype out of the feat's own name),
		///     then the catalog's name prefix, then tree inheritance. Where the feat and the prefix both speak they agree,
		///     which is what makes the feat authoritative where they differ.
		/// </remarks>
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

				// 1. The gating feat, when it names a class or an archetype. Direct client evidence, and the only
				//    source that can be narrower than the name: `Priest_05_damagedetaunt_Censuring_Strike_I_2hb`
				//    reads as all three priests by its prefix, and its feat says `Bear Shaman Class Feat 001` - which
				//    is the class that actually wields a two-handed blunt.
				var classes = gating.ClassesOf(combo.FeatId);
				var byFeat = classes.Count > 0;
				if (byFeat)
					fromFeat++;
				else
					// 2. An archetype-shared combo (`Soldier_`, `Rogue_`, `Priest_`) belongs to all three classes of
					//    that archetype, so it fans out where a class-named prefix names one. Those combos sit in the
					//    shared `20000`-`20008` block that reads as class 20 and is not one, so no class prefix and no
					//    stacking-line route could ever match them.
					classes = ConanArchetypes.FromNamePrefix(combo.Name) is { } archetype
						? ConanArchetypes.ClassesOf(archetype)
						: ClassComboPrefixes.Where(p => combo.Name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p.Class).ToArray();

				// The level is a second, separate question, and `Combos.Level <= 1` is the catalog's sentinel for
				// "not stated" rather than level one (docs/subsystems/combos.md). The gating feat answers that too
				// wherever every levelled combo it gates carries one level - which is exactly the archetype
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

				// A feat-derived answer replaces whatever a previous run's prefix guess wrote, so a narrowing actually
				// narrows. Only ever applied when the feat spoke - the prefix rule stays purely additive, because it
				// cannot tell "no opinion" from "no class", and a run against a DB whose Feats are not imported yet
				// would otherwise delete the whole map.
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

			// 3. Tree inheritance - the third source, and the weakest, which is why it runs last and only where the
			//    tree has already spoken for itself.
			var inherited = await InheritTreeClassesAsync(db, logger);

			logger?.LogInformation(
				"CaptureAbilityImporter: derived {Inserted} ClassCombo rows ({Updated} levels updated, {Removed} removed) - {FromFeat} combos attributed by their GATING FEAT, {LevelFromFeat} taking the feat's level over the catalog sentinel, {Inherited} from their own TREE; the rest from validated catalog prefixes.",
				inserted + inherited, updated, removed, fromFeat, levelFromFeat, inherited);
			return (inserted + inherited, updated);
		}

		/// <summary>
		///     Lends a feat tree's class to its unattributed members. A combo's feat tree is the leading segment of its
		///     catalog name (`Halberdier_20a_multihit_Flashing_Arc_II` is in the `Halberdier` tree) and a tree belongs
		///     to one class; where the tree's own members have already said which, by their gating feats, that answer
		///     applies to the rest of the tree.
		/// </summary>
		/// <remarks>
		///     The stragglers a tree resolves are the members whose gating feat names a weapon line rather than a class
		///     (`Pole Arm Combos 20-40 1`) or an ability (`Combo - Skewer`) - a gap in what the feat name happens to say,
		///     not evidence that the combo belongs elsewhere. Three guards, each removing a way this could attribute a
		///     combo wrongly:
		///     unanimity - a tree whose attributed members name more than one class lends nothing, which keeps the
		///     archetype trees (`Soldier_`, `Rogue_`) and the racial dance combos (`Cimmerian_`, all 12 classes) out of
		///     it, as they are correctly multi-class;
		///     an evidence floor - fewer than <see cref="MinTreeEvidence" /> attributed members lends nothing, so one
		///     stray row cannot define a tree;
		///     NPC/monster names are excluded outright, because their leading segment is not a tree at all
		///     (`combo_npc_guardian_polearm_1_sweep` splits to `combo`, which would collide with the `Combo_Charge_*`
		///     family).
		///     It is additive and never removes: like the prefix rule, it cannot tell "no opinion" from "no class".
		/// </remarks>
		private static async Task<int> InheritTreeClassesAsync(IDatabaseContext db, ILogger? logger)
		{
			var combos = await db.Combos.Where(c => c.Name != null).Select(c => new
			{
				c.Id,
				c.Name,
				c.Level
			}).ToListAsync();

			var attributed = await db.ClassCombos.Select(cc => new
			{
				cc.ComboId,
				cc.Class
			}).ToListAsync();

			var classesOf = attributed.GroupBy(x => x.ComboId).ToDictionary(g => g.Key, g => g.Select(x => x.Class).Distinct().ToArray());

			var inherited = 0;
			foreach (var tree in combos.Where(c => TreeOf(c.Name!) != null).GroupBy(c => TreeOf(c.Name!)!, StringComparer.OrdinalIgnoreCase))
			{
				var known = tree.Where(c => classesOf.ContainsKey(c.Id)).ToList();
				if (known.Count < MinTreeEvidence)
					continue;

				var classes = known.SelectMany(c => classesOf[c.Id]).Distinct().ToList();
				if (classes.Count != 1)
					continue;

				var cls = classes[0];
				foreach (var combo in tree.Where(c => !classesOf.ContainsKey(c.Id)))
				{
					db.ClassCombos.Add(new ClassCombo
					{
						Class = cls,
						ComboId = combo.Id,
						Level = combo.Level
					});
					inherited++;
					logger?.LogInformation("CaptureAbilityImporter: '{Combo}' inherits {Class} from its {Tree} tree (#581).", combo.Name, cls, tree.Key);
				}
			}

			if (inherited > 0)
				await db.SaveChangesAsync();

			return inherited;
		}

		// The combo's feat tree - the leading `_`-segment of its catalog name - or null when the name carries none, or
		// when it is an NPC/monster/generic combo whose leading segment is not a tree.
		private static string? TreeOf(string name)
		{
			foreach (var excluded in NonTreeComboPrefixes)
				if (name.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
					return null;

			var split = name.IndexOf('_');
			return split > 0 ? name[..split] : null;
		}

		/// <summary>
		///     Seeds the universal combo kit - the combos every created character is granted regardless of class - as a
		///     ClassCombo row per class (the combo twin of <see cref="DeriveClassAbilitiesFromCreatesAsync" />).
		///     Idempotent.
		/// </summary>
		/// <remarks>
		///     The prefix derivation cannot produce this: <see cref="DeriveClassCombosAsync" /> keys on the class-name
		///     prefixes, but the universal kit is named by race - `Cimmerian_Dance_combo1..3`, `Khitan_…`, `Stygian_…`,
		///     `Aquilonian_…`, the 4 playable races' dance emotes. They are racially named but universally granted: every
		///     captured character, whatever its race or class, receives all four races' dances, so the grant follows
		///     neither. That is also why this takes the intersection across create dumps where the ability kit takes the
		///     union: a create burst carries the class's own combos too, and a union would hand every class one class's
		///     combos.
		/// </remarks>
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

			// One dump cannot establish "universal" - it is indistinguishable from that class's own kit.
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

		/// <summary>
		///     Catalogs every ability's Id plus Slot/Data identity seen on the wire across all captures (the Data quad is
		///     also its RDB 1000020 TEA key, so RdbAbilityImporter can name it).
		/// </summary>
		/// <remarks>
		///     The class-to-ability access and the per-class unlock level are not set here - they are derived from the
		///     per-character grants (<see cref="DeriveClassAbilitiesAsync" />).
		/// </remarks>
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
						// Not named here: the names come from the client's own templates (`rdb-abilities` /
						// `rdb-ldb-abilities`). An ability left blank by a captures-only run is honestly unnamed rather
						// than mis-named.
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

		/// <summary>
		///     Seeds each captured character's granted ability set (CharacterAbility rows) from the 0x2052 grants, keyed
		///     by the grant's RpcHeader receiver instance (the character id) - the ability twin of CaptureSpellImporter's
		///     learned-spell seeding.
		/// </summary>
		/// <remarks>
		///     Abilities are not in the snapshot known-list, so this is the authoritative per-character set (the action bar
		///     is only shortcuts). Only characters already in the DB are touched; idempotent.
		/// </remarks>
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

		/// <summary>
		///     Seeds each captured character's combo book (CharacterCombo rows) from the ComboBookIIR grants (PassBlob
		///     carrier, sub-opcode 0xA8BBEB7F), keyed by the RpcHeader receiver instance (the character id).
		/// </summary>
		/// <remarks>
		///     Combos are class/level content the client's combo book gains per level; a max-level imported character
		///     never dings, so this persisted set is what GrantCharacterCombos re-emits on login. Only characters already
		///     in the DB are touched; idempotent.
		/// </remarks>
		public static async Task<(int Characters, int Inserted)> ImportCharacterCombosAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, true);

			// Second source: the character's own action bar. A ComboBookIIR only ever reaches a character during the
			// create flow / early levels, so it covers exactly the characters the level filter skips and none of the
			// imported ones. A saved bar names combos directly: a type-3 shortcut is a combo (see ShortcutBarReplyCodec).
			// It is a floor rather than the whole book - only what the player kept on the bar - but it is that
			// character's real, evidenced set, and GrantCharacterCombos unions it with the class/level map.
			//
			// Runs off CharacterShortcuts, so this module must run after character-state (declared in its DependsOn).
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

		/// <summary>
		///     Every packet of a create capture paired with the class that was selected when it arrived: the class is
		///     whatever the last ChangeClass callback selected, and packets before the first one have none.
		/// </summary>
		/// <remarks>
		///     Unlike <see cref="DetectClass" /> (the first ChangeClass only), this is correct when the player browsed the
		///     editor's class list: retail sends the clicked class's spell/combo book each time so the UI can show it, so
		///     one capture legitimately carries several classes' books, and filing them all under one class would give
		///     that class the others' kit.
		/// </remarks>
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

		// The distinct classes a create capture selected, in order of first appearance. More than one means the player
		// browsed the editor, and that a per-capture attribution would merge their books.
		internal static IReadOnlyList<uint> SelectedClasses(IReadOnlyList<byte[]> capture)
		{
			var seen = new List<uint>();
			foreach (var (cls, _) in PacketsWithSelectedClass(capture))
				if (cls is { } c && (seen.Count == 0 || seen[^1] != c) && !seen.Contains(c))
					seen.Add(c);

			return seen;
		}

		// How many combo grants one packet carries - the report's counter, so it reads the same reader the import does.
		internal static int CountComboGrants(byte[] packet)
		{
			var one = new Dictionary<uint, HashSet<uint>>();
			TryReadCharacterGrant(packet, one, true);
			return one.Values.Sum(x => x.Count);
		}

		/// <summary>
		///     The character class chosen in this capture, read from its first ChangeClass editor callback (the editor
		///     sends class as 2x the real id, see HandleCommandPacket). Null when the capture carries no ChangeClass
		///     (e.g. a login capture).
		/// </summary>
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

		// The DispatchAddSpecialAbility body, parsed by AddSpecialAbilityCodec (which validates the full shape).
		private static void TryReadAddSpecialAbility(byte[] packetBytes, IDictionary<uint, (uint, uint, uint, uint, uint, uint)> grants)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Method != AddSpecialAbility)
					return;

				var body = packet.BodyReader().ReadRemainingArray();

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
						// Field 4 = token name, field 5 = value string; only field 4 is the command token.
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

		// Reads one per-character grant into byCharacter, keyed by the RpcHeader receiver (the character id): an ability
		// grant (DispatchAddSpecialAbility) or a ComboBookIIR combo (PassBlob carrier), each read through its codec.
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
