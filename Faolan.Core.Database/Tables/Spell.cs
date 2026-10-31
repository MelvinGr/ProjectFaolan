using System.ComponentModel.DataAnnotations;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	public class Spell : BaseEntity
	{
		/// <summary>
		///     The spell's own level requirement, from two client sources: RDB field 125 of its type-1040005 record (the
		///     overwhelming majority, and the one that leads) and, for a spell a combo drives, the combo XML's own
		///     <c>level</c> attribute (RDB 1010206, <c>RdbComboImporter.FillComboSpellLevels</c>).
		/// </summary>
		/// <remarks>
		///     Not every row is re-derivable from the client: 7,868 of the shipped 8,460 come back from the RDB alone
		///     (7,378 from the spell record, 490 from the combo XML); the remaining 592 come from captures and survive
		///     only because <c>faolan.sql</c> carries them - a fresh import rebuilds those spells with <c>Level = 0</c>.
		///     They must not be cleared: this is the gate <c>GetClassSpellsUnlockedBetween</c> queries, so zeroing them
		///     would grant those spells at level 0. No column marks which rows are which; the set is computable by
		///     blanking the column and diffing a re-import. Open work tracked in Gitea #650.
		///     <para>
		///         Where the two client sources both speak they never contradict each other (the 8 overlaps are all
		///         <c>generic_*</c> effect templates, whose combo <c>level="1"</c> is a sentinel and is excluded). The
		///         client's combo names embed the level (<c>Guardian_56_…_Overreach_IV</c>) and back the combo value.
		///     </para>
		///     <para>
		///         This is not the level a character is actually granted the spell at - retail grants at or below this,
		///         never above. Where a capture has caught the grant, <see cref="ClassSpell.GrantLevel" /> holds that
		///         and wins; this field reads more like a trainer / full-power level.
		///     </para>
		/// </remarks>
		public byte Level { get; set; }

		/// <summary>
		///     RDB field 123 (0x7B), carried raw. The client's own stat-name table (LDB category 615) names it
		///     <c>GUI Stacking Order</c>: a spell's precedence within a stacking line, so a higher order supersedes a
		///     lower one and two buffs on the same line refuse to stack. See <see cref="GuiStackingLine" /> for the line.
		/// </summary>
		/// <remarks>
		///     It is not the rank. It correlates with rank - a higher rank stacks over a lower one - but an order only
		///     has to be monotone within its own line and the scale is otherwise arbitrary: of the 883 spells with a
		///     "(Rank N)" suffix, 669 carry 1/2/3 and 214 carry a clean x10 (<c>Healing Benediction</c> is 10/20/30 on
		///     stacking line 2 while <c>Wave of Life</c> is 1/2/3 on lines 24020+). The real rank is in the name's
		///     "(Rank N)" suffix, and the RDB may not state it at all.
		/// </remarks>
		public byte GuiStackingOrder { get; set; }

		/// <summary>
		///     RDB field 128 (0x80), which the client's own stat-name table names <c>GUI Stacking Line</c>: the line a
		///     spell stacks on, with <see cref="GuiStackingOrder" /> deciding precedence within it. Two +Strength buffs
		///     on the same line refuse to stack, which is why unrelated spells share a line. Carried raw. When it is
		///     <c>ConanClass × 1000 + lineIndex</c> it is what a feat's spell modifier points at
		///     (<see cref="FeatSpellModifier.SpellLineId" />): 41000 is Necromancer line 0, and the five
		///     <c>Flesh to Worms</c> ranks all carry it, which is how a feat boosts a spell without naming a rank.
		///     0 when the record has no such field (only 3,158 of 21,502 templates carry one).
		/// </summary>
		/// <remarks>
		///     <c>ConanClass × 1000 + index</c> is the common case, not the encoding. Line <c>50000</c> is a real,
		///     working line whose high part is not a class (50 is outside the class registry): its five spells are the
		///     five ranks of <c>Inferno of Amher</c>, and the five feat modifiers pointing at it are the five ranks of
		///     <c>Blessing of Amher</c>, a 2.5/5/7.5/10/15% ladder. An id whose <c>/1000</c> is not a class is neither a
		///     corrupt row nor a reason to filter (<c>440061</c>, one spell, is the other such id).
		///     <para>
		///         The field is also overloaded: it holds small numbers (1, 2, 4, …) shared by 25-30 unrelated spells
		///         apiece - shared stacking lines, not spell lines, and not decoded further. Carried raw rather than
		///         filtered because the feat join cannot collide: a feat only ever references a class-encoded value.
		///     </para>
		/// </remarks>
		public uint GuiStackingLine { get; set; }

		/// <summary>
		///     RDB field 847 (0x34F) of the spell's type-1040005 record: the attribute this spell's magnitude scales
		///     off, as a wire stat id. Exactly three values over the 716 records that carry one -
		///     <see cref="CharacterStatId.Intelligence" /> (0x328, ×482), <see cref="CharacterStatId.Wisdom" />
		///     (0x32A, ×224) and <see cref="CharacterStatId.Strength" /> (0x324, ×10) - and it splits where a reader
		///     would expect: the Demonologist's nukes (Hellfire Stream, Shockstrike, Fires of Gehenna) on
		///     Intelligence, the curses and DoTs (Venomous Curse, Poisonous Incantation) on Wisdom. 0 when the record
		///     carries no such field, which is the overwhelming majority.
		/// </summary>
		/// <remarks>
		///     This is a per-spell override, not the general rule, which is why a 0 here is not a gap. The client
		///     picks a caster's scaling attribute by class, not by spell: its magic-damage accessor
		///     (<c>FUN_01118ce0</c>) defaults to Wisdom (0x32A) and switches to Intelligence (0x328) only for
		///     Necromancer (0x29), Herald of Xotli (0x2B) and Demonologist (0x2C) - see
		///     <c>docs/rdb/spell-scaling.md</c>. The class default covers every spell and field 847 names the
		///     handful that deviate. <c>CombatResolver.ResolveSpellDamage</c> reads it that way round.
		/// </remarks>
		public uint ScalingAttribute { get; set; }

		/// <summary>
		///     The splash triple of the spell's type-1040005 record - RDB fields 347/348/349 (0x15B/0x15C/0x15D),
		///     which the client's own stat-name table (LDB category 615) names <c>Splash Radius</c>,
		///     <c>Splash Damage</c> and <c>Splash damage max targets</c>. They travel together: all three are on the
		///     same 215 of 21,502 records, and <c>--report rdb-field-coverage --report-arg 1040005:347x348</c> pairs
		///     them row for row. Radius is in centimetre-hundredths like <see cref="Range" /> (200 = 2 m), damage is a
		///     percentage ×100 (3000 = 30%), max-targets is a raw count (0…24). 0 = the spell has no splash, which
		///     is 99% of the catalog.
		/// </summary>
		/// <remarks>
		///     <c>SplashDamage</c> is not a damage rating: a property carried by 1% of the catalog cannot be the
		///     damage of every damaging spell. Nothing in combat reads these three; they are catalog data. The spell's
		///     damage magnitude is not sourced from this record.
		/// </remarks>
		public float SplashRadius { get; set; }

		/// <inheritdoc cref="SplashRadius" />
		public float SplashDamage { get; set; }

		/// <inheritdoc cref="SplashRadius" />
		public uint SplashMaxTargets { get; set; }

		public float Range { get; set; } // target range in metres (RDB field 0x11F / 100)

		/// <summary>
		///     Damage-over-time per tick on HealthCurrent, extracted from captured <c>StatDamageIIR</c> records keyed
		///     by base spell id (the signed delta at +41; its affected stat at +33 is 0x1B HealthCurrent). Negative = a
		///     bleed debuff (Rend -8, 0x342F23 -5); 0 = not a DoT. The world tick applies this to the target's vitals
		///     and sends the <c>StatDamageIIR</c> while the debuff is active. <see cref="DotType" /> is the +45
		///     damage-school flag (1/4), carried so the rebuilt packet matches retail.
		/// </summary>
		public int DotDelta { get; set; }

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		/// <summary>
		///     The tooltip description, from LDB category 50211 keyed by the spell id - the same text the client shows
		///     in a spell/buff tooltip ("When you or your group perform a fatality and this buff is active, the Frozen
		///     Hatred pet appears…"). Null when the LDB has none for this spell (only ~3.6k of the 20k spells carry
		///     one), or when the RDB-backed <c>rdb-ldb-names</c> module has not been run (it is opt-in, needing
		///     <c>--Content:RdbDir</c>).
		/// </summary>
		/// <remarks>
		///     This is the one source that states an effect in words: the spell record carries magnitudes but not
		///     their meaning, so the description is how an effect gets identified and how a decoded magnitude gets
		///     sanity-checked. Some carry the client's inline gender markup (<c>#name{m:…|f:…}</c>) verbatim - stored
		///     as-is, it is the client's own text.
		/// </remarks>
		[MaxLength(2048)]
		public string? Description { get; set; }

		public uint DotType { get; set; }

		/// <summary>
		///     How often the DoT ticks, in milliseconds. Measured from the captures rather than lifted from the RDB: a
		///     <c>StatDamageIIR</c> carries its spell's base id at word 12, so the gap between consecutive ticks of one
		///     spell on one target is the interval. The gaps of a real DoT cluster tightly and the values are round -
		///     500 ms to 11 s. 0 = unmeasured, and the tick falls back to 2 s.
		/// </summary>
		/// <remarks>
		///     One constant does not fit: the Incinerate family ticks every 2 s, Ripper Volley and Archfiend twice as
		///     fast, Ether Drain two and a half times slower, Hellfire Breath five times slower. Measured by the
		///     <c>dot-effects</c> importer module (53 of the 84 DoT spells), so re-running the module widens coverage
		///     as the corpus grows. <c>DotIntervalEstimator</c> decides whether observed gaps establish an interval at
		///     all and rejects rather than guesses - the remaining spells have no interval because their ticks were
		///     never observed often enough.
		/// </remarks>
		public uint DotIntervalMs { get; set; }

		/// <summary>
		///     How often a channeled spell ticks while its cast bar runs, in milliseconds. From RDB field 0x1BF (447),
		///     which the client names <c>ChannelingInterval</c> and which is in centiseconds like the cast-time and
		///     duration fields, so it is stored ×10 as real ms. 521 of 21,502 records carry it (one of them the
		///     developers' own "Oysteins Channeltest"). 0 = not a channeled spell.
		/// </summary>
		/// <remarks>
		///     Distinct from <see cref="DotIntervalMs" />, which is the tick of a debuff that ticks after the cast lands
		///     and is measured off the wire. This one is stated by the client and governs the cast itself: the
		///     channeled ground AoE (<c>CombatService.ChannelGroundAoe</c>) spreads the spell's damage across
		///     <c>channel / this</c> ticks.
		/// </remarks>
		public uint ChannelingIntervalMs { get; set; }

		/// <summary>
		///     The spell's own critical chance and critical-damage bonus, both in hundredths of a percent.
		///     RDB fields 147 <c>SpellCriticalChance</c> and 129 <c>SpellCriticalDamage</c>; 1,224 and 1,228 of the
		///     21,502 records carry one. 0 = the record does not state it, and the caster's own ratings decide.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The unit is the client's, not a fit: both stat ids sit in its tooltip renderer's first
		///         percent-divisor set (<c>StatDisplayScale.Percent1</c>), so it draws each as <c>value / 100</c>.
		///         250 is 2.50% chance; 5000 is +50.00% damage.
		///     </para>
		///     <para>
		///         The damage column's modal value is 5000 on 607 of 1,228 records, i.e. a crit for +50%, which is the
		///         client-side statement behind <c>CombatResolver.BaseCritDamagePercent</c> = 150.
		///     </para>
		///     <para>
		///         Roughly half the carriers hold a bare <c>1</c> (0.01%) on one or both. That is not a sentinel to
		///         special-case: read literally it is a spell that essentially never crits, and if it did the bonus
		///         would be nothing - self-consistent, and what heals and utility spells should look like.
		///     </para>
		/// </remarks>
		public uint CriticalChanceHundredths { get; set; }

		/// <inheritdoc cref="CriticalChanceHundredths" />
		public uint CriticalDamageHundredths { get; set; }

		/// <summary>
		///     The blast radius of an area spell, in metres. Not a property field: it is a GameAction element key
		///     (<c>Radius</c>, key 11 = stat 0x2DD) on the kinds that select targets in an area, read by key out of the
		///     same [2][9] region as the recast (<c>GameActionTypes.AreaTargetKinds</c>).
		/// </summary>
		/// <remarks>
		///     Distinct from <see cref="SplashRadius" /> (field 347): splash is the collateral a single-target hit spills
		///     onto bystanders, this is the area the spell targets in the first place. A few spells carry both, and the
		///     units differ - a metre here, a metre ×100 there.
		///     <para>
		///         0 = the spell defines no area (it is single-target, or its area is server-side). Read by
		///         <c>CombatService.ApplyAoeTick</c> as the ground-AoE blast radius, falling back to its interim
		///         constant.
		///     </para>
		/// </remarks>
		public float AreaRadius { get; set; }

		/// <summary>
		///     Buff/effect duration in milliseconds. Imported from RDB field 0x008, which is in centiseconds (×10 ms,
		///     the same unit as the cast-time field; calibrated in-client: Rend 700 = 7 s, Butcher 400 = 4 s, Exhausted
		///     10000 = 100 s), so it is stored here ×10 as real ms. 0 = no self-duration on this record (an instant
		///     spell, or an ability whose timed effect lives on a separate <see cref="EffectSpell" /> record).
		/// </summary>
		/// <remarks>
		///     Drives server-side buff expiry: when a self-buff with a known duration elapses, the world tick sends the
		///     retail end-twin removal (rank byte 1, empty payload) so the icon disappears at 0 instead of sticking.
		/// </remarks>
		public uint DurationMs { get; set; }

		public uint EffectSpell { get; set; }

		// --- Pet summons. A summon spell performs no effect of its own: its GameAction region carries one
		// StartServerSideScryScript*WithParams* (53261) element whose ScriptParams string is the summon, spelled out:
		//
		//   spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781
		//
		// so the roster is read off the catalog, not listed by hand: `SummonSpawnHash != null` is "this spell summons
		// a pet", over the whole catalog rather than over the spells one capture happened to cast.

		/// <summary>
		///     The <c>spawnHash</c> of the summon's script params: a 4-character ACG token (<c>[0-9A-Z_]</c>) that does
		///     not resolve to a creature anywhere in the client - it addresses retail's server-side content, and the
		///     client neither reads nor compares it. Carried as evidence, not as a lookup: it is what makes a summon
		///     spell with no captured pet name a reportable gap instead of an invisible one. The name itself comes
		///     from the wire (<c>PetSummonExtractor</c>).
		/// </summary>
		[MaxLength(16)]
		public string? SummonSpawnHash { get; set; }

		/// <summary>
		///     Which of the two param spellings the record uses, and so whether the pet fights: the <c>spawnHash=</c>
		///     form is a combat pet, the <c>Type=Pet;Tag=</c> form the social companion. Every captured summon agrees -
		///     all 11 spawnHash pets bind with <c>PetBindings.CombatKind</c> on the wire and the one Type=Pet pet with
		///     SocialKind - so the kind is readable off the record without a capture per pet.
		/// </summary>
		public bool SummonIsCompanion { get; set; }

		/// <summary>
		///     The buff the summon applies alongside the pet, straight out of the same params string. Null on a spell
		///     that is not a summon.
		/// </summary>
		public uint? SummonPetBuff { get; set; }

		/// <summary>
		///     A self-resurrection spell this one arms, and how much of the character it restores. Both come from the
		///     client's own effect: <c>53183 AddSelfResurrectionSpell</c> carries <c>88</c> = the spell to cast on
		///     resurrect, <c>188</c> = the restore percentage, and <c>67</c> = a flag that is 1 wherever the effect
		///     appears.
		/// </summary>
		/// <remarks>
		///     The mechanic is stated by the item that carries it - the Self-Resurrection Potion's tooltip reads "an
		///     option to resurrect themselves at the location of their death, with 100% of their Health, Stamina and
		///     Mana restored... must be consumed before the death occurs... will persist until it is used". A spell
		///     with these set is not a resurrect; it is an offer the server makes at cast time
		///     (<c>DispatchResurrectRequest</c>), which the client holds and answers with <c>ResurrectMe</c> when the
		///     player dies. Null on every spell outside the four in the family (Self Resurrection + the three Elixirs
		///     of Resurgence).
		/// </remarks>
		public uint? SelfResurrectionSpell { get; set; }
		public uint? SelfResurrectionPercent { get; set; }

		/// <summary>
		///     The pet-point cost of keeping the summoned pet out, from the same params string as
		///     <see cref="SummonPetBuff" />. Null on a spell that is not a summon.
		/// </summary>
		/// <remarks>
		///     This goes on the wire: it is <c>f2</c> of the <c>PetIIR</c> owner-assign's kind triple, which is what
		///     the client's pet-point bar counts. The accounting is client-side - stat 0x1C8 <c>PetPoints</c> appears
		///     in zero updates across all 59 necromancer captures - so this triple is the only place the server ever
		///     states a cost. See <c>PetBindings.CombatKind</c>.
		/// </remarks>
		public uint? SummonPointCost { get; set; }

		// From the RDB spell-data record (type 1040005), via RdbSpellImporter:
		public ushort CastTime { get; set; } // milliseconds (RDB field 0x126 * 10)

		/// <summary>
		///     The spell's base recast (cooldown) in centiseconds - the client's own unit for it, applied as
		///     <c>value * 0.01</c> seconds by the recast timer <c>FUN_007e1080</c>.
		///     <para>
		///         Sourced from the spell record's own GameAction element region: a <c>SetSpellRecastTime</c> (53226)
		///         element keys it on the spell and a <c>SetSpellLineRecastTime</c> (53077) one on the whole GUI
		///         stacking line, both under <c>GameActionTypes.KeyValue</c> (0x2f9) - the same key the tooltip's
		///         "Recast" line reads (<c>docs/re/spell-recast-source.md</c>). A combo's reuse time is a separate
		///         fact in a separate place - the combo XML's own <c>cooldown</c> attribute (RDB 1010206,
		///         <c>Combos.Cooldown</c>, in seconds), written here by <c>RdbComboImporter</c> against the combo's
		///         <c>GameActionId</c>. The two sources are disjoint over the whole catalog, so neither can overwrite
		///         the other.
		///     </para>
		///     <para>
		///         Centiseconds, not seconds: 67 of the 3,682 shipped values are not a whole number of seconds
		///         (<c>Divine Wave</c> 2.7 s, <c>Scourge of Derketo</c> 6.5 s), and <c>Renewing Faith</c> is 1800 s,
		///         so the column needs both the precision and the range.
		///     </para>
		///     <para>
		///         This is the base. <c>ChangeRecastTime</c> (53031) / <c>ChangeLineRecastTime</c> (53069) are
		///         feat/AA modifiers the client adds to it, and are deliberately not folded in here.
		///     </para>
		/// </summary>
		public uint RecastCentiseconds { get; set; }

		/// <summary>
		///     RDB field 132 (0x084) of the spell's type-1040005 record - the client's
		///     <see cref="CharacterStatId.ManaCost" /> stat id - carried raw, because the client reads it in two modes
		///     and the selector is not in this field. 0 = the spell is free, which is retail's own reading of an
		///     absent property. 1,691 of the 20,847 catalogued spells carry a cost.
		/// </summary>
		/// <remarks>
		///     Recovered from the client. <c>FUN_008d6f70</c> - the function behind the spell tooltip's
		///     <c>ManaCostToolTip</c> line - is:
		///     <code>
		///     cost = spell.GetIntStat(0x84);                               // this field, clamped to >= 0
		///     if (spellFlags &amp; 4)                                          // the percent-of-pool mode
		///         cost = (int)(caster.GetIntStat(0x1FB) * cost * 0.01f);   // 0x1FB = ManaMax, wire units
		///     cost = (int)((modifier.Amount * 0.0001f + 1.0f) * (modifier.Value + cost));
		///     </code>
		///     and the tooltip prints <c>cost * 0.01</c>, i.e. it divides the wire's hundredths back to display units.
		///     The two modifier terms are exactly the <see cref="FeatSpellStatModifier" /> pair (an absolute
		///     <c>Value</c> plus an <c>Amount</c> in hundredths of a percent) that feats target at
		///     <see cref="CharacterStatId.ManaCost" />, which is what says this is the real cast-cost path and not a
		///     display-only accessor.
		///     <para>
		///         With the flag set the value is a percentage of the caster's mana pool (<c>Ice Strike</c> 9 = 9%),
		///         and without it an absolute cost in wire hundredths (<c>Dark Knowledge (Rank 4)</c> 42200 = 422
		///         mana). Which of the two is in force is <see cref="ManaCostIsPercent" />; the record states it, so
		///         nothing is inferred from the magnitude.
		///     </para>
		/// </remarks>
		public ushort ManaCost { get; set; }

		/// <summary>
		///     Bit 2 of the spell record's flags word (body byte 52), which is the word the client keeps at
		///     <c>spell + 0xC8</c> and tests as <c>*(uint*)(spell + 0xC8) &gt;&gt; 2 &amp; 1</c> to choose how to read
		///     <see cref="ManaCost" />: set = a percentage of the caster's mana pool, clear = an absolute cost in wire
		///     hundredths.
		/// </summary>
		/// <remarks>
		///     The flags word sits outside the record's ascending property run (which is only ~16 fields wide and
		///     starts at word 15). Bit 2 of word 13 is set on 995 of the catalog's percent-mode spells and on zero
		///     flat-mode ones, and the mode is per record, not per line: <c>Dark Knowledge</c> rank 1 (10%) has the
		///     bit set while ranks 2-4 (128/265/422 flat) have it clear. A magnitude heuristic (<c>&lt;= 100</c> is a
		///     percent) disagrees with the bit on 215 of 1,713 records, all tiny flat costs (mostly a literal 1, i.e.
		///     0.01 mana) plus the catalog's test spells; the bit is right on all of them.
		/// </remarks>
		public bool ManaCostIsPercent { get; set; }
	}
}
