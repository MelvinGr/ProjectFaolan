namespace Faolan.Core.Enums
{
	/// <summary>
	///     The damage SCHOOL an effect belongs to - the ATTACKER's element, not a hit result. Carried by
	///     <c>DamageResultIIR</c> (0xFEC4158A) as its <c>type</c> field and by <c>StatDamageIIR</c> (0xE9F24184) in the
	///     word after its delta. RE'd across all captures (2026-07-22): it is CONSTANT per attacker, the two trailing
	///     wire fields are always 0, and there is no crit/miss flag - a crit is conveyed only by the larger number, and
	///     misses aren't sent as DamageResult at all.
	/// </summary>
	/// <remarks>
	///     <para>
	///         ⚠️ <b>Cold used to sit at 5, and everything after it was one too high (#487).</b> That order came from
	///         reading a client list as <i>Slashing, Piercing, Crushing, Fire, <b>Cold</b>, Electrical, Holy, …</i> —
	///         which made a necromancer's Pestilential Blast decode as <b>Holy</b> and their Ice Strike as <b>Siege</b>.
	///         The client's own combat log (<c>/logcombat on</c>, recorded alongside the pcap in
	///         <c>NecroL80_mage-necromancer_login_combat2</c>) names them <c>unholy damage</c> = <b>7</b> and
	///         <c>frost damage</c> = <b>10</b>, which the old numbering cannot produce.
	///     </para>
	///     <para>
	///         <b>The client's own stat table settles it, unanimously: COLD IS NOT IN THE RUN.</b> Every per-school
	///         stat family in LDB category 615 lists the same nine schools in the same order, with Cold nowhere among
	///         them - its stats are parked separately in the 157-167 block (Protection (Cold) 157, Cold Damage 161,
	///         Cold Invulnerability 167):
	///     </para>
	///     <code>
	///     Invulnerability  902 Slashing · 903 Piercing · 904 Crushing · 905 Fire · 906 Electrical · 907 Holy · 908 Unholy · 909 Poison · 910 Siege
	///     Damage           995 Slashing · 996 Piercing · 997 Crushing · 998 Fire ·      …        · 1000 Holy · 1001 Unholy · 1002 Poison · 1003 Siege
	///     Damage (ranged)          …    ·  988 Crushing ·  989 Fire · 990 Electrical · 991 Holy · 992 Unholy · 993 Poison · 994 Siege
	///     Protection       926 Fire · 927 Electrical · 928 Holy · 929 Unholy · 931 poison
	///     Combat Rating   1007 Fire · 1008 Electrical · 1009 Holy · 1010 Unholy · 1011 Poison
	///     </code>
	///     <para>
	///         Numbering that run from 1 gives <b>Unholy = 7</b> and leaves <b>Cold = 10</b> after Siege - reproducing
	///         both combat-log observations exactly, with no free parameter to tune.
	///     </para>
	///     <para>
	///         ⚠️ The old reading was an honest mistake and the trap is still in the data: the <b>PvP</b> invulnerability
	///         block DOES carry Cold in fifth place (1115 Slashing, 1116 Piercing, 1117 Crushing, 1118 Fire,
	///         <b>1119 Cold</b>, 1120 Electrical, 1121 Holy). The client holds two orderings and only the non-PvP one
	///         matches the wire, so do not re-derive this from a PvP block.
	///     </para>
	///     <para>
	///         Corpus-wide the field takes 0-8, 10, 12 and 23 - never 9 (Siege: no captured fight deals it) and never
	///         11. <see cref="Environmental" /> and <see cref="Heal" /> are outside the school range proper; see their
	///         own notes.
	///     </para>
	///     <para>
	///         The two records share ONE numbering. <c>DamageResultIIR</c>'s only capture-confirmed values are the
	///         PHYSICAL schools (Slashing = 1 matches the log for both the Zingaran Stockpiler and the Hired Bodyguard),
	///         and those are identical under the old order and this one - so no observation ever distinguished them and
	///         there is no evidence for two separate tables.
	///     </para>
	///     <para>
	///         The client (<c>FUN_0149a8f0</c>) treats <c>1..14</c> as valid and renders <c>0</c> or <c>&gt;14</c> as
	///         "GenericDamage". The player's physical melee is capture-observed as <see cref="Crushing" /> -
	///         weapon-dependent in retail, but we don't model per-weapon schools.
	///     </para>
	/// </remarks>
	public enum DamageSchool : uint
	{
		Generic = 0,
		Slashing = 1,
		Piercing = 2,
		Crushing = 3,
		Fire = 4,
		Electrical = 5,
		Holy = 6,
		Unholy = 7,
		Poison = 8,

		/// <summary>Never observed - no captured fight deals siege damage - but its slot is pinned by the stat table.</summary>
		Siege = 9,

		/// <summary>Appended AFTER the nine, not inserted at 5. The client's combat log calls it <c>frost damage</c>.</summary>
		Cold = 10,

		/// <summary>
		///     The WORLD as the damage source - fall damage and hazards (#495/#504). Not a school at all: these records
		///     carry no caster, no spell and no accompanying <c>DamageResultIIR</c>, and 12 sits outside the nine
		///     schools plus Cold. All 28 corpus occurrences are falls.
		/// </summary>
		Environmental = 12,

		/// <summary>
		///     Not damage. Of 110 corpus occurrences <b>every one</b> carries a non-negative delta, while every other
		///     value here also appears with a negative one - so this marks a RESTORE (health/mana regen) riding the same
		///     record. Named for what the evidence shows rather than left as an unexplained outlier.
		/// </summary>
		Heal = 23
	}
}
