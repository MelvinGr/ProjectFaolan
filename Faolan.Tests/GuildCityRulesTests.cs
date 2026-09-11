using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Progression;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The guild-city / Battlekeep rules and the minigame arena catalog.
	//
	// Both are decision-layer only - no capture in the corpus carries a siege or minigame message, so nothing here
	// is sent. What these gates protect is the transcription: every constant is a quoted client string, and the
	// failure mode for a table of quoted numbers is that someone "tidies" one into a rounder value. So the tests
	// assert the numbers retail states, and - more usefully - the boundaries, which is where a rule read slightly
	// wrong shows up first.
	public class GuildCityRulesTests
	{
		private static GuildCityState Guild(int members = 24, bool spot = true, bool keep = true, int tier = 3, bool battlekeep = false)
		{
			return new GuildCityState(members, spot, keep, tier, battlekeep);
		}

		[Fact]
		public void TheQuotedConstants_AreWhatTheClientSays()
		{
			Assert.Equal(24, GuildCityRules.MinimumMembersToBuildACity);
			Assert.Equal(3, GuildCityRules.TierRequiredToFight);
			Assert.Equal(TimeSpan.FromHours(2), GuildCityRules.VulnerabilityWindow);
			Assert.Equal(TimeSpan.FromMinutes(15), GuildCityRules.WindowMoveGrace);
		}

		[Theory]
		[InlineData(23, false)] // one short
		[InlineData(24, true)] // "24 or more members"
		[InlineData(25, true)]
		public void TheMemberGate_IsAtLeast24_NotMoreThan24(int members, bool allowed)
		{
			Assert.Equal(allowed, GuildCityRules.CanBuild(Guild(members: members), isTheKeep: true).IsAllowed);
		}

		[Fact]
		public void BookingRefusals_NameTheRightReason()
		{
			Assert.Equal(GuildCityRefusal.AlreadyHasACitySpot, GuildCityRules.CanBookCitySpot(Guild(spot: true), freeCitySpots: 5).Refusal);
			Assert.Equal(GuildCityRefusal.NoFreeCitySpots, GuildCityRules.CanBookCitySpot(Guild(spot: false), freeCitySpots: 0).Refusal);
			Assert.Equal(GuildCityRefusal.TooFewMembers, GuildCityRules.CanBookCitySpot(Guild(members: 10, spot: false), freeCitySpots: 5).Refusal);
			Assert.True(GuildCityRules.CanBookCitySpot(Guild(spot: false), freeCitySpots: 1).IsAllowed);
		}

		[Fact]
		public void TheKeepGatesEveryOtherBuilding_ButNotItself()
		{
			// "without it, no other buildings can be raised" - so the Keep is buildable on an empty spot and nothing
			// else is.
			Assert.True(GuildCityRules.CanBuild(Guild(keep: false), isTheKeep: true).IsAllowed);
			Assert.Equal(GuildCityRefusal.KeepNotBuilt, GuildCityRules.CanBuild(Guild(keep: false), isTheKeep: false).Refusal);
			Assert.True(GuildCityRules.CanBuild(Guild(keep: true), isTheKeep: false).IsAllowed);

			// And no spot means nothing at all, Keep included.
			Assert.Equal(GuildCityRefusal.NoCitySpot, GuildCityRules.CanBuild(Guild(spot: false), isTheKeep: true).Refusal);
		}

		[Theory]
		[InlineData(2, false)]
		[InlineData(3, true)]
		public void AttackingNeedsATier3City(int tier, bool allowed)
		{
			Assert.Equal(allowed, GuildCityRules.CanApplyForBattlekeep(Guild(tier: tier)).IsAllowed);
		}

		[Fact]
		public void AGuildThatAlreadyHoldsAKeep_MayNotFightForAnother()
		{
			// Checked before the tier gate, because it is the more specific refusal and it is the one retail's own
			// string is about ("You may not fight for another one").
			Assert.Equal(GuildCityRefusal.AlreadyOwnsABattlekeep, GuildCityRules.CanApplyForBattlekeep(Guild(tier: 3, battlekeep: true)).Refusal);
		}

		[Fact]
		public void TheVulnerabilityWindow_IsTwoHours_HalfOpen()
		{
			var opens = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);

			Assert.False(GuildCityRules.IsVulnerable(opens, opens.AddSeconds(-1)));
			Assert.True(GuildCityRules.IsVulnerable(opens, opens));
			Assert.True(GuildCityRules.IsVulnerable(opens, opens.AddMinutes(119)));

			// The instant the window closes is not vulnerable - a half-open interval, so two back-to-back windows
			// cannot both contain the same moment.
			Assert.False(GuildCityRules.IsVulnerable(opens, opens.AddHours(2)));
		}

		[Fact]
		public void TheWindowMayOnlyBeMovedInsideTheFifteenMinuteGrace()
		{
			var resolved = new DateTimeOffset(2026, 8, 20, 22, 0, 0, TimeSpan.Zero);

			Assert.True(GuildCityRules.CanMoveVulnerabilityWindow(resolved, resolved.AddMinutes(14), changesRemaining: 1).IsAllowed);
			Assert.True(GuildCityRules.CanMoveVulnerabilityWindow(resolved, resolved.AddMinutes(15), changesRemaining: 1).IsAllowed);
			Assert.Equal(GuildCityRefusal.OutsideTheWindowMoveGrace,
				GuildCityRules.CanMoveVulnerabilityWindow(resolved, resolved.AddMinutes(16), changesRemaining: 1).Refusal);

			// No battle has resolved: there is no grace to be inside of.
			Assert.Equal(GuildCityRefusal.OutsideTheWindowMoveGrace,
				GuildCityRules.CanMoveVulnerabilityWindow(null, resolved, changesRemaining: 1).Refusal);

			// The allowance is retail's, not ours - the rule is only that running out refuses.
			Assert.Equal(GuildCityRefusal.NoWindowChangesLeft,
				GuildCityRules.CanMoveVulnerabilityWindow(resolved, resolved.AddMinutes(1), changesRemaining: 0).Refusal);
		}

		[Fact]
		public void ASiegeCampNeedsAVulnerableTarget_AndHoldsOneWeapon()
		{
			Assert.True(GuildCityRules.CanRaiseSiegeCamp(targetIsVulnerable: true, campAlreadyHasAWeapon: false).IsAllowed);
			Assert.Equal(GuildCityRefusal.TargetNotVulnerable,
				GuildCityRules.CanRaiseSiegeCamp(targetIsVulnerable: false, campAlreadyHasAWeapon: false).Refusal);
			Assert.Equal(GuildCityRefusal.CampAlreadyHasAWeapon,
				GuildCityRules.CanRaiseSiegeCamp(targetIsVulnerable: true, campAlreadyHasAWeapon: true).Refusal);
		}

		[Fact]
		public void RepairAndBuildAreBothRefusedMidBattle()
		{
			Assert.Equal(GuildCityRefusal.SiegeBattleInProgress, GuildCityRules.CanRepairOrBuildDuringBattle(aBattleIsRunning: true).Refusal);
			Assert.True(GuildCityRules.CanRepairOrBuildDuringBattle(aBattleIsRunning: false).IsAllowed);
		}

		// --- The minigame arenas -------------------------------------------------------------------------

		[Fact]
		public async Task EveryArena_IsAPlayfieldWeHold()
		{
			// The arena ids come from the client's RDB 1000512; our Maps come from captures. A name that matches
			// across the two is a real cross-check, not a tautology.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			foreach (var arena in MinigameCatalog.Arenas.Values)
			{
				var map = await db.Maps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == arena.MapId, ct);

				Assert.True(map != null, $"arena {arena.MapId} ({arena.Name}) is not in Maps");
				Assert.Equal(arena.Name, map!.Name);
			}
		}

		[Fact]
		public void OnlyTheTwoArenasTheClientDescribes_CarryAMode()
		{
			// The point of this gate: three of the five arenas have no LDB 10015 rules entry, and the honest state
			// is Unknown. If someone later fills them in, this fails and they have to say where the mode came from.
			Assert.Equal(5, MinigameCatalog.Arenas.Count);

			Assert.Equal(MinigameMode.TeamAnnihilation, MinigameCatalog.ArenaFor(5002)!.Mode);
			Assert.Equal(MinigameMode.CaptureTheSkull, MinigameCatalog.ArenaFor(5201)!.Mode);

			Assert.Equal([5005u, 5006u, 5012u],
				MinigameCatalog.Arenas.Values.Where(a => a.Mode == MinigameMode.Unknown).Select(a => a.MapId).OrderBy(id => id));

			Assert.Null(MinigameCatalog.ArenaFor(4000)); // the beach is not an arena
			Assert.False(MinigameCatalog.IsArena(4000));
		}

		[Fact]
		public void TotemTorrentsRules_AreTheOnesTheClientWritesOut()
		{
			Assert.Equal(2, MinigameCatalog.TotemTorrentRules.Teams);
			Assert.Equal(2, MinigameCatalog.TotemTorrentRules.ControlPoints);
			Assert.True(MinigameCatalog.TotemTorrentRules.RespawnRequiresYourTotem);
			Assert.True(MinigameCatalog.TotemTorrentRules.ControlPointsHardenTheTotem);
			Assert.True(MinigameCatalog.TotemTorrentRules.HasCentralHealZone);

			// The tiebreak applies only "if both teams are alive when the time is up" - a wiped side loses
			// outright, so this must never be read as the win condition.
			Assert.True(MinigameCatalog.TotemTorrentRules.MostKillsWinsOnTime);
		}
	}
}
