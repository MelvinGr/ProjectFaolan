using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	// Per-class spellweaving profile: the data the activate-response needs to be built for a given caster (docs/subsystems/combos.md).
	// The 4 pure casters weave; the necromancer set is capture-derived (NecroL80, decode_spellweave.py) and REAL - the
	// weave-element ids resolve to actual necro spells (Arcane Renewal / Arcane Surge / Parasite Host / Death God /
	// Death) and the three trance-buff ids resolve to Spellweaving / Spellweaving(1) / Arcane Drought in Spells. The
	// other three casters (Demonologist, Priest of Mitra, Tempest of Set) need their own captures to fill in - until
	// then they have no profile and the toggle stays a no-op-with-feedback for them.
	public sealed record SpellweaveProfile(
		uint[] TranceBuffs, // ApplySpell (base<<8)|rank ids sent on activate to open the trance
		uint[] WeaveElements, // element BASE ids (full spell id >> 8); the add's componentIndex indexes into this list
		uint[] FireStages, // the STAT IDS the deactivate burst sets (AttackRating/MagicRating/DefenseRating/ResistRating - see CapturedFireRating)
		uint StackingBuffBase); // the Spellweaving(1) buff BASE re-applied at an incremented id each picker reappear (stacks)

	public static class SpellweaveCatalog
	{
		private static readonly SpellweaveProfile Necromancer =
			new SpellweaveProfile([0x37F29800, 0x37E5A800, 0x3969D000], [0x396AE9, 0x3969D8, 0x37E6D9, 0x396A22, 0x396A3D], [0x398, 0x39B, 0x39A, 0x39D],
				0x37E5A8); // Spellweaving(1); refreshes 37e5a8 -> 37e5a9 -> 37e5aa each reappear

		private static readonly Dictionary<ConanClass, SpellweaveProfile> Profiles = new Dictionary<ConanClass, SpellweaveProfile>
		{
			[ConanClass.Necromancer] = Necromancer
			// Demonologist / PriestOfMitra / TempestOfSet - need per-class spellweaving captures (#141).
		};

		// Necromancer (NecroL80 capture): trance buffs 0x37F29800/0x37E5A800/0x3969D000; weave elements Arcane Renewal
		// 0x396AE9, Arcane Surge 0x3969D8, Parasite Host 0x37E6D9, Death God 0x396A22, Death 0x396A3D; fire stages
		// 0x398/0x39B/0x39A/0x39D.

		// ⚠️ The value the deactivate burst carries on all four stats. It is NOT a burst constant (#678): the four
		// "fire stage" ids are the client's `AttackRating` / `MagicRating` / `DefenseRating` / `ResistRating`, so this
		// is that level-80 necromancer's OWN combat rating - the same word takes 100 / 500 / 900 / 1300 / 1700 across
		// the corpus, tracking whoever was being described. Kept so the weave sends what it sent before the record was
		// decoded; what a weave should actually push is Gitea #685.
		public const uint CapturedFireRating = 31700;

		// "Exhausted" (spell base 0xB90), the debuff retail applies when the ENERGY pool empties - on the sprint path as
		// much as the weave, which is why it lives here beside the pool rather than inside the weave service.
		//
		// ⚠️ CAPTURE-CONFIRMED, and it used to be recorded as "not in the capture, so the id is our choice". It is in the
		// capture: in `Hoxxling_..._gameplay2` the `Energy` stat (1000028) reaches exactly 0 at 20:53:41.247, and in the
		// SAME millisecond retail sends `ApplySpell 0x6B87E316` carrying spell id `000B9000` (base 0xB90, form 0 = apply)
		// with applyTo == applyFrom == the player. 3.4s later, on the first push that puts energy back above zero (1019),
		// it sends `000B9001` - the form-1 END twin, empty payload. So the SERVER owns this debuff on the sprint path
		// too; the client's own exhausted STATE (it has one, keyed on the same stat) is a separate thing from the icon.
		public const uint ExhaustedBuff = 0xB90 << 8;

		// The universal ENERGY (yellow sprint-bar) max by class - the value retail pushes on `DispatchUpdateSprintBar`
		// (0x200E) when the bar is FULL. Two flat values, and they are flat: the same 14000 reaches a level-24 Herald
		// and a level-23 assassin as a level-80 one, so this is per class family, not a level curve.
		//
		// ⚠️ Corrected 2026-08-04 from 8910 / 13984, both of which were off by one observation. 8910 is 9000 minus one
		// 90-point drain tick - it was read off the FIRST packet of a weave, which is already one tick in. The round
		// numbers are the giveaway: retail's idle "bar is full" pushes carry exactly 9000 and exactly 14000, with a
		// single distinct value across the whole capture.
		//   9000  necromancer (4 captures, 3 characters) + demonologist (1)
		//  14000  Herald of Xotli (3 captures, 2 characters) + assassin (2)
		// Priest of Mitra and Tempest of Set are grouped with the casters by class family; neither is observed.
		//
		// ⚠️ Deriving these from a dump needs `BodyHex` length 5 as well as the opcode: `0x200E` is the LOSSY wire
		// RENDERING of a hash-registered method, so a plain `Opcode == 0x200e` filter also catches SimpleCharFullUpdate
		// bodies and reads a "max" of 57001 out of a character name (see the method-enum memory / #615).
		public static uint EnergyMaxForClass(ConanClass characterClass)
		{
			return characterClass switch
			{
				ConanClass.Necromancer or ConanClass.Demonologist or ConanClass.PriestOfMitra or ConanClass.TempestOfSet => 9000,
				_ => 14000
			};
		}

		public static SpellweaveProfile? ForClass(ConanClass characterClass)
		{
			return Profiles.GetValueOrDefault(characterClass);
		}
	}
}
