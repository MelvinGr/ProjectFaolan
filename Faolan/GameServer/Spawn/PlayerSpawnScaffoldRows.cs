using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Spawn
{
	// Maps the spawn scaffold between its wire form (PlayerSpawnScaffold) and the character's DB rows
	// (CharacterAttribute / CharacterSkill / CharacterScaffoldPair / CharacterSpawnFrame), so a player spawn can
	// be GENERATED from the database instead of spliced into captured template bytes. Buff/timer records are
	// transient session state and are deliberately not stored - a buffed captured spawn extracts to the same rows
	// as its unbuffed form. Fresh characters get the fresh-character defaults below (extracted from the Dikketest
	// create capture - a just-created character at world entry; the same role Strie's template played, but with a
	// genuinely fresh scaffold). See docs/player-spawn-generation-plan.md "VALIDATED GRAMMAR".
	public static class PlayerSpawnScaffoldRows
	{
		// Replaces the character's scaffold rows with the scaffold's content (extraction: captured spawn -> rows).
		public static void ExtractTo(Character character, PlayerSpawnScaffold scaffold)
		{
			character.Attributes = scaffold.Entries.Select((e, sequence) => new CharacterAttribute
			{
				CharacterId = character.Id,
				Sequence = sequence,
				StatId = e.Index,
				Tag = e.Tag,
				Value = e.Value
			}).ToList();

			character.Skills = scaffold.IdList.Select(s => new CharacterSkill
			{
				CharacterId = character.Id,
				SkillId = s.Id,
				Rank = s.Rank
			}).ToList();

			character.ScaffoldPairs = scaffold.EndPairs.Select(p => Pair(character.Id, 0, p))
				.Concat(scaffold.EndPairs2.Select(p => Pair(character.Id, 1, p))).ToList();

			character.SpawnFrame = new CharacterSpawnFrame
			{
				CharacterId = character.Id,
				PreFrame = scaffold.PreFrame,
				EntryTableX = scaffold.EntryTableX
			};
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
			scaffold.Entries.AddRange(character.Attributes.OrderBy(a => a.Sequence).Select(a => new ScaffoldEntry(a.StatId, a.Tag, a.Value)));

			scaffold.IdList.Clear();
			if (character.Skills != null)
				scaffold.IdList.AddRange(character.Skills.OrderBy(s => s.SkillId).Select(s => new ScaffoldIdEntry(s.Rank, s.SkillId)));

			scaffold.EndPairs.Clear();
			scaffold.EndPairs2.Clear();
			if (character.ScaffoldPairs != null)
			{
				scaffold.EndPairs.AddRange(character.ScaffoldPairs.Where(p => p.ListIndex == 0).OrderBy(p => p.PairId).Select(p => new ScaffoldPair(p.PairId, p.Value)));
				scaffold.EndPairs2.AddRange(character.ScaffoldPairs.Where(p => p.ListIndex == 1).OrderBy(p => p.PairId).Select(p => new ScaffoldPair(p.PairId, p.Value)));
			}

			return true;
		}

		// Seeds a character that has no scaffold rows yet with the fresh-character defaults. Returns true when it
		// seeded (caller persists).
		public static bool EnsureFreshRows(Character character)
		{
			if (character.SpawnFrame?.PreFrame != null && character.Attributes is { Count: > 0 })
				return false;

			character.SpawnFrame = new CharacterSpawnFrame
			{
				CharacterId = character.Id,
				PreFrame = FreshPreFrame(),
				EntryTableX = 0xB7
			};
			character.Attributes = FreshEntries.Select((e, sequence) => new CharacterAttribute
			{
				CharacterId = character.Id,
				Sequence = sequence,
				StatId = e.Index,
				Tag = e.Tag,
				Value = e.Value
			}).ToList();
			character.Skills = FreshSkillIds.Select(id => new CharacterSkill
			{
				CharacterId = character.Id,
				SkillId = id
			}).ToList();
			character.ScaffoldPairs = new List<CharacterScaffoldPair>();
			return true;
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

		// Dikketest's entry table: 6 weighted attributes (weight 1.0), 7 morph entries (the editor body sliders;
		// ids 0x0100..0x0106, float-bit values) and the closing [u16][u32] record.
		private static readonly ScaffoldEntry[] FreshEntries =
		{
			new ScaffoldEntry(1, 0x00B0, 0x3F800000),
			new ScaffoldEntry(2, 0x0147, 0x3F800000),
			new ScaffoldEntry(3, 0x0154, 0x3F800000),
			new ScaffoldEntry(4, 0x0191, 0x3F800000),
			new ScaffoldEntry(5, 0x010B, 0x3F800000),
			new ScaffoldEntry(6, 0x01DE, 0x3F800000),
			new ScaffoldEntry(7, 0x0100, 0xBCF5C28F),
			new ScaffoldEntry(8, 0x0104, 0x3BE56042),
			new ScaffoldEntry(9, 0x0102, 0x3C75C28F),
			new ScaffoldEntry(10, 0x0101, 0xBCF5C28F),
			new ScaffoldEntry(11, 0x0106, 0xBCF5C28F),
			new ScaffoldEntry(12, 0x0103, 0x3BE56042),
			new ScaffoldEntry(13, 0x0105, 0xBCF5C28F),
			new ScaffoldEntry(14, 0x56B6, 0x0000012C)
		};

		// Dikketest's feat/ability ID list (all rank 0).
		private static readonly uint[] FreshSkillIds =
		{
			1800, 6000, 6100, 6200, 6300, 6600, 6800, 6900, 7100, 8100,
			8305, 8400, 13600, 14600, 124400, 318400, 318800, 328700, 328900, 331300
		};

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
