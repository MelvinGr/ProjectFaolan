using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// Local travel (#506/#514): the 0xD00D action that states a trip, and the wiring that joins a harvested trip onto
	// the dialogue branch that sells it.
	//
	// The bodies below are the complete GameActionListIIR carriers retail sent the instant a destination was picked,
	// straight out of the captures. Both flag shapes are here on purpose - `Local=1` (a wagoner's same-playfield
	// relocate) and `HeroicEnabled=0` (a cross-map trip carrying the difficulty flag instead) - because collapsing the
	// two into one bool is exactly what made the fourth captured trip invisible to the harvest.
	public class LocalTravelTests
	{
		// NecroL80_..._chat-annotated-4 frame 9790: Wagoner Cordelia -> "the border of Ymir's Pass", on Field of the Dead.
		private const string CordeliaTrip =
			"f508f4c10000c3500802e5d400000007e20000d00d00001a180000000400000000000000010000000000000001000000000037a487" +
			"003478706f733d313133363b79706f733d3133333b7a706f733d3232333b506c61794669656c6449443d313032303b4c6f63616c3d310000000000";

		// NecroL80_..._login_combat frame 6160: the same action carrying `HeroicEnabled=0` instead of `Local=1`.
		private const string HeroicTrip =
			"f508f4c10000c3500802e5d400000007e20000d00d00000b320000000400000000000000010000000000000001000000000037a487" +
			"003d78706f733d313532303b79706f733d3137323b7a706f733d313437363b506c61794669656c6449443d323037303b4865726f6963456e61626c65643d300000000000";

		[Fact]
		public void TheWagonerTrip_ParsesToItsDestinationAndArrival()
		{
			var trip = TravelActionCodec.Parse(GameActionListCodec.Parse(Convert.FromHexString(CordeliaTrip)).ActionTail);

			// Exactly what the player's re-stream then confirmed: SimpleCharFullUpdate at (1136.0, 132.8, 223.0) on
			// playfield 1020. `ypos` is the HEIGHT.
			Assert.Equal(1020u, trip.DestinationMapId);
			Assert.Equal(1136f, trip.X);
			Assert.Equal(133f, trip.Y);
			Assert.Equal(223f, trip.Z);
			Assert.True(trip.IsLocal);
		}

		// The flag is the only thing that separates a wagoner's relocate from an instance entry, so it is carried as a
		// (key, value) pair rather than as a bool: a `Local`-only model reads HeroicEnabled=0 as "not local" AND
		// re-emits the wrong key.
		[Fact]
		public void TheHeroicFlagTrip_KeepsItsOwnKeyAndIsNotLocal()
		{
			var trip = TravelActionCodec.Parse(GameActionListCodec.Parse(Convert.FromHexString(HeroicTrip)).ActionTail);

			Assert.Equal(2070u, trip.DestinationMapId);
			Assert.Equal(TravelActionCodec.HeroicEnabledFlag, trip.FlagKey);
			Assert.Equal(0u, trip.FlagValue);
			Assert.False(trip.IsLocal);
		}

		[Theory]
		[InlineData(CordeliaTrip)]
		[InlineData(HeroicTrip)]
		public void EveryCapturedTrip_RebuildsByteExact(string captured)
		{
			var body = Convert.FromHexString(captured);
			var list = GameActionListCodec.Parse(body);

			var rebuilt = GameActionListCodec.Build(list with { ActionTail = TravelActionCodec.Build(TravelActionCodec.Parse(list.ActionTail)) });

			Assert.Equal(body, rebuilt);
		}

		// An unrecognised key is a body we have mis-read, not a default to assume - the rule every Parse follows.
		[Fact]
		public void AnUnknownKey_Throws()
		{
			Assert.ThrowsAny<Exception>(() => TravelActionCodec.Parse("xpos=1;ypos=2;zpos=3;PlayFieldID=4;Whatever=5", 0));
		}

		// The fare gate is DERIVED, not stored: retail puts the destination list on one screen and each destination one
		// pick deeper, so the screen that charges is the parent of the destination's answer path.
		[Theory]
		[InlineData("0>3", "0")]
		[InlineData("0>5", "0")]
		[InlineData("1>0", "1")]
		[InlineData("0", "")]
		[InlineData("", "")]
		public void TheFareGate_IsTheDestinationsParentScreen(string answerPath, string expected)
		{
			Assert.Equal(expected, TravelService.ParentPath(answerPath));
		}

		// THE wiring invariant (#514): a shipped trip is only reachable if the dialogue branch it names exists. A row
		// whose (npc, answer path) matches no ConversationNode is dead wiring - the trip can never be picked - and that
		// is precisely the failure a hand-authored path would produce silently.
		[Fact]
		public void EveryShippedTrip_NamesADialogueBranchThatExists()
		{
			var nodes = ShippedRows("SELECT NpcName, AnswerPath FROM ConversationNodes", r => (r.GetString(0), r.GetString(1))).ToHashSet();
			var trips = ShippedRows("SELECT NpcName, AnswerPath FROM TravelDestinations", r => (r.GetString(0), r.GetString(1))).ToList();

			Assert.NotEmpty(trips);
			Assert.Equal([], trips.Where(t => !nodes.Contains(t)).ToArray());
		}

		// ...and the screen that PRESENTS the list has to exist too, or the fare is never charged.
		[Fact]
		public void EveryShippedTrip_HasTheListScreenThatChargesForIt()
		{
			var nodes = ShippedRows("SELECT NpcName, AnswerPath FROM ConversationNodes", r => (r.GetString(0), r.GetString(1))).ToHashSet();
			var trips = ShippedRows("SELECT NpcName, AnswerPath FROM TravelDestinations", r => (r.GetString(0), r.GetString(1))).ToList();

			Assert.Equal([], trips.Where(t => !nodes.Contains((t.Item1, TravelService.ParentPath(t.Item2)))).ToArray());
		}

		private static IEnumerable<T> ShippedRows<T>(string sql, Func<SqliteDataReader, T> read)
		{
			var rows = new List<T>();
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = sql;
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				rows.Add(read(reader));
			return rows;
		}
	}
}
