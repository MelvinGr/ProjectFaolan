using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	public class Spell : BaseEntity
	{
		/// <summary>
		///     The spell's own level requirement — RDB field 125 of its type-1040005 record, which is where the
		///     overwhelming majority of these come from and which leads over every other source.
		/// </summary>
		/// <remarks>
		///     ⚠️ **Not every row is re-derivable.** The RDB carries field 125 on 7,645 of 21,502 records while the
		///     shipped catalog has 8,221 rows with a level, so roughly 576 of them came from the community scrapes
		///     retired in #296 (plus captures) and survive only because `faolan.db` is committed — a fresh import
		///     rebuilds those spells with `Level = 0`. They are deliberately NOT cleared: this is the gate
		///     `GetClassSpellsUnlockedBetween` queries, so zeroing them would grant those spells at level 0. There is
		///     no marker saying which rows are which; Gitea #650 tracks closing that.
		///     <para>
		///         ⚠️ It is also NOT the level a character is actually GRANTED the spell at — retail grants at or
		///         below this, never above. Where a capture has caught the grant, <see cref="ClassSpell.GrantLevel" />
		///         holds that and wins; this field reads more like a trainer / full-power level.
		///     </para>
		/// </remarks>
		public byte Level { get; set; }

		// RDB field 123 (0x7B), carried RAW. The client's own stat-name table (LDB category 615) names it
		// `GUI Stacking Order`: a spell's precedence within a stacking line, so a higher order supersedes a lower one
		// and two buffs on the same line refuse to stack (#634). See GuiStackingLine for the line itself.
		//
		// ⚠️ **It is not the rank**, which is what this column was called until #634. It CORRELATES with rank - a
		// higher rank stacks over a lower one, so the ordering runs with the ranks - and that is why it read as one.
		// But an ORDER only has to be monotone within its own line, and the scale is otherwise arbitrary, which is
		// exactly the "mystery" this comment used to record: of the 883 spells with a "(Rank N)" suffix, 669 carry
		// 1/2/3 and 214 carry a clean x10 (`Healing Benediction` is 10/20/30 on stacking line 2 while `Wave of Life`
		// is 1/2/3 on lines 24020+). Both are valid orderings. Nothing discriminated them because neither is a rank -
		// the real rank is in the name's "(Rank N)" suffix, and the RDB may not state it at all.
		public byte GuiStackingOrder { get; set; }

		/// <summary>
		///     RDB field 128 (0x80), which the client's own stat-name table names <c>GUI Stacking Line</c> - the line a
		///     spell stacks ON, with <see cref="GuiStackingOrder" /> deciding precedence within it. It was called
		///     <c>LineId</c> ("the spell line") until #634; the behaviour described below is real and the feat join it
		///     supports is sound, but the NAME was wrong, and with it the two things this field recorded as
		///     unexplained. Both dissolve once it is read as a stacking line: an id whose <c>/1000</c> is not a class
		///     is simply a stacking line that is not per-class, and the "small numbers (1, 2, 4, …) shared by 25-30
		///     unrelated spells, far too coarse to be a line" are SHARED stacking lines - which is the entire purpose
		///     of one, so that (say) two +Strength buffs refuse to stack. A feat boosting a "spell line" is a feat
		///     boosting a stacking line, which is why the join works.
		///     <para>
		///     Carried RAW. When it is
		///     <c>ConanClass × 1000 + lineIndex</c> it is the spell's LINE - what a feat's spell modifier points at
		///     (<see cref="FeatSpellModifier.SpellLineId" />): 41000 is Necromancer line 0, and the five
		///     <c>Flesh to Worms</c> ranks all carry it, which is how a feat boosts a spell without naming a rank.
		///     0 when the record has no such field (only 3,158 of 21,502 templates carry one).
		///     </para>
		/// </summary>
		/// <remarks>
		///     ⚠️ <c>ConanClass × 1000 + index</c> is the common CASE, not the encoding (#591). Line <c>50000</c> is a
		///     real, working line whose high part is not a class - 50 (0x32) is outside the class registry entirely -
		///     and it resolves perfectly: its five spells are the five ranks of <c>Inferno of Amher</c>, and the five
		///     feat modifiers pointing at it are the five ranks of <c>Blessing of Amher</c>, a clean
		///     2.5/5/7.5/10/15% ladder. The feat is named for the spell it boosts. So an id whose <c>/1000</c> is not
		///     a class is not a corrupt row and not a reason to filter - it is a line the class reading does not
		///     explain. (<c>440061</c>, one spell, is the other such id.)
		///     <para>
		///     The field is also OVERLOADED and only its class-encoded values are line ids. It also holds small numbers
		///     (1, 2, 4, …) shared by 25-30 unrelated spells apiece - far too coarse to be a line, which is 3-7 ranks -
		///     so those are some other grouping and are not decoded. Carried raw rather than filtered because the join
		///     cannot collide: a feat only ever references a class-encoded value.
		///     <para>
		///         This also qualifies the note on <see cref="GuiStackingOrder" /> above, which said field 128 "is constant (2)
		///         across that family's ranks". It is constant across a family - that part was right - but the 2 was
		///         one family's value, not the field's meaning.
		///     </para>
		///     </para>
		/// </remarks>
		public uint GuiStackingLine { get; set; }

		/// <summary>
		///     RDB field 847 (0x34F) of the spell's type-1040005 record: the ATTRIBUTE this spell's magnitude scales
		///     off, as a wire stat id. Exactly three values over the 716 records that carry one -
		///     <see cref="CharacterStatId.Intelligence" /> (0x328, ×482), <see cref="CharacterStatId.Wisdom" />
		///     (0x32A, ×224) and <see cref="CharacterStatId.Strength" /> (0x324, ×10) - and it splits where a reader
		///     would expect: the Demonologist's nukes (Hellfire Stream, Shockstrike, Fires of Gehenna) on
		///     Intelligence, the curses and DoTs (Venomous Curse, Poisonous Incantation) on Wisdom. 0 when the record
		///     carries no such field, which is the overwhelming majority - see the remarks for what happens then.
		/// </summary>
		/// <remarks>
		///     ⚠️ This is a per-spell OVERRIDE, not the general rule, which is why a 0 here is not a gap. The client
		///     picks a caster's scaling attribute by CLASS, not by spell: its magic-damage accessor
		///     (<c>FUN_01118ce0</c>) defaults to Wisdom (0x32A) and switches to Intelligence (0x328) only for
		///     Necromancer (0x29), Herald of Xotli (0x2B) and Demonologist (0x2C) - see
		///     <c>docs/rdb/spell-scaling.md</c>. So the class default covers every spell and field 847 names the
		///     handful that deviate. <c>CombatResolver.ResolveSpellDamage</c> reads it that way round.
		/// </remarks>
		public uint ScalingAttribute { get; set; }

		/// <summary>
		///     The SPLASH triple of the spell's type-1040005 record - RDB fields 347/348/349 (0x15B/0x15C/0x15D),
		///     which the client's own stat-name table (LDB category 615) names <c>Splash Radius</c>,
		///     <c>Splash Damage</c> and <c>Splash damage max targets</c>. They travel together: all three are on the
		///     same 215 of 21,502 records, and `--report rdb-field-coverage --report-arg 1040005:347x348` pairs them
		///     row for row. Radius is in centimetre-hundredths like <see cref="Range" /> (200 -> 2 m), damage is a
		///     percentage ×100 (3000 -> 30%), max-targets is a raw count (0…24). 0 = the spell has no splash, which
		///     is 99% of the catalog.
		/// </summary>
		/// <remarks>
		///     ⚠️ <c>SplashDamage</c> was imported as <c>DamageRating</c> until #626, described as "the spell's
		///     damage rating, 0-8000 = 0-80%". It never was one: a property carried by 1% of the catalog cannot be
		///     the damage rating of every damaging spell, and the reading came from matching one tooltip's
		///     "Effect 50.00" to a 5000 that also appears here. The spell's real damage MAGNITUDE remains unsourced
		///     (#592) - nothing in combat reads these three; they are catalog data.
		/// </remarks>
		public float SplashRadius { get; set; }

		/// <inheritdoc cref="SplashRadius" />
		public float SplashDamage { get; set; }

		/// <inheritdoc cref="SplashRadius" />
		public uint SplashMaxTargets { get; set; }

		public float Range { get; set; } // target range in metres (RDB field 0x11F / 100)

		// Damage-over-time per tick on HealthCurrent, extracted from captured StatDamageIIR (0xE9F24184) records keyed
		// by base spell id (the +41 signed delta; its affected stat at +33 is 0x1B HealthCurrent). Negative = a bleed
		// debuff (Rend -8, 0x342F23 -5); 0 = not a DoT. The world tick applies this to the target's vitals + sends the
		// StatDamageIIR while the debuff is active (#274). DotType is the +45 damage-school flag (1/4), carried so the
		// rebuilt packet matches retail.
		public int DotDelta { get; set; }

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The tooltip description, from LDB category 50211 keyed by the SPELL id - the same text the client shows in a
		// spell/buff tooltip ("When you or your group perform a fatality and this buff is active, the Frozen Hatred pet
		// appears…"). NULL when the LDB has none for this spell (only ~3.6k of the 20k spells carry one), or when the
		// RDB-backed rdb-ldb-names module has not been run (it is opt-in, needing --RdbDir).
		//
		// This is the one source that states an effect in WORDS, which is why it is worth storing: the spell record
		// itself carries magnitudes but not their meaning, so the description is how an effect gets identified (it is
		// what pinned Frozen Hatred's fatality proc, #434) and how a decoded magnitude gets sanity-checked. Some carry
		// the client's inline gender markup (#name{m:…|f:…}) verbatim - stored as-is, it is the client's own text.
		[MaxLength(2048)]
		public string? Description { get; set; }

		public uint DotType { get; set; }

		// How often the DoT ticks, in milliseconds. MEASURED from the captures rather than lifted from the RDB (#437):
		// a StatDamageIIR carries its spell's base id at word 12, so the gap between consecutive ticks of one spell on
		// one target IS the interval. The gaps of a real DoT cluster tightly and the values are round - 500 ms to 11 s.
		//
		// Which is why this column exists at all: the tick used ONE constant, 2 s, for every DoT. That is right for the
		// Incinerate family and wrong everywhere else - Ripper Volley and Archfiend tick twice as fast as we ran them,
		// Ether Drain two and a half times slower, Hellfire Breath five times slower. 0 = unmeasured, and the tick
		// falls back to 2 s.
		//
		// Measured by the `dot-effects` importer module, NOT by hand: the first 38 came from a one-off scan that was
		// never shipped, so the number could not be improved without redoing the work. Now it is 53 of the 84 DoT
		// spells, and re-running the module is all it takes to widen that as the corpus grows. What decides whether
		// observed gaps establish an interval at all is DotIntervalEstimator, which rejects rather than guesses -
		// the remaining 31 have no interval because their ticks were never observed often enough, not because
		// nobody looked.
		public uint DotIntervalMs { get; set; }

		// How often a CHANNELED spell ticks while its cast bar runs, in milliseconds. From RDB field 0x1BF (447), which
		// the client names `ChannelingInterval` and which is in CENTISECONDS like the cast-time and duration fields, so
		// it is stored ×10 as real ms. 521 of 21,502 records carry it - and one of them is the developers' own
		// "Oysteins Channeltest", which is about as direct a statement of what the field is for as the data gets.
		// 0 = not a channeled spell (the overwhelming majority).
		//
		// Distinct from DotIntervalMs, which is the tick of a debuff that ticks AFTER the cast lands and is measured
		// off the wire. This one is stated by the client and governs the cast itself: the channeled ground AoE
		// (CombatService.ChannelGroundAoe, #349) spreads the spell's damage across `channel / this` ticks.
		public uint ChannelingIntervalMs { get; set; }

		// The blast radius of an AREA spell, in metres. NOT a property field - it is a GameAction ELEMENT key
		// (`Radius`, key 11 = stat 0x2DD) on the kinds that select targets in an area, read by key out of the same
		// [2][9] region as the recast (GameActionTypes.AreaTargetKinds). Which is why the "no per-spell AoE radius
		// exists" reading held for so long: the property run genuinely has none, and the answer was one region over.
		//
		// ⚠️ Distinct from SplashRadius (field 347), which 231 records carry: splash is the collateral a SINGLE-target
		// hit spills onto bystanders, this is the area the spell targets in the first place. A few spells carry both.
		// Also distinct in UNIT - a metre here, a metre ×100 there.
		//
		// 0 = the spell defines no area (it is single-target, or its area is server-side). Read by
		// CombatService.ApplyAoeTick as the ground-AoE blast radius, falling back to its interim constant.
		public float AreaRadius { get; set; }

		// Buff/effect duration in milliseconds. Imported from RDB field 0x008, which is in CENTISECONDS (×10ms - the
		// same unit as the cast-time field; calibrated in-client: Rend 700->7s, Butcher 400->4s, Exhausted 10000->100s),
		// so it is stored here ×10 as real ms. 0 = no self-duration on this record (an instant spell, or an ability
		// whose timed effect lives on a separate EffectSpell record). Drives server-side buff expiry: when a self-buff
		// with a known duration elapses, the world tick sends the retail end-twin removal (rank byte -> 1, empty
		// payload) so the icon disappears at 0 instead of sticking (#274).
		public uint DurationMs { get; set; }

		public uint EffectSpell { get; set; }

		// --- Pet summons (#640/#307). A summon spell performs no effect of its own: its GameAction region carries one
		// StartServerSideScryScript*WithParams* (53261) element whose ScriptParams string is the summon, spelled out:
		//
		//   spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781
		//
		// so the roster is READ, not listed. This is the whole reason the twelve necro pets stopped being a hand-typed
		// table: `SummonSpawnHash != null` IS "this spell summons a pet", over the whole catalog rather than over the
		// spells one capture happened to cast.
		//
		// ⚠️ `spawnHash` is a 4-character ACG token ([0-9A-Z_]) and does NOT resolve to a creature anywhere in the
		// client - it addressed retail's SERVER-side content, and the client neither reads nor compares it (#547 closed
		// the same question for the quest tokens). So it is carried as EVIDENCE, not as a lookup: it is what makes a
		// summon spell we have no captured pet NAME for a reportable gap instead of an invisible one. The name itself
		// comes from the wire (PetSummonExtractor).
		[MaxLength(16)]
		public string? SummonSpawnHash { get; set; }

		// Which of the two param spellings the record used, and so whether the pet FIGHTS: the `spawnHash=` form is a
		// combat pet, the `Type=Pet;Tag=` form the social companion. Every captured summon agrees - all 11 spawnHash
		// pets bind with PetCodec.CombatKind on the wire and the one Type=Pet pet with SocialKind - which is what
		// makes this readable off the record instead of needing a capture per pet.
		public bool SummonIsCompanion { get; set; }

		// The buff the summon applies alongside the pet, and the pet-POINT cost of keeping it out - both straight out
		// of the same params string. Null on a spell that is not a summon. Nothing consults them yet; they are lifted
		// because they are free here and a re-harvest is not.
		public uint? SummonPetBuff { get; set; }
		public uint? SummonPointCost { get; set; }

		// Backfilled from the RDB spell-data record (type 1040005) by RdbSpellImporter (#236):
		public ushort CastTime { get; set; } // milliseconds (RDB field 0x126 * 10)

		/// <summary>
		///     The spell's base recast (cooldown) in CENTISECONDS - the client's own unit for it, applied as
		///     <c>value * 0.01</c> seconds by the recast timer <c>FUN_007e1080</c>.
		///     <para>
		///         Sourced from the spell record's own GameAction ELEMENT region (#271): a
		///         <c>SetSpellRecastTime</c> (53226) element keys it on the spell and a <c>SetSpellLineRecastTime</c>
		///         (53077) one on the whole GUI stacking line, both under <c>GameActionTypes.KeyValue</c>
		///         (0x2f9) - the same key the tooltip's "Recast" line reads
		///         (<c>docs/re/spell-recast-source.md</c>). A COMBO's reuse time is a separate fact in a separate
		///         place - the combo XML's own <c>cooldown</c> attribute (RDB 1010206, <c>Combos.Cooldown</c>, in
		///         seconds), written here by <c>RdbComboImporter</c> against the combo's <c>GameActionId</c>. The two
		///         sources are disjoint over the whole catalog, so neither can overwrite the other.
		///     </para>
		///     <para>
		///         ⚠️ NOT seconds, which is what this column held until #271 found the real source. 67 of the 3,682
		///         shipped values are not a whole number of seconds (<c>Divine Wave</c> 2.7 s,
		///         <c>Scourge of Derketo</c> 6.5 s), so a seconds column silently truncated them - and the ceiling
		///         went with it: <c>Renewing Faith</c> is 1800 s, which no longer fits alongside the precision.
		///     </para>
		///     <para>
		///         ⚠️ This is the BASE. <c>ChangeRecastTime</c> (53031) / <c>ChangeLineRecastTime</c> (53069) are
		///         feat/AA MODIFIERS the client adds to it, and are deliberately not folded in here.
		///     </para>
		/// </summary>
		public uint RecastCentiseconds { get; set; }

		/// <summary>
		///     RDB field 132 (0x084) of the spell's type-1040005 record - the client's
		///     <see cref="CharacterStatId.ManaCost" /> stat id - carried RAW, because the client reads it in TWO modes
		///     and the selector is not in this record. 0 = the spell is free, which is retail's own reading of an
		///     absent property. 1,691 of the 20,847 catalogued spells carry a cost.
		/// </summary>
		/// <remarks>
		///     Recovered from the client, not fitted (#629). <c>FUN_008d6f70</c> - the function behind the spell
		///     tooltip's <c>ManaCostToolTip</c> line - is:
		///     <code>
		///     cost = spell.GetIntStat(0x84);                               // this field, clamped to >= 0
		///     if (spellFlags &amp; 4)                                          // the percent-of-pool mode
		///         cost = (int)(caster.GetIntStat(0x1FB) * cost * 0.01f);   // 0x1FB = ManaMax, wire units
		///     cost = (int)((modifier.Amount * 0.0001f + 1.0f) * (modifier.Value + cost));
		///     </code>
		///     and the tooltip prints <c>cost * 0.01</c>, i.e. it divides the wire's hundredths back to display units.
		///     The two modifier terms are exactly the <see cref="FeatSpellStatModifier" /> pair (an absolute
		///     <c>Value</c> plus an <c>Amount</c> in hundredths of a percent) that feats already target at
		///     <see cref="CharacterStatId.ManaCost" />, which is what says this is the real cast-cost path and not a
		///     display-only accessor.
		///     <para>
		///         So with the flag set the value is a PERCENTAGE of the caster's mana pool (<c>Ice Strike</c> 9 = 9%),
		///         and without it an absolute cost in wire hundredths (<c>Dark Knowledge (Rank 4)</c> 42200 = 422
		///         mana). Which of the two is in force is <see cref="ManaCostIsPercent" /> - the record STATES it
		///         (#630), so nothing here is inferred from the magnitude any more.
		///     </para>
		/// </remarks>
		public ushort ManaCost { get; set; }

		/// <summary>
		///     Bit 2 of the spell record's FLAGS word (body byte 52), which is the word the client keeps at
		///     <c>spell + 0xC8</c> and tests as <c>*(uint*)(spell + 0xC8) &gt;&gt; 2 &amp; 1</c> to choose how to read
		///     <see cref="ManaCost" />: set = a percentage of the caster's mana pool, clear = an absolute cost in wire
		///     hundredths.
		/// </summary>
		/// <remarks>
		///     Found by sweeping the catalog rather than by reading the loader (#630). Three shapes over the ascending
		///     PROPERTY run - value buckets, single bits, and mere presence - all came up empty, which is what said to
		///     look outside it: the run is only ~16 fields wide and starts at word 15, so ~80% of a 964-byte record is
		///     not property table at all. Bit 2 of word 13 is set on 995 of the catalog's percent-mode spells and on
		///     ZERO flat-mode ones.
		///     <para>
		///         It also lands exactly where the client says it should - bit 2 - and it settles the case the ticket
		///         raised as proof the mode is per-record: <c>Dark Knowledge</c> rank 1 (10%) has the bit set while
		///         ranks 2-4 (128/265/422 flat) have it clear, in one spell line.
		///     </para>
		///     <para>
		///         ⚠️ It disagrees with the old <c>&lt;= 100 is a percent</c> heuristic on 215 of 1,713 records, and
		///         the flag is right on all of them: they are tiny FLAT costs (mostly a literal 1, i.e. 0.01 mana) plus
		///         the catalog's test spells, which the magnitude rule read as 1% costs. Nobody would have noticed -
		///         both readings are ~free - which is exactly why a heuristic that agrees 87% of the time is worth
		///         replacing with the stated bit.
		///     </para>
		/// </remarks>
		public bool ManaCostIsPercent { get; set; }
	}
}
