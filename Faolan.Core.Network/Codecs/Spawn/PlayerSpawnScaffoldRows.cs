using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Maps the spawn scaffold between its wire form (PlayerSpawnScaffold) and the character's DB rows
	// (CharacterSkill / CharacterScaffoldPair / CharacterFeat, plus the appearance model CharacterBody +
	// CharacterBodyResources), so a player spawn can be GENERATED from the database instead of spliced into
	// captured template bytes. Buff/timer records are transient session state and are deliberately not stored - a
	// buffed captured spawn extracts to the same rows as its unbuffed form. Fresh characters get the
	// fresh-character defaults below (extracted from the Dikketest create capture - a just-created character at
	// world entry; the same role Strie's template played, but with a genuinely fresh scaffold).
	// See docs/re/player-spawn-generation.md "VALIDATED GRAMMAR".
	//
	// The ENTRY TABLE is DERIVED, never stored (#483). It used to be persisted as CharacterAttribute rows and kept
	// in step with the appearance model by explicit refresh calls on every edit path - two copies of one fact, and
	// forgetting one froze the avatar's face permanently. BuildAppearanceEntries is the single generator now:
	// weighted entries from CharacterBodyResources through the MorphTarget registry, morph entries from
	// CharacterBody, the closer from the class.
	public static class PlayerSpawnScaffoldRows
	{
		// The pre-IIR frame's grammar lives in PlayerSpawnPreFrame / PlayerSpawnPreFrameCodec - it is the client's
		// SimpleCharFullUpdateIIR scaffold, not a blob with islands, and the character's UNLOCKED FEAT list is the
		// nested count-framed list inside its sublist (see docs/subsystems/feats.md +
		// tools/capture/decode_unlocked_feats.py). Nothing here addresses the frame by offset any more.

		private static readonly Dictionary<ConanClass, uint[]> FreshSkillIdsByClass = new Dictionary<ConanClass, uint[]>
		{
			[ConanClass.Barbarian] = [1800, 6000, 6100, 6200, 6300, 6600, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 124400, 318400, 318800, 328700, 328900, 331300],
			[ConanClass.Conqueror] = [2200, 6000, 6100, 6200, 6300, 6700, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 54900, 318400, 318800, 328700, 328900, 331300],
			[ConanClass.Guardian] = [2000, 6000, 6200, 6500, 6700, 6800, 6900, 7100, 7500, 8100, 8305, 8400, 13600, 14600, 24700, 328700, 328900, 331300],
			[ConanClass.DarkTemplar] = [3100, 6000, 6200, 6700, 6800, 6900, 7100, 7500, 7800, 8100, 8305, 8400, 13600, 14600, 74600, 328700, 328900, 331300],
			[ConanClass.BearShaman] = [2900, 6300, 6600, 6700, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 244300, 328700, 328900, 331300],
			[ConanClass.PriestOfMitra] = [2400, 6000, 6200, 6400, 6700, 6800, 6900, 7100, 7500, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 198000, 328900, 331300],
			[ConanClass.TempestOfSet] = [2800, 6000, 6200, 6400, 6500, 6600, 6700, 6800, 6900, 7100, 7500, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 234600, 328900, 331300],
			[ConanClass.Assassin] = [3400, 6400, 6700, 6900, 8100, 8305, 8400, 13600, 14600, 160000, 318000, 328700, 328900, 331300],
			[ConanClass.Ranger] = [3900, 6000, 6200, 6400, 6600, 6700, 6900, 7100, 7500, 8100, 8305, 8400, 13600, 14600, 175000, 318000, 328700, 328900, 331300],
			[ConanClass.Necromancer] = [4100, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 85200, 328900, 331300],
			[ConanClass.Demonologist] = [4400, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 104500, 328900, 331300],
			[ConanClass.HeraldOfXotli] = [4300, 6100, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 13600, 14600, 114800, 328700, 328900, 331300]
		};

		// The FULL ability/skill scaffold a high-level character of the class carries, harvested from the captured
		// retail chars (NecroL80 L80 Necro, DemoL80 L80 Demo, AssassinL23 L23 Assassin, Hakotep L24 Herald). A spell's
		// in-client usability is gated on the char carrying the matching ABILITY/SKILL scaffold entry - and that
		// spell->ability mapping is server-side, so we replicate a working char's whole scaffold, which makes every
		// class spell the char has learned castable. NOTE: this scaffold is the ability/skill list, NOT the feat
		// tree - feats ride a SEPARATE record (FeatIIR 0x28da68a0, its own id-space; an earlier RE wrongly conflated
		// the two). Over-grants (no per-level breakdown), and only covers classes we have a high-level capture for.
		// See [[feat_trees_decode_251]] (feats-vs-skills correction) and Gitea #298/#251/#74. Ascending ids (the
		// wire/codec require it).
		private static readonly Dictionary<ConanClass, uint[]> FullSkillIdsByClass = new Dictionary<ConanClass, uint[]>
		{
			[ConanClass.Necromancer] =
			[
				4100, 7002, 8909, 13603, 18502, 85279, 86200, 86507, 299901, 301403, 302303, 302603, 303103, 304503, 304803, 304903, 305102, 305403, 305702, 306902, 307802, 308102, 308402, 313104,
				321202, 321502, 323205, 323305, 323405, 323505, 326000, 326300, 326600, 327402, 327505, 328908, 329400, 330400, 330500, 331300, 1440500, 1445000
			],
			[ConanClass.Demonologist] =
			[
				4400, 7002, 8909, 13603, 18502, 104579, 104600, 299901, 301403, 302303, 302603, 303103, 304503, 304803, 304903, 305102, 305403, 305702, 306902, 307802, 308102, 308402, 313704,
				321701, 322402, 323201, 323300, 323402, 323501, 326000, 326300, 326600, 327200, 327500, 328908, 329400, 330400, 330500, 331300, 1410900, 1411300, 1440000, 1440400, 1440500,
				1440700, 1441902, 1445000
			],
			[ConanClass.Assassin] =
			[
				3400, 13603, 14101, 14801, 15000, 16707, 160022, 160300, 165600, 301200, 302100, 302900, 304600, 305101, 305200, 305500, 307600, 307900, 308200, 309100, 318100, 323301, 328703,
				328902, 329302, 330400, 331300
			],
			[ConanClass.HeraldOfXotli] =
			[
				4300, 7700, 13603, 14101, 18502, 114823, 114900, 117801, 119000, 300300, 301200, 302100, 302400, 302900, 304300, 304600, 305101, 305200, 305500, 307600, 307900, 308200, 328703,
				328902, 330400, 331300
			]
		};

		// Per-class fresh feat/ability ID list (all rank 0). The FIRST id is the PROFESSION feat and equals
		// (class enum) * 100 (1800=Barbarian, 2000=Guardian, 2900=BearShaman, 3400=Assassin, 4100=Necromancer,
		// 2200=Conqueror, 4300=HeraldOfXotli, 4400=Demonologist...), matching the retail self-spawns (Captures/
		// scripts: profession feat = class*100, always first/ascending). The four captured-at-L1 archetypes use
		// their exact retail fresh list; the rest fall back to the profession feat + the base feats common to every
		// captured fresh list. NOTE: the in-world *displayed class* is NOT derived from this feat tree - it reads
		// player stat 0x43 (see PostReadyFollowUp, #239). The earlier "client derives the
		// profession from the feat tree" claim was DISPROVEN in-client (a spawn carrying feat 4100 still showed
		// Barbarian); this list still matters for the actual feat/AA content, just not the class label.
		private static readonly uint[] CommonFreshSkillIds = [6900, 8100, 8305, 8400, 13600, 14600, 328900, 331300];

		// Builds the weighted (1.0) appearance entries from the character's chosen editor resources, resolved
		// against the imported client registry (MorphTarget; wire tag = registry id - 1). Wire order = editor
		// send order (Sequence).
		//
		// A character wears the resources it chose or the build FAILS - there is no substitute face. This used to
		// return null (registry missing, no ref resolved, or nothing but category 2) and the caller then spliced in
		// DIKKETEST's captured 6 attributes, which is a wrong hair plus two face-marking texscripts presented as the
		// player's own choices - a wrong appearance that renders as if it worked. The two null cases it collapsed are
		// different problems and both are now loud: a missing registry is seed corruption (MorphTargets is 1072 rows
		// of static seed content that every spawn resolves through), and a ref the registry does not know is content
		// the importer never took in.
		//
		// The ONE thing still skipped is category 2 (face paint, e.g. the always-sent *texscript_183875): captured
		// spawns never carry it, so emitting it would be inventing wire. That is proven grammar, not a fallback - a
		// character whose only choice is category 2 legitimately builds an EMPTY entry list.
		public static List<ScaffoldEntry> BuildWeightedEntries(IEnumerable<CharacterBodyResource>? resources, IReadOnlyDictionary<string, MorphTarget> registry)
		{
			ArgumentNullException.ThrowIfNull(registry);

			var entries = new List<ScaffoldEntry>();
			if (resources == null)
				return entries;

			var ordered = resources.OrderBy(r => r.Sequence).ToList();

			var unresolved = ordered.Where(r => !registry.ContainsKey(r.Ref)).Select(r => r.Ref).ToArray();
			if (unresolved.Length > 0)
			{
				throw new InvalidOperationException(
					$"Character {ordered[0].CharacterId:X8} chose appearance resource(s) {string.Join(", ", unresolved)}, which are not in MorphTargets - its own looks cannot be built. Import them (Faolan.Importer --only rdb-morph-targets).");
			}

			foreach (var resource in ordered)
			{
				var target = registry[resource.Ref];
				if (target.Category == 2)
					continue;

				entries.Add(new ScaffoldEntry((uint)entries.Count + 1, (ushort)target.Id, BitConverter.SingleToUInt32Bits(target.Weight)));
			}

			return entries;
		}

		// The scaffold's ENTRY TABLE, built WHOLE from the character's appearance model - the one generator, used by
		// every spawn. In wire order: the weighted appearance entries (the character's chosen editor resources,
		// resolved through the registry by BuildWeightedEntries - EMPTY when it chose none, never somebody else's
		// face), then the character's OWN editor morphs (CharacterMorphs - absent sliders yield no entry, the captured shape of an
		// unmorphed character), then the closing record. Indices are 1..n in that order.
		//
		// NB retail's captured indices are NOT always 1..n: they are per-character SLOT ids that survive appearance
		// edits, so a re-barbered character carries gaps and appends its new hair/beard after its morphs
		// (SpawnNecroKingsCastle starts at 2, SpawnDemoRandomActions runs 4..13,24,23). Nothing derives them, and
		// nothing needs to - every character we create spawns with 1..n and renders correctly in-client.
		public static List<ScaffoldEntry> BuildAppearanceEntries(Character character, IReadOnlyList<ScaffoldEntry> weightedAppearance)
		{
			ArgumentNullException.ThrowIfNull(weightedAppearance);

			var entries = new List<ScaffoldEntry>(weightedAppearance);
			entries.AddRange(CharacterMorphs.BuildEntries(character.Body, (uint)entries.Count + 1));

			var closer = FreshCloser(character.Class);
			entries.Add(new ScaffoldEntry((uint)entries.Count + 1, closer.Tag, closer.Value));
			return entries;
		}

		// Seeds the fresh-character scaffold defaults onto a character that has none, IN PLACE, and says whether it did
		// - false means it already had rows and nothing was touched, so the caller can skip the save. Try-named because
		// that "no" is an ordinary outcome (every login after the first), not a failure.
		//
		// The APPEARANCE is not seeded: it is derived from CharacterBody / CharacterBodyResources at spawn time
		// (BuildAppearanceEntries). What is left is the progression the character starts with, and the "already
		// seeded" marker is therefore the SKILL rows - seeded here and nowhere else.
		public static bool TrySeedFreshRows(Character character)
		{
			if (character.Skills is { Count: > 0 })
				return false;

			character.Skills = FreshSkillIdsFor(character.Class)
				.Select(id => new CharacterSkill
				{
					CharacterId = character.Id,
					SkillId = id
				})
				.ToList();
			character.ScaffoldPairs = [];

			// A fresh character has trained no feats - but it DOES have the one unnamed spec every character carries,
			// which is what the frame's sublist entry is. Seeding it is REQUIRED, not cosmetic: SpecsOf throws on a
			// character with no spec rows (it cannot tell "never trained" from "loaded without Include(FeatSpecs)"),
			// so a character that reaches a spawn without one cannot be built at all. It must also be PERSISTED -
			// see the ensure-specs block in DatabaseRepository.SaveCharacterScaffold, which is what writes it.
			character.Feats = [];
			character.FeatSpecs =
			[
				new CharacterFeatSpec
				{
					CharacterId = character.Id,
					Index = 0,
					Name = ""
				}
			];
			return true;
		}

		// The unlocked-feat id list the frame carries, flattened across its specs - the inverse of FreshPreFrame's
		// feat splice. Empty when the frame has no sublist (the create-editor variant). Order is preserved: the wire
		// order is TRAIN order, not sorted.
		//
		// The frame arrives typed now, so there is nothing to parse here and nothing to fail. It used to take the
		// frame's BYTES and re-walk them - the same walk PlayerSpawnScaffoldCodec had already done to produce them.
		public static List<uint> ExtractPreFrameFeats(PlayerSpawnPreFrame frame)
		{
			return frame.Sublist.SelectMany(spec => spec.FeatIds).ToList();
		}

		// Replaces the character's scaffold rows with the scaffold's content (extraction: captured spawn -> rows), and
		// says how much of the entry table landed in the appearance model.
		//
		// `refsByTag` is the MorphTarget registry keyed by WIRE TAG (= MorphTarget.Id), i.e. the inverse of the map
		// BuildWeightedEntries generates through: tag 133 comes back as "*beardmesh_210844", 226 as
		// "*texscript_202392". It is REQUIRED rather than optional because the entry table is the only place a
		// captured character's looks exist - extracting without it would silently import a faceless character.
		public static (int Morphs, int Resources) ExtractTo(Character character, PlayerSpawnScaffold scaffold, IReadOnlyDictionary<ushort, string> refsByTag)
		{
			ArgumentNullException.ThrowIfNull(refsByTag);

			character.Skills = scaffold
				.IdList.Select(s => new CharacterSkill
				{
					CharacterId = character.Id,
					SkillId = s.Id,
					Rank = s.Rank
				})
				.ToList();

			character.ScaffoldPairs = scaffold.EndPairs.Select(p => Pair(character.Id, 0, p)).Concat(scaffold.EndPairs2.Select(p => Pair(character.Id, 1, p))).ToList();

			// The unlocked FEAT list lives in the pre-IIR frame (not the post-IIR IdList, which is skills). It IS
			// character data, so extract it to CharacterFeat rows (in wire/train order); the rest of the frame +
			// EntryTableX are constants reconstructed from Character.Class by TryApplyFrom (see FreshPreFrame).
			// Specs are preserved rather than flattened (#474): a character with four saved specs round-trips as
			// four CharacterFeatSpec rows, and the same feat id may legitimately appear in several of them.
			character.FeatSpecs = scaffold.Frame.Sublist
				.Select((spec, index) => new CharacterFeatSpec
				{
					CharacterId = character.Id,
					Index = index,
					Name = spec.Name
				})
				.ToList();
			character.Feats = scaffold.Frame.Sublist
				.SelectMany((spec, index) => spec.FeatIds.Select((id, sequence) => new CharacterFeat
				{
					CharacterId = character.Id,
					SpecIndex = index,
					FeatId = id,
					Sequence = sequence
				}))
				.ToList();

			return ExtractAppearance(character, scaffold, refsByTag);
		}

		// The entry table read back into the APPEARANCE MODEL - the inverse of BuildAppearanceEntries, and the reason
		// a captured character's looks survive without a stored copy of the table.
		//
		//   - morph entries  -> CharacterBody columns (CharacterMorphs.ApplyEntries, pure arithmetic),
		//   - weighted rows  -> CharacterBodyResources, by looking the wire tag back up as a resource ref,
		//   - the closer     -> dropped; it is derived from the class (FreshCloser).
		//
		// A morph slider is never mistaken for a resource: the two share a tag SPACE (the registry carries entries
		// literally named "arm"/"chest"/"bosom" at ids 256..258, which are also the type-1 slider tags), so the
		// discriminator is CharacterMorphs.IsMorphTag - the whole (type, id) pair - and it is checked FIRST.
		private static (int Morphs, int Resources) ExtractAppearance(Character character, PlayerSpawnScaffold scaffold, IReadOnlyDictionary<ushort, string> refsByTag)
		{
			character.Body ??= new CharacterBody
			{
				CharacterId = character.Id
			};

			var morphs = CharacterMorphs.ApplyEntries(character.Body, scaffold.Entries.Select(e => (e.Tag, e.Value)));

			// Everything but the closer, which is the LAST record and carries a count marker, not appearance.
			character.BodyResources.Clear();
			foreach (var entry in scaffold.Entries.Take(Math.Max(0, scaffold.Entries.Count - 1)))
			{
				if (CharacterMorphs.IsMorphTag(entry.Tag) || !refsByTag.TryGetValue(entry.Tag, out var resourceRef))
					continue;

				character.BodyResources.Add(new CharacterBodyResource
				{
					CharacterId = character.Id,
					Sequence = character.BodyResources.Count,
					Ref = resourceRef
				});
			}

			return (morphs, character.BodyResources.Count);
		}

		// The character's specs as the wire wants them: each spec's feats in Sequence order, specs in Index order.
		//
		// THROWS when the character has none. Every character has at least the one unnamed spec - the wire's sublist
		// always carries it, TrySeedFreshRows writes it, and the #474 migration backfilled it for every existing row -
		// so an empty set is a broken character, not a shape to paper over. Synthesizing one here would let a
		// character whose spec rows failed to load spawn with an EMPTY feat list, which renders as "you have trained
		// nothing" and is indistinguishable from the real thing.
		public static IReadOnlyList<SpawnPreFrameSublistEntry> SpecsOf(Character character)
		{
			if (character?.FeatSpecs is not { Count: > 0 })
				throw new InvalidOperationException($"character {character?.Id} has no CharacterFeatSpec rows - every character carries at least the one unnamed spec, so this row set is incomplete (was it loaded without Include(c => c.FeatSpecs)?).");

			// Not `Feats ?? []`: the collection is initialised on the entity, so a null one means the graph is broken,
			// and coalescing it would spawn the character carrying no feats at all.
			var specs = character.FeatSpecs.OrderBy(s => s.Index).Select(s => (s.Index, s.Name)).ToList();

			return specs
				.Select(spec =>
				{
					var entry = new SpawnPreFrameSublistEntry
					{
						Key = (uint)spec.Index,
						Flag = (byte)spec.Index,
						Name = spec.Name
					};
					entry.FeatIds.AddRange(character.Feats.Where(f => f.SpecIndex == spec.Index).OrderBy(f => f.Sequence).Select(f => f.FeatId));
					return entry;
				})
				.ToList();
		}

		// The single-spec overload - the shape every fresh character and every shipped character has.
		public static PlayerSpawnPreFrame FreshPreFrame(ConanClass @class, uint vanityShownMask = 0, IReadOnlyList<uint>? feats = null)
		{
			var entry = new SpawnPreFrameSublistEntry();
			if (feats != null)
				entry.FeatIds.AddRange(feats);

			return FreshPreFrame(@class, vanityShownMask, [entry]);
		}

		public static PlayerSpawnPreFrame FreshPreFrame(ConanClass @class, uint vanityShownMask, IReadOnlyList<SpawnPreFrameSublistEntry> specs)
		{
			var frame = new PlayerSpawnPreFrame();
			frame.Flags[PlayerSpawnPreFrame.ArchetypeFlagIndex] = FrameArchetype(@class);
			frame.Flags[PlayerSpawnPreFrame.ClassFlagIndex] = (byte)@class;
			frame.Words[PlayerSpawnPreFrame.VanityShownMaskWordIndex] = vanityShownMask; // #336
			frame.Words[PlayerSpawnPreFrame.CategoryWordIndex] = NpcSpawnPreIir.DefaultCategory;
			frame.Words[PlayerSpawnPreFrame.ConstantWordIndex] = PlayerSpawnPreFrame.ConstantWordValue;
			frame.Words[PlayerSpawnPreFrame.SessionWordIndex] = PlayerSpawnPreFrame.FreshSessionWordValue;

			// One sublist entry per saved feat spec, each carrying its name and its own trained-feat list in train
			// order. A fresh character has one unnamed spec with no feats, which frames as the bare 0x3F1 word -
			// byte-identical to the old constant-239 fresh frame.
			frame.Sublist.AddRange(specs);
			return frame;
		}

		// The fresh feat list for a class: the captured retail list when we have one, else the profession feat
		// (class*100) prepended to the common base feats. Always ascending (the wire/codec require it).
		public static uint[] FreshSkillIdsFor(ConanClass @class)
		{
			if (FreshSkillIdsByClass.TryGetValue(@class, out var ids))
				return ids;

			return new[] { (uint)@class * 100 }.Concat(CommonFreshSkillIds).OrderBy(id => id).ToArray();
		}

		// The full high-level feat scaffold for a class (harvested from a captured retail char), or null when we have
		// no high-level capture for that class. Used on level-up to make the class's learned spells castable (the
		// feat-presence gate; see FullSkillIdsByClass). Ascending ids.
		public static uint[]? FullSkillIdsFor(ConanClass @class)
		{
			return FullSkillIdsByClass.TryGetValue(@class, out var ids)
				? ids
				: null;
		}

		// The vanity shown-mask carried by a captured spawn's frame. The write side is FreshPreFrame; this is the
		// read side, so the importer recovers a captured character's setting instead of defaulting it to 0.
		public static uint ReadVanityShownMask(PlayerSpawnScaffold scaffold)
		{
			return scaffold.Frame.Words[PlayerSpawnPreFrame.VanityShownMaskWordIndex];
		}

		// Fills the scaffold's per-character parts from the character (generation: model -> wire). Returns false when
		// the character was never seeded, i.e. has no scaffold rows to describe it with.
		//
		// That "never seeded" test is now the SKILL rows. It used to be the entry table, which no longer exists as
		// state; skills are the honest replacement because TrySeedFreshRows writes them and nothing else does, and
		// every captured character extracts a full id list. The APPEARANCE deliberately does not gate anything - a
		// character with no editor choices legitimately wears the default face.
		//
		// `registry` is the MorphTarget catalog by ref name; it resolves the character's editor choices into the
		// weighted entries. A ref it does not know THROWS rather than being dropped or swapped for a default face
		// (BuildWeightedEntries) - the create flow says the same thing once, at creation, where it is still
		// actionable (CreateFlowService.ReportUnresolvableAppearance).
		public static bool TryApplyFrom(Character character, PlayerSpawnScaffold scaffold, IReadOnlyDictionary<string, MorphTarget> registry)
		{
			if (character?.Skills is not { Count: > 0 })
				return false;

			// The frame + EntryTableX are CONSTRUCTED, not stored: FreshPreFrame(class, vanityMask, feats) is the
			// constant skeleton with the archetype/class island written from the character's class, the vanity
			// hidden-slots mask (#336) from the character's equipment (the FA015807 self-spawn carries it so a hidden
			// piece survives relog), and the unlocked FEAT list spliced in from CharacterFeat rows (train order).
			// EntryTableX is the constant 0xB7. Transient session islands are correctly absent.
			scaffold.Frame = FreshPreFrame(character.Class, character.Equipment?.VanityShownMask ?? 0, SpecsOf(character));
			scaffold.EntryTableX = 0xB7;
			scaffold.BuffRecords.Clear();

			scaffold.Entries.Clear();
			scaffold.Entries.AddRange(BuildAppearanceEntries(character, BuildWeightedEntries(character.BodyResources, registry)));

			scaffold.IdList.Clear();
			if (character.Skills != null)
				scaffold.IdList.AddRange(character.Skills.OrderBy(s => s.SkillId).Select(s => new ScaffoldIdEntry(s.Rank, s.SkillId)));

			// The closer (last entry-table record) carries the COUNT MARKER the client uses to read the feat list:
			// TAG = (IdList.Count + 2) * 0x3F1 (see FreshCloser). It was stored at the FRESH skill count, so once the
			// class feat scaffold is granted on level-up (#298) the skill count grows and the stored tag goes stale ->
			// the client miscounts the feat list and CoreCharacterFullUpdateIIR asserts (a_cBlob.HasFailed). Recompute
			// it from the ACTUAL IdList here so the wire is always self-consistent (value/archetype is left intact).
			if (scaffold.Entries.Count > 0)
			{
				scaffold.Entries[^1] = scaffold.Entries[^1] with
				{
					Tag = (ushort)ListFrame.ForCount(scaffold.IdList.Count + 1) // frames ONE MORE than the id list holds; truncated to u16
				};
			}

			scaffold.EndPairs.Clear();
			scaffold.EndPairs2.Clear();
			if (character.ScaffoldPairs != null)
			{
				scaffold.EndPairs.AddRange(character.ScaffoldPairs.Where(p => p.ListIndex == 0).OrderBy(p => p.PairId).Select(p => new ScaffoldPair(p.PairId, p.Value)));
				scaffold.EndPairs2.AddRange(character.ScaffoldPairs.Where(p => p.ListIndex == 1).OrderBy(p => p.PairId).Select(p => new ScaffoldPair(p.PairId, p.Value)));
			}

			return true;
		}

		// The frame's archetype code (scaffold flag 1, abs @135): Soldier=1, Priest=2, Rogue=3, Mage=4. This is a
		// DIFFERENT numbering than the ArchType enum, recovered from the captured player spawns (each class maps to
		// exactly one value across all its sessions - the only genuine per-character field in the whole pre-IIR frame;
		// everything else that varies is the RDB script-engine char buffer, transient/zero for a player). See
		// docs/re/fa015807-spawn-reader.md (FUN_01121c00) + docs/re/player-spawn-generation.md.
		private static byte FrameArchetype(ConanClass @class)
		{
			return @class switch
			{
				ConanClass.Conqueror or ConanClass.DarkTemplar or ConanClass.Guardian => 1, // Soldier
				ConanClass.PriestOfMitra or ConanClass.TempestOfSet or ConanClass.BearShaman => 2, // Priest
				ConanClass.Barbarian or ConanClass.Ranger or ConanClass.Assassin => 3, // Rogue
				ConanClass.HeraldOfXotli or ConanClass.Demonologist or ConanClass.Necromancer => 4, // Mage
				_ => 3
			};
		}

		// The closing [u16 tag][u32 value] entry-table record (the final entry-table entry). #250: stamping the WRONG
		// closer crashes the client's character construction at world entry. DECODED (#269, 117 player self-spawns
		// across every capture, zero violations - parse_spawn_scaffold.py): the closer is fully DERIVED, not a
		// per-class constant.
		//   VALUE = archetype index x 100 (Soldier=100, Priest=200, Rogue=300, Mage=400).
		//   TAG   = (feat/skill IdList length + 2) x 0x3F1.
		// So the tag is a COUNT marker the client uses to read the feat list - which is exactly why a mismatched one
		// crashes. It is race-independent (same class -> same fresh feat list -> same count -> same tag) and holds
		// for feat counts from 14 (Assassin -> 0x3F10) up to 47 (leveled chars). The old per-class hardcoded tags
		// happened to match only because each class's feat-list length is fixed; computing it is correct for every
		// feat list (incl. Assassin, whose interim 0x56B6 guess was wrong - 14 feats -> 0x3F10).
		private static (ushort Tag, uint Value) FreshCloser(ConanClass @class)
		{
			var value = @class switch
			{
				ConanClass.Guardian or ConanClass.Conqueror or ConanClass.DarkTemplar => 100u, // Soldier
				ConanClass.PriestOfMitra or ConanClass.TempestOfSet or ConanClass.BearShaman => 200u, // Priest
				ConanClass.Barbarian or ConanClass.Assassin or ConanClass.Ranger => 300u, // Rogue
				_ => 400u // Mage
			};

			var tag = (ushort)ListFrame.ForCount(FreshSkillIdsFor(@class).Length + 1); // as above: one more than the ids
			return (tag, value);
		}

		private static CharacterScaffoldPair Pair(uint characterId, byte listIndex, ScaffoldPair p)
		{
			return new CharacterScaffoldPair
			{
				CharacterId = characterId,
				ListIndex = listIndex,
				PairId = p.Id,
				Value = p.Value
			};
		}
	}
}
