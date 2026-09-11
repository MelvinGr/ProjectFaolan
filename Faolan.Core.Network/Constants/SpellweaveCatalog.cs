using System;
using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     One caster class's spellweaving profile. Every id here is a spell base id; the wire form is
	///     <c>base &lt;&lt; 8</c> (form 0 = apply, form 1 = the end twin).
	/// </summary>
	/// <remarks>
	///     Read <c>--report spellweave</c> before editing any number in this file. The whole table is derived from the
	///     client's own spell records and that report re-derives it in one command, so a value changed here without
	///     the report agreeing is a value nobody can check.
	/// </remarks>
	public sealed record SpellweaveProfile(
		uint TranceBuff, // the 6,400,000 ms `Spellweaving` buff the trance opens with
		uint[] StackLadder, // `Spellweaving (1)..(6)`, in rung order - see the remark on the ladders below
		uint[] WeaveElements, // the five addable elements; the add's componentIndex indexes this list, in order
		uint BaseSpellDamageStep, // `Modify BaseSpellDamage` per rung: rung N grants N x this
		uint BacklashCritical, // the class's own backlash on roll 0-5
		uint BacklashSignature); // the class's own backlash on the second band

	public static class SpellweaveCatalog
	{
		// The four profiles below are the client's own table, not a capture reading. The client declares each one as
		// the argument list of a `StartSpellWeavingMode` (53190) GameAction element, whose five keys are
		// `NanoTemplate, Arg1..Arg4` (docs/re/gameaction-keylists.tsv). Searching by element kind is what finds them:
		// `--report spell-refs --report-arg kind:53190` lists all 24 carriers, and `--report spellweave` prints the
		// whole decoded ruleset.
		//
		// The Necromancer row is the control and it is exact: the client's list is
		// `Arcane Renewal / Arcane Surge / Parasite Host / Death God / Death`, in that order, which is
		// element-for-element and index-for-index what the NecroL80 capture wove (componentIndex 0/1/3 ->
		// Arcane Renewal / Arcane Surge / Death God). The other three rows are read off a table proven right where
		// it can be checked.
		//
		// The first two elements are the same prefix for every class - `Arcane Renewal` and `Arcane Surge` are
		// universal weaves - and only the last three are class-specific.
		//
		// The class each row belongs to is stated mechanically rather than read off those names: every rung carries
		// `SpellLineStatModifier` elements against its class's own spell lines, and a spell line is
		// `ConanClass * 1000 + index`, so the leading block is the class id - 41 Necromancer, 44 Demonologist,
		// 28 Tempest of Set, 24 Priest of Mitra. That agrees with the element names on all four. Not every leading
		// block is a class: the Demonologist ladder also carries a `50000` line that resolves to none, the same trap
		// the archetype-shared combos set with their `20` block, so read the blocks that are classes and leave the
		// rest alone.
		//
		// A fifth `Spellweaving (1)..(6)` band exists and is superseded content: 3364577 + 3364671-3364675, on
		// Demonologist spell line 44000. It carries no `StartSpellWeavingMode`, no `BaseSpellDamage` ramp and no
		// backlash payload; its durations are a flat 12,000 ms instead of the live 12/17/22/27/32 ladder; it chains
		// forward (each rung casts the next) instead of through a dispatcher; and its top rung modifies Necromancer
		// spell lines from inside a Demonologist ladder. The Demonologist's `Spellweaving` ability (3364576) happens
		// to sit beside it, which is only where that id lives.

		// `StackLadder` is an ordered list, not a base plus an offset: consecutive ids such as the necromancer's
		// `0x37E5A8 -> A9 -> AA` are `Spellweaving (1)`, `(2)` and `(3)`, the stack level advancing one rung, and
		// they are contiguous only for that class. The Demonologist's run descends (3663649 down to 3663643) and the
		// Priest of Mitra's is interleaved with unrelated records (3761196, 3761198, 3761224, 3761231...), so
		// `base + n` would walk those two classes into other spells.
		//
		// Rung 6 is the top buff but not the top of the ramp: its record carries the whole `BaseSpellDamage`
		// continuation for rungs 6..25 in one list, so a weave longer than six components keeps scaling without
		// a further buff id.
		private static readonly SpellweaveProfile Necromancer = new SpellweaveProfile(
			0x37F298, // Spellweaving (3666584) - the one trance buff a capture confirms
			[3663272, 3663273, 3663274, 3663275, 3663276, 3663277],
			[3762921, 3762648, 3663577, 3762722, 3762749], // Arcane Renewal / Arcane Surge / Parasite Host / Death God / Death
			250,
			3762741, // Rebellious Spirit
			3762685); // Infestation

		private static readonly SpellweaveProfile Demonologist = new SpellweaveProfile(
			3666962,
			[3663649, 3663648, 3663647, 3663646, 3663645, 3663643],
			[3762921, 3762648, 3763671, 3763797, 3763816], // ... / Hell / Hand of Doom / Howl of Armageddon
			100,
			3763492, // Flesh to Dust
			3763670); // Demon

		private static readonly SpellweaveProfile TempestOfSet = new SpellweaveProfile(
			3761192,
			[3761193, 3761197, 3761199, 3761200, 3761201, 3761202],
			[3762921, 3762648, 3764133, 3764495, 3765566], // ... / Set / Power Surge / Vengeful Idol of Set
			100,
			3764119, // Deadly Feedback
			3765588); // Twisted Rune of Set

		private static readonly SpellweaveProfile PriestOfMitra = new SpellweaveProfile(
			3761195,
			[3761196, 3761198, 3761224, 3761231, 3761232, 3761233],
			[3762921, 3762648, 3763852, 3763855, 3763853], // ... / Mitra's Grace / Mitra / Benevolence of Mitra
			100,
			3763867, // Shunned by Mitra
			3765073); // Flesheater

		// The trance buff is the one field here that is not self-naming. All four `Spellweaving` records
		// (6,400,000 ms) are byte-identical - `ExecuteBccAction 270` plus `CastNano Spellweaving Remover if
		// CombatMode != 39` - so the record cannot say whose it is, and nothing in the catalog casts them: the
		// server applies them directly, as the captures show. The assignment rests on three separate anchors: the
		// Necromancer's is captured (0x37F298); the Tempest of Set's and the Priest of Mitra's each sit in a
		// consecutive ability / trance / rung-1 triple (3761191/3761192/3761193 and 3761194/3761195/3761196, where
		// the two abilities are the `Spellweaving` rows of the Abilities catalog); and the Demonologist's is the
		// only one of the four left, which makes it an inference rather than a reading.
		private static readonly Dictionary<ConanClass, SpellweaveProfile> Profiles = new Dictionary<ConanClass, SpellweaveProfile>
		{
			[ConanClass.Necromancer] = Necromancer,
			[ConanClass.Demonologist] = Demonologist,
			[ConanClass.TempestOfSet] = TempestOfSet,
			[ConanClass.PriestOfMitra] = PriestOfMitra
		};

		// "Exhausted" (spell base 0xB90), the debuff retail applies when the energy pool empties - on the sprint path as
		// much as the weave, which is why it lives here beside the pool rather than inside the weave service.
		//
		// Capture-confirmed: in `Hoxxling_..._gameplay2` the `Energy` stat (1000028) reaches exactly 0 at
		// 20:53:41.247, and in the same millisecond retail sends `ApplySpell 0x6B87E316` carrying spell id
		// `000B9000` (base 0xB90, form 0 = apply) with applyTo == applyFrom == the player. 3.4s later, on the first
		// push that puts energy back above zero (1019), it sends `000B9001`, the form-1 end twin with an empty
		// payload. The server therefore owns this debuff on the sprint path too; the client's own exhausted state,
		// keyed on the same stat, is a separate thing from the icon.
		public const uint ExhaustedBuff = 0xB90 << 8;

		// The backlash is one roll of 0..99 per rung, not a chance-then-choose pair. Each rung's `Detrimental Effect`
		// record is a run of six `CastNano` elements: the first reads stat 520 `Rnd`, the read that draws the number,
		// and every later one compares stat 522 `LastRnd`, the value that draw left behind, against a band of it. The
		// six branches therefore partition a single roll and at most one can fire; reading them as six independent
		// chances would apply several backlashes at once.
		//
		// The band boundaries are identical across all four classes; only the first two outcomes differ per class
		// (`BacklashCritical` / `BacklashSignature`). The bands widen rung by rung, so the chance of escaping one
		// falls 24% -> 19% -> 14% -> 9% -> 4% -> 0%: by the sixth component a backlash is certain.
		//
		// `Spellweave Gone Bad: Weak/Moderate/Strong/Maximum` (196612-196615) is not this. Those four sit beside
		// `TEST Spellweave: Fire 1` in a low id band and are referenced by none of the 21,502 spell records: dead
		// test content whose name is the only thing about them that fits.
		//
		// Each row is the inclusive upper bound of the six bands, in order:
		//   [0] the class's own critical backlash   [1] the class's own signature backlash
		//   [2] Soul Wrack (tiered - see SoulWrack) [3] Arcane Drought  [4] Frailty  [5] Ether Drain
		private static readonly int[][] BacklashBandTops =
		[
			[5, 17, 25, 37, 56, 75],
			[5, 18, 27, 40, 60, 80],
			[5, 19, 29, 43, 64, 85],
			[5, 20, 31, 46, 68, 90],
			[5, 21, 33, 49, 72, 95],
			[5, 22, 35, 52, 76, 99]
		];

		// The four backlash outcomes every weaving class shares. `Soul Wrack` is the only one that tiers with
		// the rung - a stronger copy every two rungs - and the other three are one id each at every rung.
		private static readonly uint[] SoulWrack = [3762895, 3762895, 3762896, 3762896, 3762897, 3762897];
		private const uint ArcaneDrought = 3762640;
		private const uint Frailty = 3762647;
		private const uint EtherDrain = 3762654;

		// How many rungs the ladder has. A weave may run longer - rung 6's record carries the damage ramp up to
		// 25 - but there is no seventh buff, so the stack saturates here.
		public static int MaxRung => BacklashBandTops.Length;

		/// <summary>
		///     The buff for a weave stack level, as a spell base id. <paramref name="rung" /> is 0-based (0 is
		///     `Spellweaving (1)`, the rung the trance opens on) and saturates at the top of the ladder.
		/// </summary>
		public static uint RungBuff(SpellweaveProfile profile, int rung)
		{
			return profile.StackLadder[Math.Clamp(rung, 0, profile.StackLadder.Length - 1)];
		}

		/// <summary>
		///     Rolls this rung's backlash. Returns the spell base id to apply, or null when the roll lands in the
		///     band no branch covers - the escape. <paramref name="roll" /> is the 0..99 draw the client's `Rnd`
		///     stat stands for; the caller owns the randomness so the table itself stays testable.
		/// </summary>
		public static uint? RollBacklash(SpellweaveProfile profile, int rung, int roll)
		{
			var index = Math.Clamp(rung, 0, BacklashBandTops.Length - 1);
			var tops = BacklashBandTops[index];
			uint[] outcomes = [profile.BacklashCritical, profile.BacklashSignature, SoulWrack[index], ArcaneDrought, Frailty, EtherDrain];

			for (var band = 0; band < tops.Length; band++)
			{
				if (roll <= tops[band])
					return outcomes[band];
			}

			return null;
		}

		/// <summary>
		///     Whether a wire buff id is one the weave owns: the trance buff, any rung of any class's ladder, any
		///     backlash outcome, or the Exhausted twin the energy floor applies.
		/// </summary>
		/// <remarks>
		///     None of these may be persisted across a relog. A weave is session state only - <c>IsSpellweaving</c>,
		///     <c>WeaveComponents</c>, <c>SpellweaveBuffStack</c> and the energy pool all live on the session and reset
		///     at login - so restoring the buff would restore the trance's glow and sound loop around a player who is
		///     not weaving and cannot cancel, since the toggle would open a new weave. Energy makes the same argument:
		///     the pool comes back full at login, so a restored Exhausted contradicts the stat it was applied for.
		///     <para>
		///         The backlash outcomes are included on the same evidence: across all 21,502 spell records the only
		///         things that cast any of the six are the weave's own 24 <c>Detrimental Effect</c> /
		///         <c>Hostile Effect</c> records, so one surviving a relog can only be a weave that is not running.
		///     </para>
		/// </remarks>
		public static bool IsTranceBuff(uint wireSpellId)
		{
			if (wireSpellId == ExhaustedBuff)
				return true;

			var spellBase = wireSpellId >> 8;
			if (spellBase == ArcaneDrought || spellBase == Frailty || spellBase == EtherDrain || Array.IndexOf(SoulWrack, spellBase) >= 0)
				return true;

			foreach (var profile in Profiles.Values)
			{
				if (spellBase == profile.TranceBuff || spellBase == profile.BacklashCritical || spellBase == profile.BacklashSignature)
					return true;

				if (Array.IndexOf(profile.StackLadder, spellBase) >= 0)
					return true;
			}

			return false;
		}

		/// <summary>
		///     The energy (yellow sprint-bar) maximum by class: the value retail pushes on
		///     <c>DispatchUpdateSprintbar</c> when the bar is full.
		/// </summary>
		/// <remarks>
		///     Two flat values, per class family rather than a level curve - the same 14000 reaches a level-24 Herald
		///     and a level-23 assassin as a level-80 one. Retail's idle "bar is full" pushes carry exactly 9000 for
		///     the necromancer (4 captures, 3 characters) and demonologist (1), and exactly 14000 for the Herald of
		///     Xotli (3 captures, 2 characters) and assassin (2), with a single distinct value across each capture.
		///     Priest of Mitra and Tempest of Set are grouped with the casters by class family; neither is observed.
		///     <para>
		///         Deriving these from a dump needs a <c>BodyHex</c> length of 5 as well as the opcode: the sprintbar
		///         opcode is the lossy wire rendering of a hash-registered method, so filtering on the opcode alone
		///         also catches SimpleCharFullUpdate bodies and reads a "max" of 57001 out of a character name.
		///     </para>
		///     <para>
		///         <c>MaxEnergy</c> (stat 1000029) is read by the client's own data: the weave's
		///         <c>Spellweaving Cost</c> record (3663500) is a <c>HitPercentage</c> on <c>Energy</c> (1000028)
		///         stated as a percentage of <c>MaxEnergy</c>. The ceiling is therefore a real per-character stat
		///         retail expects to be set, even though it never pushes one in 120 captures.
		///     </para>
		/// </remarks>
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
