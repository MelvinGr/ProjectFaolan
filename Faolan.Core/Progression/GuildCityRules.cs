using System;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The guild-city and Battlekeep RULES, as the client states them (#147).
	/// </summary>
	/// <remarks>
	///     This is the DECISION layer only — it puts nothing on the wire, and deliberately so: not one siege or
	///     city message occurs anywhere in the capture corpus, so every body that would carry these outcomes is
	///     unknown. What IS known is what the server has to decide, because retail says it in its own words, in
	///     ordinary user-facing LDB text. Each rule below cites the string it comes from; the prose is in
	///     <c>docs/subsystems/guild-cities-and-sieges.md</c>.
	///     <para>
	///         ⚠️ So this type answers "may they?" and "when?", never "what do we send?". Wiring it to a message is
	///         blocked on a capture; encoding the rules is not, and doing it now is what keeps them from being
	///         re-derived or quietly re-guessed.
	///     </para>
	/// </remarks>
	public static class GuildCityRules
	{
		/// <summary>
		///     Members a guild needs before it may build a city.
		///     <para>[LDB 50020/606: <i>"Only available to guilds with 24 or more members."</i>]</para>
		/// </summary>
		public const int MinimumMembersToBuildACity = 24;

		/// <summary>
		///     The city tier required to apply for Massive PvP, and to build or capture a Battlekeep — the same
		///     number for both, and the largest tier there is.
		///     <para>
		///         [LDB 100/87649219: <i>"…your guild city is now Tier 3. You may apply for Massive PvP battles."</i>;
		///         LDB 10017/38: <i>"To build or capture a battlekeep …your guild must first possess a Tier 3 city
		///         keep."</i>; LDB 50020/691 calls Tier 3 <i>"the largest possible"</i>.]
		///     </para>
		/// </summary>
		public const int TierRequiredToFight = 3;

		/// <summary>The largest city tier. Same value as <see cref="TierRequiredToFight" />, for a different reason.</summary>
		public const int MaximumCityTier = 3;

		/// <summary>
		///     How long a Battlekeep stays vulnerable once its window opens.
		///     <para>[LDB 50020/691: <i>"These vulnerability windows are only 2 hours long."</i>]</para>
		/// </summary>
		public static readonly TimeSpan VulnerabilityWindow = TimeSpan.FromHours(2);

		/// <summary>
		///     How long after a battle resolves the controlling leader may MOVE the next window.
		///     <para>
		///         [LDB 100/14454: <i>"The vulnerability window can only be set in the 15 minutes after winning a
		///         battle."</i>; LDB 50020/691 states the same 15 minutes for a city conquered or an invading army
		///         repelled.]
		///     </para>
		/// </summary>
		public static readonly TimeSpan WindowMoveGrace = TimeSpan.FromMinutes(15);

		/// <summary>
		///     Whether <paramref name="guild" /> may book a city spot, and why not when it may not. Booking is the
		///     first step of the chain and the only one with a captured refusal SET, which is what a bookable finite
		///     resource looks like [LDB 100/20200, 100/20201, 100/129749027].
		/// </summary>
		/// <param name="freeCitySpots">
		///     How many spots the realm has left. ⚠️ Not a rule this type owns — retail's supply is content we do not
		///     hold, so the caller states it.
		/// </param>
		public static GuildCityVerdict CanBookCitySpot(GuildCityState guild, int freeCitySpots)
		{
			ArgumentNullException.ThrowIfNull(guild);

			if (guild.HasCitySpot)
				return GuildCityVerdict.Refused(GuildCityRefusal.AlreadyHasACitySpot);

			if (freeCitySpots <= 0)
				return GuildCityVerdict.Refused(GuildCityRefusal.NoFreeCitySpots);

			// ⚠️ The member gate is stated against BUILDING a city, not against booking. Applied here anyway because
			// booking with no way to build is a dead end, and refusing early is the reading that matches the client
			// hiding the whole feature below 24 members. If a capture ever shows a 20-member guild booking, this is
			// the line to move. [V0: whether the 24-member gate is on booking or only on building.]
			return guild.MemberCount < MinimumMembersToBuildACity
				? GuildCityVerdict.Refused(GuildCityRefusal.TooFewMembers)
				: GuildCityVerdict.Allowed;
		}

		/// <summary>
		///     Whether the guild may raise a building. The Keep comes first and gates every other structure.
		///     <para>
		///         [LDB 100/215909: <i>"This building must be built to claim an area as yours, and without it, no
		///         other buildings can be raised."</i>]
		///     </para>
		/// </summary>
		public static GuildCityVerdict CanBuild(GuildCityState guild, bool isTheKeep)
		{
			ArgumentNullException.ThrowIfNull(guild);

			if (!guild.HasCitySpot)
				return GuildCityVerdict.Refused(GuildCityRefusal.NoCitySpot);

			if (guild.MemberCount < MinimumMembersToBuildACity)
				return GuildCityVerdict.Refused(GuildCityRefusal.TooFewMembers);

			return isTheKeep || guild.HasKeep
				? GuildCityVerdict.Allowed
				: GuildCityVerdict.Refused(GuildCityRefusal.KeepNotBuilt);
		}

		/// <summary>
		///     Whether the guild may apply to attack a Battlekeep. Two gates, both quoted: a Tier 3 city, and no
		///     Battlekeep already held.
		/// </summary>
		public static GuildCityVerdict CanApplyForBattlekeep(GuildCityState guild)
		{
			ArgumentNullException.ThrowIfNull(guild);

			if (guild.OwnsBattlekeep)
				return GuildCityVerdict.Refused(GuildCityRefusal.AlreadyOwnsABattlekeep);

			return guild.CityTier < TierRequiredToFight
				? GuildCityVerdict.Refused(GuildCityRefusal.CityNotTier3)
				: GuildCityVerdict.Allowed;
		}

		/// <summary>
		///     Whether a Battlekeep is vulnerable at <paramref name="now" /> — i.e. inside the two-hour window that
		///     opened at <paramref name="windowOpensAt" />.
		/// </summary>
		public static bool IsVulnerable(DateTimeOffset windowOpensAt, DateTimeOffset now)
		{
			return now >= windowOpensAt && now < windowOpensAt + VulnerabilityWindow;
		}

		/// <summary>
		///     Whether the controlling leader may MOVE the next vulnerability window. Legal only inside the
		///     fifteen-minute grace that follows a resolved battle, and only while changes remain.
		/// </summary>
		/// <param name="lastBattleResolvedAt">
		///     When the city was conquered or the invaders repelled. Null = no battle has resolved, so no grace is
		///     open and the window sits wherever the default put it.
		/// </param>
		/// <param name="changesRemaining">
		///     ⚠️ Retail caps this — <i>"There are no more changes left to the vulnerability window."</i>
		///     [LDB 100/245141412] — but never states the allowance, so the caller supplies it rather than this type
		///     inventing a number. [V0: how many window changes a guild gets.]
		/// </param>
		public static GuildCityVerdict CanMoveVulnerabilityWindow(DateTimeOffset? lastBattleResolvedAt, DateTimeOffset now, int changesRemaining)
		{
			if (changesRemaining <= 0)
				return GuildCityVerdict.Refused(GuildCityRefusal.NoWindowChangesLeft);

			if (lastBattleResolvedAt is not { } resolved || now < resolved || now - resolved > WindowMoveGrace)
				return GuildCityVerdict.Refused(GuildCityRefusal.OutsideTheWindowMoveGrace);

			return GuildCityVerdict.Allowed;
		}

		/// <summary>
		///     Whether a siege camp may be raised. Two rules, both from the siege-camp strings: the target must be
		///     vulnerable, and a camp holds ONE active weapon.
		///     <para>
		///         [LDB 50018/116765824: <i>"You can only build a siege camp while the city is vulnerable."</i>;
		///         LDB 50018/116771712: <i>"This siege camp already has an active siege weapon!"</i>]
		///     </para>
		/// </summary>
		public static GuildCityVerdict CanRaiseSiegeCamp(bool targetIsVulnerable, bool campAlreadyHasAWeapon)
		{
			if (!targetIsVulnerable)
				return GuildCityVerdict.Refused(GuildCityRefusal.TargetNotVulnerable);

			return campAlreadyHasAWeapon
				? GuildCityVerdict.Refused(GuildCityRefusal.CampAlreadyHasAWeapon)
				: GuildCityVerdict.Allowed;
		}

		/// <summary>
		///     Repair and construction are BOTH refused for the duration of a siege battle — two separate strings
		///     saying the same thing about the two actions.
		///     <para>[LDB 100/14456 (repair), 100/14457 (build)]</para>
		/// </summary>
		public static GuildCityVerdict CanRepairOrBuildDuringBattle(bool aBattleIsRunning)
		{
			return aBattleIsRunning
				? GuildCityVerdict.Refused(GuildCityRefusal.SiegeBattleInProgress)
				: GuildCityVerdict.Allowed;
		}
	}

	/// <summary>
	///     The guild state these rules read. A snapshot passed in rather than fetched, because nothing persists a
	///     city yet — when something does, this is the shape it has to be able to produce.
	/// </summary>
	/// <param name="MemberCount">Roster size, which is what the 24-member gate counts.</param>
	/// <param name="HasCitySpot">Whether a spot has been booked.</param>
	/// <param name="HasKeep">Whether the Keep is built — the building that gates every other building.</param>
	/// <param name="CityTier">1..<see cref="GuildCityRules.MaximumCityTier" />; 0 for a spot with nothing on it.</param>
	/// <param name="OwnsBattlekeep">A guild may hold at most one.</param>
	public sealed record GuildCityState(int MemberCount, bool HasCitySpot, bool HasKeep, int CityTier, bool OwnsBattlekeep);

	/// <summary>Allowed, or refused with the reason retail would have shown.</summary>
	public sealed record GuildCityVerdict(bool IsAllowed, GuildCityRefusal Refusal)
	{
		public static readonly GuildCityVerdict Allowed = new GuildCityVerdict(true, GuildCityRefusal.None);

		public static GuildCityVerdict Refused(GuildCityRefusal refusal)
		{
			return new GuildCityVerdict(false, refusal);
		}
	}

	/// <summary>
	///     Why a guild-city or siege action was refused. Each member names a REAL client string, so when the
	///     message that carries one is finally captured there is a decision here to attach it to — and until then
	///     nothing is sent, because the body is unknown.
	/// </summary>
	public enum GuildCityRefusal
	{
		None = 0,

		/// <summary>LDB 50020/606 — "Only available to guilds with 24 or more members."</summary>
		TooFewMembers,

		/// <summary>LDB 100/20200 — "Booking failed. There are no more free city spots."</summary>
		NoFreeCitySpots,

		/// <summary>LDB 100/20201 — "Booking failed. Your guild already has a city spot."</summary>
		AlreadyHasACitySpot,

		/// <summary>LDB 100/176329231 — "Your guild does not have a city spot."</summary>
		NoCitySpot,

		/// <summary>LDB 100/215909 — the Keep "must be built to claim an area as yours".</summary>
		KeepNotBuilt,

		/// <summary>LDB 100/212940409 — "Your PvE guild city needs to be Tier 3 before you can perform this action."</summary>
		CityNotTier3,

		/// <summary>LDB 100/164371129 — "Your guild already owns a battlekeep. You may not fight for another one."</summary>
		AlreadyOwnsABattlekeep,

		/// <summary>LDB 100/245141412 — "There are no more changes left to the vulnerability window."</summary>
		NoWindowChangesLeft,

		/// <summary>LDB 100/14454 — the window "can only be set in the 15 minutes after winning a battle".</summary>
		OutsideTheWindowMoveGrace,

		/// <summary>LDB 50018/116765824 — "You can only build a siege camp while the city is vulnerable."</summary>
		TargetNotVulnerable,

		/// <summary>LDB 50018/116771712 — "This siege camp already has an active siege weapon!"</summary>
		CampAlreadyHasAWeapon,

		/// <summary>LDB 100/14456 / 100/14457 — no repairing and no building during a siege battle.</summary>
		SiegeBattleInProgress
	}
}
