using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Maps the spawn scaffold between its wire form (PlayerSpawnScaffold) and the character's DB rows
	// (CharacterAttribute / CharacterSkill / CharacterScaffoldPair), so a player spawn can
	// be GENERATED from the database instead of spliced into captured template bytes. Buff/timer records are
	// transient session state and are deliberately not stored - a buffed captured spawn extracts to the same rows
	// as its unbuffed form. Fresh characters get the fresh-character defaults below (extracted from the Dikketest
	// create capture - a just-created character at world entry; the same role Strie's template played, but with a
	// genuinely fresh scaffold). See docs/re/player-spawn-generation-plan.md "VALIDATED GRAMMAR".
	public static class PlayerSpawnScaffoldRows
	{
		private static readonly Dictionary<Class, uint[]> FreshSkillIdsByClass = new Dictionary<Class, uint[]>
		{
			[Class.Barbarian] = [1800, 6000, 6100, 6200, 6300, 6600, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 124400, 318400, 318800, 328700, 328900, 331300],
			[Class.Conqueror] = [2200, 6000, 6100, 6200, 6300, 6700, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 54900, 318400, 318800, 328700, 328900, 331300],
			[Class.Guardian] = [2000, 6000, 6200, 6500, 6700, 6800, 6900, 7100, 7500, 8100, 8305, 8400, 13600, 14600, 24700, 328700, 328900, 331300],
			[Class.DarkTemplar] = [3100, 6000, 6200, 6700, 6800, 6900, 7100, 7500, 7800, 8100, 8305, 8400, 13600, 14600, 74600, 328700, 328900, 331300],
			[Class.BearShaman] = [2900, 6300, 6600, 6700, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 244300, 328700, 328900, 331300],
			[Class.PriestOfMitra] = [2400, 6000, 6200, 6400, 6700, 6800, 6900, 7100, 7500, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 198000, 328900, 331300],
			[Class.TempestOfSet] = [2800, 6000, 6200, 6400, 6500, 6600, 6700, 6800, 6900, 7100, 7500, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 234600, 328900, 331300],
			[Class.Assassin] = [3400, 6400, 6700, 6900, 8100, 8305, 8400, 13600, 14600, 160000, 318000, 328700, 328900, 331300],
			[Class.Ranger] = [3900, 6000, 6200, 6400, 6600, 6700, 6900, 7100, 7500, 8100, 8305, 8400, 13600, 14600, 175000, 318000, 328700, 328900, 331300],
			[Class.Necromancer] = [4100, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 85200, 328900, 331300],
			[Class.Demonologist] = [4400, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 104500, 328900, 331300],
			[Class.HeraldOfXotli] = [4300, 6100, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 13600, 14600, 114800, 328700, 328900, 331300]
		};

		// The FULL feat/ability scaffold a high-level character of the class carries, harvested from the captured
		// retail chars (NecroL80 L80 Necro, DemoL80 L80 Demo, AssassinL23 L23 Assassin, Hakotep L24 Herald). A spell's
		// in-client usability is gated on the char having the feat that unlocks it - and that spell->feat mapping
		// lives ONLY in the server GDB (never transmitted; the client just receives the resulting feat-id LIST). So
		// we can't grant the ONE exact feat per spell; instead we replicate a working char's whole scaffold, which
		// makes every class spell the char has learned castable. Over-grants feats (no per-level GDB breakdown), and
		// only covers the classes we have a high-level capture for. See [[feat_trees_decode_251]] (GDB-mapping-
		// blocked, NOT RDB-key-blocked) and Gitea #298/#251/#74. Ascending ids (the wire/codec require it).
		private static readonly Dictionary<Class, uint[]> FullSkillIdsByClass = new Dictionary<Class, uint[]>
		{
			[Class.Necromancer] =
			[
				4100, 7002, 8909, 13603, 18502, 85279, 86200, 86507, 299901, 301403, 302303, 302603, 303103, 304503, 304803, 304903, 305102, 305403, 305702, 306902, 307802, 308102, 308402, 313104,
				321202, 321502, 323205, 323305, 323405, 323505, 326000, 326300, 326600, 327402, 327505, 328908, 329400, 330400, 330500, 331300, 1440500, 1445000
			],
			[Class.Demonologist] =
			[
				4400, 7002, 8909, 13603, 18502, 104579, 104600, 299901, 301403, 302303, 302603, 303103, 304503, 304803, 304903, 305102, 305403, 305702, 306902, 307802, 308102, 308402, 313704,
				321701, 322402, 323201, 323300, 323402, 323501, 326000, 326300, 326600, 327200, 327500, 328908, 329400, 330400, 330500, 331300, 1410900, 1411300, 1440000, 1440400, 1440500,
				1440700, 1441902, 1445000
			],
			[Class.Assassin] =
			[
				3400, 13603, 14101, 14801, 15000, 16707, 160022, 160300, 165600, 301200, 302100, 302900, 304600, 305101, 305200, 305500, 307600, 307900, 308200, 309100, 318100, 323301, 328703,
				328902, 329302, 330400, 331300
			],
			[Class.HeraldOfXotli] =
			[
				4300, 7700, 13603, 14101, 18502, 114823, 114900, 117801, 119000, 300300, 301200, 302100, 302400, 302900, 304300, 304600, 305101, 305200, 305500, 307600, 307900, 308200, 328703,
				328902, 330400, 331300
			]
		};

		// Dikketest's 6 weighted attributes (weight 1.0). The tags are per-character (likely the chosen
		// race/class/head presets - mapping not yet cracked), so fresh characters keep Dikketest's proven set.
		// The character's morph entries are generated from its CharacterBody (see CharacterMorphs), not stored here.
		private static readonly ScaffoldEntry[] FreshWeightedAttributes =
		[
			new ScaffoldEntry(1, 0x00B0, 0x3F800000), new ScaffoldEntry(2, 0x0147, 0x3F800000), new ScaffoldEntry(3, 0x0154, 0x3F800000), new ScaffoldEntry(4, 0x0191, 0x3F800000),
			new ScaffoldEntry(5, 0x010B, 0x3F800000), new ScaffoldEntry(6, 0x01DE, 0x3F800000)
		];

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
		// send order (Sequence). Category 2 (face paint, e.g. the always-sent *texscript_183875) is never
		// emitted in captured spawns and is skipped; unresolvable refs are skipped too. Returns null when
		// nothing resolves - the caller then falls back to the Dikketest defaults.
		public static List<ScaffoldEntry> BuildWeightedEntries(IEnumerable<CharacterBodyResource> resources, IReadOnlyDictionary<string, MorphTarget> registry)
		{
			if (resources == null || registry == null)
				return null;

			var entries = new List<ScaffoldEntry>();
			foreach (var resource in resources.OrderBy(r => r.Sequence))
			{
				if (!registry.TryGetValue(resource.Ref, out var target) || target.Category == 2)
					continue;

				entries.Add(new ScaffoldEntry((uint)entries.Count + 1, (ushort)target.Id, BitConverter.SingleToUInt32Bits(target.Weight)));
			}

			return entries.Count > 0
				? entries
				: null;
		}

		// Seeds a character that has no scaffold rows yet with the fresh-character defaults. Returns true when it
		// seeded (caller persists). weightedAppearance (from BuildWeightedEntries) replaces the Dikketest
		// weighted-attribute defaults when the character's own editor choices are resolvable.
		public static bool EnsureFreshRows(Character character, IReadOnlyList<ScaffoldEntry> weightedAppearance = null)
		{
			if (character.Attributes is { Count: > 0 })
				return false;

			// Entry table: the weighted appearance entries (the character's chosen resources, or Dikketest's
			// proven set when none resolve), then the character's OWN editor morphs (CharacterMorphs - absent
			// sliders yield no entry, the captured shape of an unmorphed character), then the closing record.
			// Indices and Sequence both follow that wire order.
			var entries = new List<ScaffoldEntry>(weightedAppearance ?? FreshWeightedAttributes);
			entries.AddRange(CharacterMorphs.BuildEntries(character.Body, (uint)entries.Count + 1));
			var closer = FreshCloser(character.Class);
			entries.Add(new ScaffoldEntry((uint)entries.Count + 1, closer.Tag, closer.Value));

			character.Attributes = entries
				.Select((e, sequence) => new CharacterAttribute
				{
					CharacterId = character.Id,
					Sequence = sequence,
					EntryIndex = e.Index,
					Tag = e.Tag,
					Value = e.Value
				})
				.ToList();
			character.Skills = FreshSkillIdsFor(character.Class)
				.Select(id => new CharacterSkill
				{
					CharacterId = character.Id,
					SkillId = id
				})
				.ToList();
			character.ScaffoldPairs = new List<CharacterScaffoldPair>();
			return true;
		}

		// Replaces the character's scaffold rows with the scaffold's content (extraction: captured spawn -> rows).
		public static void ExtractTo(Character character, PlayerSpawnScaffold scaffold)
		{
			character.Attributes = scaffold
				.Entries.Select((e, sequence) => new CharacterAttribute
				{
					CharacterId = character.Id,
					Sequence = sequence,
					EntryIndex = e.Index,
					Tag = e.Tag,
					Value = e.Value
				})
				.ToList();

			character.Skills = scaffold
				.IdList.Select(s => new CharacterSkill
				{
					CharacterId = character.Id,
					SkillId = s.Id,
					Rank = s.Rank
				})
				.ToList();

			character.ScaffoldPairs = scaffold.EndPairs.Select(p => Pair(character.Id, 0, p)).Concat(scaffold.EndPairs2.Select(p => Pair(character.Id, 1, p))).ToList();
			// The pre-IIR frame + EntryTableX are NOT extracted: they're not character data (the frame is a constant
			// skeleton + the archetype/class island + the transient RDB script char buffer; EntryTableX is const 0xB7).
			// TryApplyFrom reconstructs them from Character.Class - see FreshPreFrame + docs/re/fa015807-spawn-reader.md.
		}

		// Offset of the vanity hidden-slots bitmask within the 239-byte PreFrame (4 bytes, big-endian; bit = base equip
		// slot id). 0 = nothing hidden (the fresh default); the FA015807 self-spawn carries it so a hidden piece
		// survives relog - the client renders the show/hide off it (#336).
		public const int VanityHiddenMaskOffset = 18;

		public static byte[] FreshPreFrame(Class @class, uint vanityHiddenMask = 0)
		{
			var f = new byte[239];
			f[0] = 0x05;
			BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(VanityHiddenMaskOffset, 4), vanityHiddenMask); // vanity hide mask (#336)
			f[70] = FrameArchetype(@class); // @135 hi byte = archetype (Soldier1/Priest2/Rogue3/Mage4)
			f[71] = (byte)@class; // @136 = the class byte (0x12 Barbarian .. 0x2c Demonologist)
			f[76] = 0x6F; // constant marker
			f[81] = 0x41; // constant marker 0x4170
			f[82] = 0x70;
			f[94] = 0x08; // u16 island @93 = 0x0008 (the one byte that differed between fresh captures)
			f[113] = 0x07; // constant 0x7E2 (2018)
			f[114] = 0xE2;
			f[124] = 0x03; // constant 0x3F1
			f[125] = 0xF1;
			f[234] = 0x41; // closer 41 00 00 82 08
			f[237] = 0x82;
			f[238] = 0x08;
			return f;
		}

		// The fresh feat list for a class: the captured retail list when we have one, else the profession feat
		// (class*100) prepended to the common base feats. Always ascending (the wire/codec require it).
		public static uint[] FreshSkillIdsFor(Class @class)
		{
			if (FreshSkillIdsByClass.TryGetValue(@class, out var ids))
				return ids;

			return new[] { (uint)@class * 100 }.Concat(CommonFreshSkillIds).OrderBy(id => id).ToArray();
		}

		// The full high-level feat scaffold for a class (harvested from a captured retail char), or null when we have
		// no high-level capture for that class. Used on level-up to make the class's learned spells castable (the
		// feat-presence gate; see FullSkillIdsByClass). Ascending ids.
		public static uint[] FullSkillIdsFor(Class @class)
		{
			return FullSkillIdsByClass.TryGetValue(@class, out var ids)
				? ids
				: null;
		}

		// Rebuilds ONLY the appearance portion of an EXISTING scaffold (the weighted appearance entries + the editor
		// morphs + the closer) from the character's current CharacterBody / CharacterBodyResources, leaving skills,
		// spawn-frame and scaffold-pairs intact. This propagates a later appearance edit (e.g. the Faolan.Web editor,
		// which rewrites Body/BodyResources) into the spawn the game sends on the next login, WITHOUT resetting the
		// character's progression the way a full EnsureFreshRows would. Returns false when there's no scaffold to
		// refresh (the caller then seeds a fresh one). For an unedited character it reproduces the same entries it was
		// seeded with, so it is a no-op in effect.
		public static bool RefreshAppearance(Character character, IReadOnlyList<ScaffoldEntry> weightedAppearance = null)
		{
			if (character?.Attributes is not { Count: > 0 })
				return false;

			var entries = new List<ScaffoldEntry>(weightedAppearance ?? FreshWeightedAttributes);
			entries.AddRange(CharacterMorphs.BuildEntries(character.Body, (uint)entries.Count + 1));
			var closer = FreshCloser(character.Class);
			entries.Add(new ScaffoldEntry((uint)entries.Count + 1, closer.Tag, closer.Value));

			character.Attributes = entries
				.Select((e, sequence) => new CharacterAttribute
				{
					CharacterId = character.Id,
					Sequence = sequence,
					EntryIndex = e.Index,
					Tag = e.Tag,
					Value = e.Value
				})
				.ToList();
			return true;
		}

		// Fills the scaffold's per-character parts from the rows (generation: rows -> wire). Returns false when
		// the character has no scaffold rows yet (caller falls back to the captured-spawn splice path).
		public static bool TryApplyFrom(Character character, PlayerSpawnScaffold scaffold)
		{
			if (character?.Attributes == null || character.Attributes.Count == 0)
				return false;

			// The frame + EntryTableX are CONSTRUCTED, not stored: FreshPreFrame(class, vanityMask) is the constant
			// skeleton with the archetype/class island written from the character's class and the vanity hidden-slots
			// mask (#336) from the character's equipment (the FA015807 self-spawn carries it so a hidden piece survives
			// relog); EntryTableX is the constant 0xB7. Transient session islands are correctly absent.
			scaffold.PreFrame = FreshPreFrame(character.Class, character.Equipment?.VanityHiddenMask ?? 0);
			scaffold.EntryTableX = 0xB7;
			scaffold.BuffRecords.Clear();

			scaffold.Entries.Clear();
			scaffold.Entries.AddRange(character.Attributes.OrderBy(a => a.Sequence).Select(a => new ScaffoldEntry(a.EntryIndex, a.Tag, a.Value)));

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
					Tag = (ushort)((scaffold.IdList.Count + 2) * 0x3F1)
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

		// The normal-login pre-IIR frame (239 bytes) with the fresh-character island values: the near-constant
		// skeleton (0x05 lead, the 0x7E2/0x3F1 constants, the 41 00 00 82 08 closer) + Dikketest's per-char
		// values at the two known islands.
		// The frame's archetype code at @70 (abs-135 high byte): Soldier=1, Priest=2, Rogue=3, Mage=4. This is a
		// DIFFERENT numbering than the ArchType enum, recovered from the captured player spawns (each class maps to
		// exactly one value across all its sessions - the only genuine per-character field in the whole pre-IIR frame;
		// everything else that varies is the RDB script-engine char buffer, transient/zero for a player). See
		// docs/re/fa015807-spawn-reader.md (FUN_01121c00) + docs/re/player-spawn-generation-plan.md.
		private static byte FrameArchetype(Class @class)
		{
			return @class switch
			{
				Class.Conqueror or Class.DarkTemplar or Class.Guardian         => 1, // Soldier
				Class.PriestOfMitra or Class.TempestOfSet or Class.BearShaman  => 2, // Priest
				Class.Barbarian or Class.Ranger or Class.Assassin              => 3, // Rogue
				Class.HeraldOfXotli or Class.Demonologist or Class.Necromancer => 4, // Mage
				_                                                              => 3
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
		private static (ushort Tag, uint Value) FreshCloser(Class @class)
		{
			var value = @class switch
			{
				Class.Guardian or Class.Conqueror or Class.DarkTemplar        => 100u, // Soldier
				Class.PriestOfMitra or Class.TempestOfSet or Class.BearShaman => 200u, // Priest
				Class.Barbarian or Class.Assassin or Class.Ranger             => 300u, // Rogue
				_                                                             => 400u // Mage
			};

			var tag = (ushort)((FreshSkillIdsFor(@class).Length + 2) * 0x3F1);
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
