using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Raid;
using Faolan.Core.Network.Codecs.Team;
using Xunit;

namespace Faolan.Tests
{
	// The raid wire layer. There is no raid traffic anywhere in the corpus - no captured session ever formed
	// one - so these gates cannot be byte-exact against retail the way TeamTradeFriendCodecTests is. What they can
	// do, and what the ticket's whole case rests on, is pin that every raid body is made of pieces retail did send:
	//
	//   * `cTeam`   - the same record DispatchYouJoinedTeam carries, byte-exact against f693;
	//   * `cCharacter` - the same member record, byte-exact against f678/f680;
	//   * ID32 / wire string / the 0x3F1 list frame - all capture-pinned primitives.
	//
	// So the assertions below are of the form "the raid message equals the captured team bytes", plus round-trips
	// for the two shapes that have no team twin.
	public class RaidCodecTests
	{
		// The Demonologist's member record exactly as retail sent it (f678) - see TeamTradeFriendCodecTests, which
		// owns the provenance. Repeated here rather than shared so each file states the bytes it is gating.
		private const string MemberHex = "0000c350013184ae000a53747269636b656e6e650000002c0000000500000fb400000003000000000000000600000000000001620000c79c00000fb4" +
										"0000000000000000000000000000000100009c5001eda8b2000000030159ea5c54000000000000000000";

		private static TeamMemberRecord Member()
		{
			return TeamMemberRecordCodec.Parse(Convert.FromHexString(MemberHex));
		}

		private static TeamRoster Roster(uint teamId = 0x0051DD9A)
		{
			return new TeamRoster(teamId, 0x013184AE, [Member()], TeamSettings.Default);
		}

		// The roster's trailing 18 bytes are not one opaque `DefaultLootSettings` blob: the client's reader splits
		// them, and the split is corroborated by TeamSetLootOption's own captured c2s body (`00000000 01 00000002`) - mode
		// 0, need/greed on, threshold 2, which is exactly what falls out of the captured roster.
		[Fact]
		public void TheRosterTrailer_IsTheLootSettingsBlock()
		{
			var settings = TeamRosterCodec.Parse(Convert.FromHexString("0000dea90051dd9a0000c350013184ae000007e2" + MemberHex + "000000000100000000000000000000000002")).Settings;

			Assert.Equal(0u, settings.LootMode);
			Assert.True(settings.UseNeedGreed);
			Assert.False(settings.Flag);
			Assert.Equal(0u, settings.MasterLooter.Id); // no master looter in any captured roster
			Assert.Equal(2u, settings.LootThreshold);
			Assert.Equal(TeamSettings.Default, settings);
		}

		// DispatchRaidTeamJoined / DispatchRaidTeamUpdate carry a `cTeam`, and the client reads all three through
		// FUN_00557960 - so a raid announcing a team must be byte-identical to the captured team roster for the same
		// team. This is the load-bearing claim of the whole ticket: the raid messages are not new bodies.
		[Fact]
		public void ARaidTeamAnnouncement_IsTheCapturedTeamRosterBody()
		{
			var expected = "0000dea90051dd9a" + "0000c350013184ae" + "000007e2" + MemberHex + "000000000100000000000000000000000002";

			Assert.Equal(expected, Convert.ToHexString(TeamRosterCodec.Build(Roster())).ToLowerInvariant());
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidTeamJoined, TeamRosterCodec.HandledMethods);
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidTeamUpdate, TeamRosterCodec.HandledMethods);
		}

		// Every `i_cWho` / `i_cCharacter` raid dispatch is a bare member record - the client reads each with the same
		// cCharacter reader the team member vector uses.
		[Fact]
		public void ARaidCharacterDispatch_IsABareMemberRecord()
		{
			Assert.Equal(MemberHex, Convert.ToHexString(TeamMemberRecordCodec.Build(Member())).ToLowerInvariant());
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidCharacterUpdate, TeamMemberRecordCodec.HandledMethods);
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidNewMasterLooter, TeamMemberRecordCodec.HandledMethods);
		}

		// DispatchRaidInvite is DispatchTeamInvite's body - the member then the note - which the captured team invite
		// pins to the byte.
		[Fact]
		public void TheRaidInvitePush_IsTheCapturedTeamInviteBody()
		{
			Assert.Equal(MemberHex + "0000", Convert.ToHexString(TeamInviteCodec.Build(Member())).ToLowerInvariant());
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidInvite, TeamInviteCodec.HandledMethods);
		}

		// cMatch: a header cTeam, the member teams, the per-character flag map, then the tail. Nothing captured, so
		// the gate is the round-trip - which is what catches a list frame or a field width going wrong.
		[Fact]
		public void RaidMatch_RoundTrips()
		{
			var match = new RaidMatch(
				new TeamRoster(0x00600001, 0x013184AE, [], TeamSettings.Default),
				[Roster(0x0051DD9A), Roster(0x0051DD9B)],
				[new RaidMemberFlags(EntityIds.Character(0x013184AE), true, false)],
				new RaidMatchTail(EntityIds.Character(0x013184AE), 7, true, EntityIds.Team(0x0051DD9A), 1, 2, 3));

			var built = RaidMatchCodec.Build(match);
			var parsed = RaidMatchCodec.Parse(built);

			Assert.Equal(2, parsed.Teams.Count);
			Assert.Equal(match.MemberFlags, parsed.MemberFlags);
			Assert.Equal(match.Tail, parsed.Tail);
			Assert.Equal(Convert.ToHexString(built), Convert.ToHexString(RaidMatchCodec.Build(parsed)));
		}

		// An empty raid is still a legal cMatch, and it is the one the list frames get wrong first: a zero-element
		// list is 0x3F1, which reads as a constant if you have only ever seen one with members in it.
		[Fact]
		public void AnEmptyRaidMatch_FramesItsListsAsEmptyRatherThanOmittingThem()
		{
			var built = RaidMatchCodec.Build(new TeamRoster(0x00600001, 0, [], TeamSettings.Default), []);

			// [ID32 raid][ID32 leader][empty member frame][settings] [empty team frame] [empty flag frame] [33-byte tail]
			Assert.Equal("0000dea9006000010000c35000000000000003f1000000000100000000000000000000000002" + "000003f1" + "000003f1" + new string('0', 66),
				Convert.ToHexString(built).ToLowerInvariant());
			Assert.Empty(RaidMatchCodec.Parse(built).Teams);
		}

		[Fact]
		public void RaidInviteRequest_RoundTrips()
		{
			var built = RaidInviteRequestCodec.Build(new RaidInviteRequest(EntityIds.Character(0x013184AE), "join us"));

			Assert.Equal("0000c350013184ae0007" + Convert.ToHexString("join us"u8).ToLowerInvariant(), Convert.ToHexString(built).ToLowerInvariant());
			Assert.Equal("join us", RaidInviteRequestCodec.Parse(built).Message);
		}

		[Fact]
		public void RaidInviteByNameRequest_RoundTrips()
		{
			var built = RaidInviteByNameRequestCodec.Build(new RaidInviteByNameRequest("Strickenne", ""));
			var parsed = RaidInviteByNameRequestCodec.Parse(built);

			Assert.Equal("Strickenne", parsed.CharacterName);
			Assert.Equal("", parsed.Message);
			Assert.Equal(14, built.Length); // [u16 10]"Strickenne"[u16 0]
		}

		// Move and swap share one layout, so the codec declares both rather than picking one: the second ID32 is a
		// team on the move and a character on the swap, which is why its field is named for the slot.
		[Fact]
		public void RaidRosterEdit_RoundTripsAndIsSharedByBothVerbs()
		{
			var built = RaidRosterEditCodec.Build(new RaidRosterEdit(EntityIds.Character(0x013184AE), EntityIds.Team(0x0051DD9A)));

			Assert.Equal("0000c350013184ae0000dea90051dd9a", Convert.ToHexString(built).ToLowerInvariant());
			Assert.Equal(new List<CSPlayerAgentMethods> { CSPlayerAgentMethods.RaidMoveTeamMember, CSPlayerAgentMethods.RaidSwapCharacters }, RaidRosterEditCodec.HandledMethods);
		}
	}
}
