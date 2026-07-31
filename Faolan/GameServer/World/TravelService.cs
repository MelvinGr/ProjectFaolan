using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Local travel (#506): a wagoneer, ship captain or caravan master who moves the player somewhere for a fee. The
	// zone-line doors the `world-teleports` module handles are a different thing - a fixed captured UseObject on one
	// door - whereas this is a per-NPC LIST the player chooses from, which is why it is data plus a flow.
	//
	// The flow the annotated capture shows, and the one implemented here:
	//
	//   1. an ordinary NPC conversation where the player picks a destination;
	//   2. the FEE is charged at the pick - the wallet drops by a round 50,000 at 20:16:41, well before anything else;
	//   3. a scry confirmation box naming the destination map (ScryMessageBoxCodec);
	//   4. on confirm, the trip happens.
	//
	// ⚠️ Step 4 is two different wire paths and the service picks between them, because a destination is not always
	// another playfield: a SAME-MAP destination relocates the player in place (no loading screen), a cross-map one goes
	// through the full changemap handoff. See TravelDestination.
	public sealed class TravelService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly ITravelOutbound _output;

		// Trips awaiting the player's confirmation, keyed by the scry thread-context id the box was raised with. The
		// client echoes that id back verbatim in its answer, which is exactly what makes it a usable correlation handle
		// (ScryMessageBoxCodec) - so no server-side script thread is needed to resume the action.
		private readonly ConcurrentDictionary<uint, PendingTrip> _pending = new();

		// The scry context id counter. Any value works (the client only echoes it); it starts high enough not to
		// collide with the small ids the create-flow's own scry steps use.
		private uint _nextContext = 0x00300000;

		public TravelService(IDatabaseRepository database, ITravelOutbound output, ILogger logger)
		{
			_database = database;
			_output = output;
			_logger = logger;
		}

		/// <summary>The destinations an NPC offers, empty when it is not a travel NPC at all.</summary>
		public Task<TravelDestination[]> DestinationsOf(string npcName)
		{
			return _database.GetTravelDestinations(npcName);
		}

		/// <summary>
		///     Step 2+3: charge the fee and raise the confirmation box. Returns false (with the player told why) when
		///     the trip cannot start - an unknown destination, an empty wallet, or a criminal.
		/// </summary>
		public async Task<bool> BeginTravel(INetworkClient client, string npcName, uint destinationMapId)
		{
			var character = client.Session.Character;
			if (character == null)
				return false;

			// The criminal gate is the client's own rule, quoted in CriminalStatus: "Wagoneers & ship captains will not
			// transport you...". Checked BEFORE the fee, so a refused criminal is never charged.
			if (character.IsCriminal && !CriminalStatus.TransportsCriminals(npcName))
			{
				await _output.SendTravelRefusal(client, $"{npcName} refuses to transport a criminal.");
				_logger.LogInformation("Travel: \"{Npc}\" refused {Character} - criminal until {Until}.", npcName, character.Name, character.CriminalUntil);
				return false;
			}

			var destination = (await DestinationsOf(npcName)).FirstOrDefault(d => d.DestinationMapId == destinationMapId);
			if (destination == null)
			{
				_logger.LogWarning("Travel: \"{Npc}\" offers no destination {Map}.", npcName, destinationMapId);
				return false;
			}

			if (character.Money < destination.Cost)
			{
				await _output.SendTravelRefusal(client, "You cannot afford that journey.");
				_logger.LogInformation("Travel: {Character} can't afford {Npc} -> map {Map} ({Cost} tin, has {Balance}).", character.Name, npcName, destinationMapId, destination.Cost,
					character.Money);
				return false;
			}

			// Charge at the PICK, matching retail's ordering - the wallet moves ~40 s before the confirmation box in
			// the capture, so the fee is the price of choosing, not of confirming.
			if (destination.Cost > 0)
			{
				character.Money -= destination.Cost;
				await _database.SetCharacterMoney(character.Id, character.Money);
				await _output.SendTravelFeeCharged(client, character.Money);
			}

			var context = Interlocked.Increment(ref _nextContext);
			// The cookie is only ever echoed back, so it needs to be unpredictable-ish rather than meaningful; pairing
			// it with the character makes a stray answer from another session identifiable in the log.
			var cookie = ((ulong)character.Id << 32) | context;
			_pending[context] = new PendingTrip(character.Id, npcName, destination);

			await _output.SendTravelConfirm(client, destination.DestinationMapId, context, cookie);
			_logger.LogInformation("Travel: {Character} booked \"{Npc}\" -> map {Map} for {Cost} tin; awaiting confirmation (ctx 0x{Ctx:X8}).", character.Name, npcName,
				destinationMapId, destination.Cost, context);
			return true;
		}

		/// <summary>
		///     Step 4: the player answered the confirmation box. Answer 1 is the confirm button (every captured answer
		///     is 1); anything else cancels. Returns false when the context names no pending trip, which is the normal
		///     case for the OTHER scry boxes in the game and must not be treated as an error.
		/// </summary>
		public async Task<bool> ConfirmTravel(INetworkClient client, uint answer, uint threadCtxId)
		{
			if (!_pending.TryRemove(threadCtxId, out var trip))
				return false;

			var character = client.Session.Character;
			if (character == null || character.Id != trip.CharacterId)
				return false;

			if (answer != 1)
			{
				// ⚠️ The fee is NOT refunded, deliberately: retail charges at the pick and the corpus has no cancelled
				// trip to show what happens next, so refunding would be inventing a rule. Logged so that if a cancel is
				// ever tested in-client and DOES refund, this line is where the wrong assumption is.
				_logger.LogInformation("Travel: {Character} declined the trip to map {Map} (answer {Answer}); the {Cost} tin fee is not refunded.", character.Name,
					trip.Destination.DestinationMapId, answer, trip.Destination.Cost);
				return true;
			}

			var arrival = trip.Destination.ArrivalX is { } x && trip.Destination.ArrivalY is { } y && trip.Destination.ArrivalZ is { } z
				? new Vector3(x, y, z)
				: (Vector3?)null;

			// Same map = relocate in place; different map = the full changemap handoff. A same-map trip with no arrival
			// point would move the player nowhere at all, so it is refused rather than silently doing nothing.
			if (trip.Destination.DestinationMapId == character.MapId)
			{
				if (arrival == null)
				{
					_logger.LogWarning("Travel: \"{Npc}\" -> map {Map} is a SAME-MAP destination with no arrival point; nothing to do.", trip.NpcName, trip.Destination.DestinationMapId);
					return true;
				}

				await _output.RelocateOnSameMap(client, arrival.Value);
				_logger.LogInformation("Travel: {Character} travelled with \"{Npc}\" to {Arrival} on map {Map} (in place, no loading screen).", character.Name, trip.NpcName, arrival,
					trip.Destination.DestinationMapId);
				return true;
			}

			await _output.TravelToMap(character.Id, trip.Destination.DestinationMapId, arrival);
			_logger.LogInformation("Travel: {Character} travelled with \"{Npc}\" to map {Map}.", character.Name, trip.NpcName, trip.Destination.DestinationMapId);
			return true;
		}

		/// <summary>Drops any pending trip for a client that went away, so the table cannot grow unbounded.</summary>
		public void Forget(uint characterId)
		{
			foreach (var pair in _pending)
			{
				if (pair.Value.CharacterId == characterId)
					_pending.TryRemove(pair.Key, out _);
			}
		}

		private sealed record PendingTrip(uint CharacterId, string NpcName, TravelDestination Destination);
	}
}
