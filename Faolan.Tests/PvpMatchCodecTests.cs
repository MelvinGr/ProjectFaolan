using System;
using Faolan.Core.Network.Codecs.Pvp;
using Faolan.Core.Network.Codecs.Team;
using Xunit;

namespace Faolan.Tests
{
	// The custom-match payloads: PvPMatchRules_c and the i_cMatch it rides in. Zero captured instances
	// exist (no captured session ever creates or fills a match), so there is no golden to pin - the layout is
	// client-recovered (reader FUN_01346ef0 / writer FUN_00558880 / copier FUN_00547070 agree; see the codecs and
	// docs/re/minigame-massivepvp-messages.md). What can be gated without a capture is what these do: the
	// round-trip, the Silent gate on the leader ID32 tail, and the wire size arithmetic the client's own struct
	// stride implies. The first captured match body replaces this with a real golden.
	public class PvpMatchCodecTests
	{
		private static PvpMatchRules Rules(bool silent)
		{
			// The community-server debug command's own defaults (2 teams of 3..6, 900 s, the map word the debug view
			// shows) - values the client ships, not invented ones.
			return new PvpMatchRules(MatchType: 4, MapId: 5003, NumberOfTeams: 2, TeamSizeMin: 3, TeamSizeMax: 6, Password: "", TimeLimitSeconds: 900, ScoreLimit: 100,
				FieldH: 0, HasPassword: false, Silent: silent, QueueType: 0);
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void Rules_RoundTripByteExact(bool silent)
		{
			var built = PvpMatchRulesCodec.Build(Rules(silent));

			// 5 u32 + [u16 len][0 chars] + 3 u32 + 2 flag bytes + 1 u32 = 40 B with an empty password - the wire
			// size the client's 0x44-byte struct (whose string is a std::string member) implies.
			Assert.Equal(40, built.Length);
			Assert.Equal(built, PvpMatchRulesCodec.Build(PvpMatchRulesCodec.Parse(built)));
		}

		[Fact]
		public void Rules_PasswordRidesTheWire()
		{
			var rules = Rules(silent: true) with { Password = "secret", HasPassword = true };
			var parsed = PvpMatchRulesCodec.Parse(PvpMatchRulesCodec.Build(rules));

			Assert.Equal("secret", parsed.Password);
			Assert.True(parsed.HasPassword);
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void Match_RoundTripByteExact_AndTheLeaderRidesOnlyANonSilentMatch(bool silent)
		{
			var member = new TeamMemberRecord
			{
				CharacterId = 0x1234,
				Name = "Faolan",
				Class = Core.Enums.ConanClass.Necromancer,
				Level = 80,
				MapId = 5003
			};
			var match = new PvpMatch([member], [], Rules(silent), MatchId: 7) { LeaderId = 0x1234 };

			var built = PvpMatchCodec.Build(match);
			var parsed = PvpMatchCodec.Parse(built);

			Assert.Equal(built, PvpMatchCodec.Build(parsed));
			Assert.Equal(7u, parsed.MatchId);
			Assert.Single(parsed.Roster);
			Assert.Equal("Faolan", parsed.Roster[0].Name);

			// The leader ID32 is the copier's fifo chooser (YouCreatedMatch vs YouJoinedMatch) and rides only a
			// non-silent match - the same byte gates the announcement and the tail, in the client's reader and
			// copier alike.
			Assert.Equal(silent ? 0u : 0x1234u, parsed.LeaderId);

			var silentLength = PvpMatchCodec.Build(match with { Rules = Rules(silent: true) }).Length;
			var announcedLength = PvpMatchCodec.Build(match with { Rules = Rules(silent: false) }).Length;
			Assert.Equal(8, announcedLength - silentLength);
		}

		[Fact]
		public void Match_EmptyRosterAndIdList_FrameAsEmptyLists()
		{
			var built = PvpMatchCodec.Build(new PvpMatch([], [], Rules(silent: true), MatchId: 1));

			// Two empty ListFrames (0x3F1 each) then the rules and the id - no hidden constants.
			Assert.Equal(0x3F1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(0, 4)));
			Assert.Equal(0x3F1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(4, 4)));
			Assert.Equal(4 + 4 + 40 + 4, built.Length);
		}
	}
}
