using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Local travel (#506/#514): a wagoneer, ship captain or caravan master who moves the player somewhere for a fee.
	// The zone-line doors the `world-teleports` module handles are a different thing - a fixed captured UseObject on
	// one door - whereas this is a per-NPC LIST the player chooses from inside an ordinary Knubot conversation.
	//
	// The flow, as the annotated capture actually shows it (see TravelDestination for the full frame-by-frame):
	//
	//   1. the greeting offers "(Pay to travel)";
	//   2. picking it opens a Knubot trade, and when the client closes the trade the server pushes the new wallet
	//      balance and THEN the destination list. So the FARE is charged at the screen that PRESENTS the list, one
	//      step before any destination is chosen - not at the destination pick, and not at a confirmation;
	//   3. picking a destination closes the window and teleports, immediately. There is NO confirmation box: the
	//      scry box in the same capture belongs to the zone-line door the player walked into half a minute later.
	//
	// Both steps are driven off the dialogue GRAPH rather than off any server-side state: a TravelDestination row IS the
	// edge its pick takes, so the screen that charges the fare is that edge's own From screen (#642 - it used to be
	// derived by chopping the last index off an answer path). ConversationService calls in as it takes each branch.
	public sealed class TravelService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly ITravelOutbound _output;

		public TravelService(IDatabaseRepository database, ITravelOutbound output, ILogger logger)
		{
			_database = database;
			_output = output;
			_logger = logger;
		}

		/// <summary>
		///     The destinations an NPC offers, by DISPLAY NAME - the `.travel` GM bootstrap's entry, because a GM types
		///     a name. Nothing on the retail path resolves a traveller this way since #646; the dialogue reaches a row
		///     through the branch that is its key.
		/// </summary>
		public Task<TravelDestination[]> DestinationsOf(string npcName)
		{
			return _database.GetTravelDestinationsByNpcName(npcName);
		}

		/// <summary>
		///     Step 2: the player reached the screen that PRESENTS an NPC's destination list, so the fare comes off
		///     here - that is where retail's DispatchUpdateCash sits. Returns false when the trip is refused (a
		///     criminal, or an empty wallet), which is the caller's cue to keep the player off the list.
		///     A screen that presents no list at all is not a fare gate and returns true untouched.
		/// </summary>
		public async Task<bool> TryChargeFare(INetworkClient client, uint listNodeId)
		{
			var character = client.Session.Character;
			if (character == null)
				return true;

			// The fare gate is the screen a destination's branch LEAVES: retail's own tree puts the list on one screen
			// and each destination one pick deeper, and the edge states which screen that is. That screen is half of
			// this table's primary key, so it is the LOOKUP (#646) - the NPC no longer has to be named to find it, and
			// its name comes back off the row for the text and the log lines.
			var offered = await _database.GetTravelDestinationsFrom(listNodeId);
			if (offered.Length == 0)
				return true;

			var npcName = offered[0].NpcName;

			// Every destination of one NPC carries the same fare (retail charges before the list is shown, so there is
			// only one price to charge); Max rather than First so a partially-harvested NPC cannot be ridden for free.
			// ⚠️ A NULL cost is "the capture never showed the fare", not "free" - charging 0 there would invent a price
			// as surely as charging a guessed one would, so nothing is taken and the gap is logged.
			var fare = offered.Max(d => d.Cost ?? 0);
			if (offered.All(d => d.Cost == null))
				_logger.LogInformation("Travel: \"{Npc}\" has no captured fare, so this ride is free - the capture never showed the wallet move.", npcName);

			// The criminal gate is the client's own rule, quoted in CriminalStatus: "Wagoneers & ship captains will not
			// transport you...". Checked BEFORE the fee, so a refused criminal is never charged.
			if (character.IsCriminal && !CriminalStatus.TransportsCriminals(npcName))
			{
				await _output.SendTravelRefusal(client, $"{npcName} refuses to transport a criminal.");
				_logger.LogInformation("Travel: \"{Npc}\" refused {Character} - criminal until {Until}.", npcName, character.Name, character.CriminalUntil);
				return false;
			}

			if (character.Money < fare)
			{
				await _output.SendTravelRefusal(client, "You cannot afford that journey.");
				_logger.LogInformation("Travel: {Character} can't afford \"{Npc}\" ({Fare} tin, has {Balance}).", character.Name, npcName, fare, character.Money);
				return false;
			}

			if (fare > 0)
			{
				character.Money -= fare;
				await _database.SetCharacterMoney(character.Id, character.Money);
				await _output.SendTravelFeeCharged(client, character.Money);
				_logger.LogInformation("Travel: {Character} paid \"{Npc}\" {Fare} tin for a ride ({Count} destination(s) offered).", character.Name, npcName, fare, offered.Length);
			}

			return true;
		}

		/// <summary>
		///     Step 3: the player picked a destination. Returns false when this dialogue branch sells no trip, which is
		///     the normal case for every other conversation screen in the game and must not be treated as an error.
		/// </summary>
		public async Task<bool> TryTravel(INetworkClient client, uint fromNodeId, uint optionId)
		{
			var character = client.Session.Character;
			if (character == null)
				return false;

			// `(FromNodeId, OptionId)` IS the table's primary key, so the pick is a single-row lookup rather than a
			// fetch-by-name filtered down to one (#646).
			var destination = await _database.GetTravelDestination(fromNodeId, optionId);
			if (destination == null)
				return false;

			await Travel(client, destination.NpcName, destination);
			return true;
		}

		/// <summary>Runs a trip the caller has already picked (the GM bootstrap takes this entry).</summary>
		public async Task Travel(INetworkClient client, string npcName, TravelDestination destination)
		{
			ArgumentNullException.ThrowIfNull(destination);

			var character = client.Session.Character;
			var arrival = destination.ArrivalX is { } x && destination.ArrivalY is { } y && destination.ArrivalZ is { } z
				? new Vector3(x, y, z)
				: (Vector3?)null;

			// Same map = relocate in place (retail's `Local=1`); different map = the full changemap handoff. A same-map
			// trip with no arrival point would move the player nowhere at all, so it is refused rather than silently
			// doing nothing.
			if (destination.DestinationMapId == character.MapId)
			{
				if (arrival == null)
				{
					_logger.LogWarning("Travel: \"{Npc}\" -> map {Map} is a SAME-MAP destination with no arrival point; nothing to do.", npcName, destination.DestinationMapId);
					return;
				}

				await _output.RelocateOnSameMap(client, arrival.Value);
				_logger.LogInformation("Travel: {Character} travelled with \"{Npc}\" to {Arrival} on map {Map} (in place, no loading screen).", character.Name, npcName, arrival,
					destination.DestinationMapId);
				return;
			}

			await _output.TravelToMap(character.Id, destination.DestinationMapId, arrival);
			_logger.LogInformation("Travel: {Character} travelled with \"{Npc}\" to map {Map}.", character.Name, npcName, destination.DestinationMapId);
		}

	}
}
