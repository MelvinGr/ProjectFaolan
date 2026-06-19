using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Spawn
{
	// Maps the spawn scaffold between its wire form (PlayerSpawnScaffold) and the character's DB rows
	// (CharacterAttribute / CharacterSkill / CharacterScaffoldPair / CharacterSpawnFrame), so a player spawn can
	// be GENERATED from the database instead of spliced into captured template bytes. Buff/timer records are
	// transient session state and are deliberately not stored - a buffed captured spawn extracts to the same rows
	// as its unbuffed form. Fresh characters get the fresh-character defaults below (extracted from the Dikketest
	// create capture - a just-created character at world entry; the same role Strie's template played, but with a
	// genuinely fresh scaffold). See docs/archive/player-spawn-generation-plan.md "VALIDATED GRAMMAR".
	public static class PlayerSpawnScaffoldRows
	{
		// The closing [u16 tag][u32 value] entry-table record (the final entry-table entry). It is PER-CLASS, not a
		// single constant - #250: stamping one class's closer on another crashes the client's character construction
		// (in-client: a Necromancer carrying the Barbarian closer 0x56B6/0x12C crashes the loading screen; its own
		// 0x4301/0x190 loads). The VALUE is the archetype index x 100 (Soldier=100, Priest=200, Rogue=300, Mage=400),
		// solid across all eight captured classes. The TAG is class-specific (race-independent: a race-1 and a
		// Stygian Necromancer both load with 0x4301); the five values below are read off each class's create capture
		// (Captures/*_create). Classes with no create capture yet reuse a same-archetype tag as a best-effort - those
		// are FIXME until their create spawn is captured (Captures/scripts can re-extract the closer per class).
		private static (ushort Tag, uint Value) FreshCloser(Class @class)
		{
			var value = @class switch
			{
				Class.Guardian or Class.Conqueror or Class.DarkTemplar => 100u,          // Soldier
				Class.PriestOfMitra or Class.TempestOfSet or Class.BearShaman => 200u,   // Priest
				Class.Barbarian or Class.Assassin or Class.Ranger => 300u,               // Rogue
				_ => 400u                                                                // Mage
			};

			var tag = @class switch
			{
				Class.Barbarian => (ushort)0x56B6,
				Class.Guardian => (ushort)0x4ED4,
				Class.BearShaman => (ushort)0x46F2,
				Class.HeraldOfXotli => (ushort)0x46F2,
				Class.Necromancer => (ushort)0x4301,
				// No create capture yet - reuse a captured class of the same archetype (FIXME: capture to confirm).
				Class.Conqueror or Class.DarkTemplar => (ushort)0x4ED4,    // Guardian (soldier)
				Class.Assassin or Class.Ranger => (ushort)0x56B6,          // Barbarian (rogue)
				Class.PriestOfMitra or Class.TempestOfSet => (ushort)0x46F2, // BearShaman (priest)
				_ => (ushort)0x4301                                        // Demonologist -> Necromancer (mage)
			};

			return (tag, value);
		}

		// Dikketest's 6 weighted attributes (weight 1.0). The tags are per-character (likely the chosen
		// race/class/head presets - mapping not yet cracked), so fresh characters keep Dikketest's proven set.
		// The character's morph entries are generated from its CharacterBody (see CharacterMorphs), not stored here.
		private static readonly ScaffoldEntry[] FreshWeightedAttributes =
		[
			new ScaffoldEntry(1, 0x00B0, 0x3F800000), new ScaffoldEntry(2, 0x0147, 0x3F800000), new ScaffoldEntry(3, 0x0154, 0x3F800000), new ScaffoldEntry(4, 0x0191, 0x3F800000), new ScaffoldEntry(5, 0x010B, 0x3F800000),
			new ScaffoldEntry(6, 0x01DE, 0x3F800000)
		];

		// Per-class fresh feat/ability ID list (all rank 0). The FIRST id is the PROFESSION feat and equals
		// (class enum) * 100 (1800=Barbarian, 2000=Guardian, 2900=BearShaman, 3400=Assassin, 4100=Necromancer,
		// 2200=Conqueror, 4300=HeraldOfXotli, 4400=Demonologist...). The AoC client DERIVES the displayed
		// profession from this feat tree, so a fresh character MUST carry its own class's profession feat or the
		// sheet (and weapon proficiency) defaults to whatever the profession feat resolves to - #239, where every
		// fresh char inherited Dikketest's Barbarian feat 1800 and showed as a Barbarian. Confirmed across 8
		// classes' retail self-spawns (Captures/scripts: profession feat = class*100, always first/ascending).
		// The four captured-at-L1 archetypes use their exact retail fresh list; the rest fall back to the
		// profession feat + the base feats common to every captured fresh list (display is correct for all 12).
		private static readonly uint[] CommonFreshSkillIds = [6900, 8100, 8305, 8400, 13600, 14600, 328900, 331300];

		private static readonly Dictionary<Class, uint[]> FreshSkillIdsByClass = new()
		{
			[Class.Barbarian] = [1800, 6000, 6100, 6200, 6300, 6600, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 124400, 318400, 318800, 328700, 328900, 331300],
			[Class.Guardian] = [2000, 6000, 6200, 6500, 6700, 6800, 6900, 7100, 7500, 8100, 8305, 8400, 13600, 14600, 24700, 328700, 328900, 331300],
			[Class.BearShaman] = [2900, 6300, 6600, 6700, 6800, 6900, 7100, 8100, 8305, 8400, 13600, 14600, 244300, 328700, 328900, 331300],
			[Class.Assassin] = [3400, 6400, 6700, 6900, 8100, 8305, 8400, 13600, 14600, 160000, 318000, 328700, 328900, 331300],
			[Class.Necromancer] = [4100, 6400, 6700, 6800, 6900, 7800, 8100, 8305, 8400, 8900, 13600, 14600, 85200, 328900, 331300],
		};

		// The fresh feat list for a class: the captured retail list when we have one, else the profession feat
		// (class*100) prepended to the common base feats. Always ascending (the wire/codec require it).
		public static uint[] FreshSkillIdsFor(Class @class)
		{
			if (FreshSkillIdsByClass.TryGetValue(@class, out var ids))
				return ids;

			return new[] { (uint)@class * 100 }
				.Concat(CommonFreshSkillIds)
				.OrderBy(id => id)
				.ToArray();
		}

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
			if (character.SpawnFrame?.PreFrame != null && character.Attributes is { Count: > 0 })
				return false;

			character.SpawnFrame = new CharacterSpawnFrame
			{
				CharacterId = character.Id,
				PreFrame = FreshPreFrame(),
				EntryTableX = 0xB7
			};

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
					StatId = e.Index,
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
				.Entries
				.Select((e, sequence) => new CharacterAttribute
				{
					CharacterId = character.Id,
					Sequence = sequence,
					StatId = e.Index,
					Tag = e.Tag,
					Value = e.Value
				})
				.ToList();

			character.Skills = scaffold
				.IdList
				.Select(s => new CharacterSkill
				{
					CharacterId = character.Id,
					SkillId = s.Id,
					Rank = s.Rank
				})
				.ToList();

			character.ScaffoldPairs = scaffold
				.EndPairs
				.Select(p => Pair(character.Id, 0, p))
				.Concat(scaffold.EndPairs2.Select(p => Pair(character.Id, 1, p)))
				.ToList();

			character.SpawnFrame = new CharacterSpawnFrame
			{
				CharacterId = character.Id,
				PreFrame = scaffold.PreFrame,
				EntryTableX = scaffold.EntryTableX
			};
		}

		// The normal-login pre-IIR frame (239 bytes) with the fresh-character island values: the near-constant
		// skeleton (0x05 lead, the 0x7E2/0x3F1 constants, the 41 00 00 82 08 closer) + Dikketest's per-char
		// values at the two known islands.
		public static byte[] FreshPreFrame()
		{
			var f = new byte[239];
			f[0] = 0x05;
			f[70] = 0x03; // u16 island @70 = 0x0312
			f[71] = 0x12;
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

		// Fills the scaffold's per-character parts from the rows (generation: rows -> wire). Returns false when
		// the character has no scaffold rows yet (caller falls back to the captured-spawn splice path).
		public static bool TryApplyFrom(Character character, PlayerSpawnScaffold scaffold)
		{
			if (character?.SpawnFrame?.PreFrame == null || character.Attributes == null || character.Attributes.Count == 0)
				return false;

			scaffold.PreFrame = character.SpawnFrame.PreFrame;
			scaffold.EntryTableX = character.SpawnFrame.EntryTableX;
			scaffold.BuffRecords.Clear();

			scaffold.Entries.Clear();
			scaffold.Entries.AddRange(character
				.Attributes
				.OrderBy(a => a.Sequence)
				.Select(a => new ScaffoldEntry(a.StatId, a.Tag, a.Value)));

			scaffold.IdList.Clear();
			if (character.Skills != null)
			{
				scaffold.IdList.AddRange(character
					.Skills
					.OrderBy(s => s.SkillId)
					.Select(s => new ScaffoldIdEntry(s.Rank, s.SkillId)));
			}

			scaffold.EndPairs.Clear();
			scaffold.EndPairs2.Clear();
			if (character.ScaffoldPairs != null)
			{
				scaffold.EndPairs.AddRange(character
					.ScaffoldPairs
					.Where(p => p.ListIndex == 0)
					.OrderBy(p => p.PairId)
					.Select(p => new ScaffoldPair(p.PairId, p.Value)));
				scaffold.EndPairs2.AddRange(character
					.ScaffoldPairs
					.Where(p => p.ListIndex == 1)
					.OrderBy(p => p.PairId)
					.Select(p => new ScaffoldPair(p.PairId, p.Value)));
			}

			return true;
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
