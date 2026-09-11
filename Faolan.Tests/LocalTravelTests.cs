using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// Local travel: the 0xD00D action that states a trip, and the wiring that joins a harvested trip onto
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
			// playfield 1020. `ypos` is the height.
			Assert.Equal(1020u, trip.DestinationMapId);
			Assert.Equal(1136f, trip.X);
			Assert.Equal(133f, trip.Y);
			Assert.Equal(223f, trip.Z);
			Assert.True(trip.IsLocal);
		}

		// The flag is the only thing that separates a wagoner's relocate from an instance entry, so it is carried as a
		// (key, value) pair rather than as a bool: a `Local`-only model reads HeroicEnabled=0 as "not local" and
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

		// NecroL80_..._chat-annotated-4 frame 9793: the casterless *Racer* buff retail applies between the trip's action
		// and the teleport pair. Its twin at frame 11539 and Hakotep's at frame 812 differ only in the expiry stamp and
		// the level, which is exactly what this build takes as arguments.
		private const string CordeliaRacerBuff =
			"6b87e3160000c3500802e5d400004cbd29000000000000000000000000000000000000000032000000000001" +
			"00000000004cbd29000000000000000000000000000000000000005000000000151bb10c";

		// The element's `field` (0x1A18 here) is retail's `GameActionData::s_nInstance` - the sending process's
		// unique-instance counter - not a per-trip value derived from something the server holds. So the only thing a
		// build needs from the capture is that counter reading, and everything else comes from the destination.
		// Constructing both flag shapes from (map, x, y, z) and landing on the captured bytes is what says the decode
		// is right.
		[Theory]
		[InlineData(CordeliaTrip, 0x0802E5D4u, 0x1A18u, true, 1020u, 1136f, 133f, 223f)]
		[InlineData(HeroicTrip, 0x0802E5D4u, 0x0B32u, false, 2070u, 1520f, 172f, 1476f)]
		public void ACapturedTrip_BuildsFromItsDestinationAndTheInstanceCounter(string captured, uint frameHandle, uint instanceId, bool local, uint map, float x, float y, float z)
		{
			var trip = local
				? TravelActions.Local(map, x, y, z)
				: TravelActions.CrossPlayfield(map, x, y, z);

			var built = GameActionListCodec.Build(new GameActionListIIR(frameHandle, TravelActionCodec.TypeId, instanceId, TravelActionCodec.Build(trip)));

			Assert.Equal(Convert.FromHexString(captured), built);
		}

		// The counter is an allocation, so what a test can pin is the invariant the client asserts on: it never hands
		// out 0 ("not assigned" - the default ctor leaves the field there), and it only ever goes up.
		[Fact]
		public void TheGameActionInstanceCounter_NeverHandsOutZeroAndOnlyRises()
		{
			var ids = Enumerable.Range(0, 64).Select(_ => GameActionInstanceId.Next()).ToArray();

			Assert.DoesNotContain(0u, ids);
			Assert.Equal(ids.OrderBy(i => i).ToArray(), ids);
			Assert.Equal(ids.Length, ids.Distinct().Count());
		}

		// The Racer buff, byte-exact from the catalog. It is the casterless apply form - a zero entity-class word where
		// the caster reference would be, `Bool2` 0 and Descriptor[3] 0 - which is why it does not go through the
		// generic `Apply`. All 11 casterless applies in the corpus carry those two slots that way.
		[Fact]
		public void TheRacerBuff_BuildsByteExactFromTheCatalog()
		{
			var built = MagicBuffCodec.Build(new MagicBuffIIR(0x0802E5D4, 0x00, MagicBuffIds.BaseOf(TravelCatalog.RacerSpell), MagicBuffIds.FormOf(TravelCatalog.RacerSpell), null,
				MagicBuffDataCodec.Build(TravelCatalog.RacerPayload(80, 0x151BB10C))));

			Assert.Equal(Convert.FromHexString(CordeliaRacerBuff), built);
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

		// The wiring invariant: a shipped trip is only reachable if the dialogue branch it names exists. The
		// row is that branch now - keyed by the screen it leaves and the option's own LDB id - so the check is that the
		// graph has an edge with exactly that key. A row with none is dead wiring: the trip can never be picked, and
		// that is precisely the failure a hand-authored path would produce silently.
		[Fact]
		public void EveryShippedTrip_NamesADialogueBranchThatExists()
		{
			var edges = ShippedRows("SELECT FromNodeId, OptionId FROM ConversationEdges", r => ((uint)r.GetInt64(0), (uint)r.GetInt64(1))).ToHashSet();
			var trips = ShippedRows("SELECT FromNodeId, OptionId FROM TravelDestinations", r => ((uint)r.GetInt64(0), (uint)r.GetInt64(1))).ToList();

			Assert.NotEmpty(trips);
			Assert.Equal([], trips.Where(t => !edges.Contains(t)).ToArray());
		}

		// ...and the screen that presents the list has to exist too, or the fare is never charged. That screen is the
		// branch's own From node, i.e. a foreign key rather than a string-surgery derivation - worth pinning, because
		// a derived form silently accepts a path with no parent row.
		[Fact]
		public void EveryShippedTrip_HasTheListScreenThatChargesForIt()
		{
			var screens = ShippedRows("SELECT Id FROM ConversationNodes", r => (uint)r.GetInt64(0)).ToHashSet();
			var trips = ShippedRows("SELECT FromNodeId FROM TravelDestinations", r => (uint)r.GetInt64(0)).ToList();

			Assert.Equal([], trips.Where(t => !screens.Contains(t)).ToArray());
		}

		// The second travel mechanism - the three travellers who carry their destination inline in their dialogue
		// text as a `{teleportplayer}` directive, harvested by `rdb-dialogue-teleports` into this same table.
		//
		// Pinned by value, not by count, because the values are the decode: the grammar is
		// `{teleportplayer <x> <z> [y <y>] <map>}`, and the load-bearing reading is that the two leading numbers are X
		// and Z with the height carried optionally behind a literal `y`. The Smuggler's row is the one the corpus
		// checks - `{teleportplayer 210 205 y 11 4579}` against a capture that lands the player at (210.0, 10.5, 205.0)
		// on playfield 4579 - so a row of (210, 11, 205) is that reading and (210, 205, 11) would be the wrong one.
		//
		// [V2: NecroL80_..._disconnect-after-wayfarer t=105.8 (the Smuggler's line) -> 111.7 (IntendedTeleport 4579) ->
		// 112.3 (arrival), and t=516.2 -> 519.4 for Polus]
		[Theory]
		[InlineData("Tortage Smuggler", 4579u, 210f, 11f, 205f, 6f)]
		[InlineData("Wagoner Polus", 1000u, 349f, null, 564f, 3f)] // no height in the directive - ground-snapped at run time
		[InlineData("Sigurd", 4020u, 55.7f, 158.1f, 113.9f, 0f)] // no {delay} - retail teleports as the line lands
		public void EachDirectiveTraveller_CarriesTheTripItsTextStates(string npc, uint map, float x, float? y, float z, float delay)
		{
			var rows = ShippedRows(
					"SELECT NpcName, DestinationMapId, ArrivalX, ArrivalY, ArrivalZ, DelaySeconds, Cost, FromDialogueDirective FROM TravelDestinations WHERE FromDialogueDirective = 1",
					r => (Npc: r.GetString(0), Map: (uint)r.GetInt64(1), X: (float)r.GetDouble(2), Y: r.IsDBNull(3)
						? (float?)null
						: (float)r.GetDouble(3), Z: (float)r.GetDouble(4), Delay: (float)r.GetDouble(5), HasCost: !r.IsDBNull(6)))
				.ToList();

			var row = Assert.Single(rows, t => t.Npc == npc);
			Assert.Equal(map, row.Map);
			Assert.Equal(x, row.X, 3);
			Assert.Equal(y.HasValue, row.Y.HasValue);
			if (y.HasValue)
				Assert.Equal(y.Value, row.Y!.Value, 3);

			Assert.Equal(z, row.Z, 3);
			Assert.Equal(delay, row.Delay, 3);

			// A directive traveller charges nothing - there is no fare and no trade window anywhere in the flow - so a
			// null Cost here means free, unlike a wagoner's null, which means "the capture never showed the wallet move".
			Assert.False(row.HasCost);
		}

		// The captured wagoner trips carry no delay: retail teleports on the pick. Pinned so the column stays the
		// directive travellers' own property rather than drifting onto the paid ones.
		[Fact]
		public void ACapturedWagonerTrip_HasNoDelay()
		{
			var delays = ShippedRows("SELECT DelaySeconds FROM TravelDestinations WHERE FromDialogueDirective = 0", r => (float)r.GetDouble(0)).ToList();

			Assert.NotEmpty(delays);
			Assert.All(delays, d => Assert.Equal(0f, d));
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
