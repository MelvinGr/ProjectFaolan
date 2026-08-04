using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// LOCAL TRAVEL destinations (#506/#514), harvested rather than hand-authored: which dialogue branch of which NPC
	// sells which trip, for how much, and where it lands the player. Every field is stated on the wire, which is why
	// this is an extractor and not a seed table:
	//
	//   * the BRANCH - the player's KnubotAnswerIIR names an option of the screen that was on their display, so the
	//     harvested row joins onto the imported graph by that screen's CONTENT and that option's own LDB id (#642).
	//     Content rather than a node id because the ids are assigned by the fold, which has not run yet;
	//   * the DESTINATION and ARRIVAL - the GameActionListIIR the server sends the instant the destination is picked
	//     carries "xpos=1136;ypos=133;zpos=223;PlayFieldID=1020;Local=1" (TravelActionCodec);
	//   * the FARE - the wallet DELTA across the conversation. Retail charges through a Knubot trade before the
	//     destination list is even shown, so the DispatchUpdateCash inside the conversation is the price of the ride.
	//
	// ⚠️ Only trips the capture actually TOOK are harvested. A wagoneer offers nine destinations and the capture rides
	// one; the other eight have a name in the LDB and coordinates nowhere, so they stay unharvested rather than being
	// guessed at.
	public static class TravelDestinationExtractor
	{
		// One harvested trip. `FromContentKey` names the screen that PRESENTED the destination list - the same
		// `category:id` rendering ConversationGraph keys its nodes by - and `OptionId` the destination the player
		// clicked, by its own LDB string id.
		public sealed record Entry(string NpcName, string FromContentKey, uint OptionId, uint DestinationMapId, uint? Cost, float ArrivalX, float ArrivalY, float ArrivalZ);

		public static List<Entry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			ArgumentNullException.ThrowIfNull(packets);

			var namesByHandle = ConversationExtractor.BuildSpawnNameMap(packets);
			var wallet = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchUpdateCash);
			var found = new List<Entry>();

			// The live conversation: which NPC, which screen the player is looking at, which option they last clicked,
			// and what the wallet did while it was open.
			uint npc = 0;
			var screen = new List<byte[]>(); // the packets of the screen currently on the player's display
			var fromContentKey = "";
			uint optionId = 0;
			var active = false;
			ulong? balanceNow = null;
			// NULL until a wallet DROP is actually seen inside the conversation. A 0 would claim a free ride; the
			// difference matters, because the first wallet update of a capture has no predecessor to subtract from
			// (Wagoner Glendon's trip is exactly that) and would otherwise be reported as costing nothing.
			uint? spentInConversation = null;

			foreach (var p in packets.OrderBy(p => p.Ticks))
			{
				// The wallet is an ABSOLUTE balance, so a fare is only visible as the drop between two of them. Tracked
				// across the whole capture (not just inside a conversation) so the balance entering one is known.
				if (p.IsDownload && p.WireOpcode == wallet)
				{
					if (TryReadBalance(p) is not { } balance)
						continue;

					if (active && balanceNow is { } previous && balance < previous)
						spentInConversation = (spentInConversation ?? 0) + (uint)(previous - balance);

					balanceNow = balance;
					continue;
				}

				if (!p.IsIirCarrier)
					continue;

				// A greeting (server opens the window) restarts the walk; the player's picks deepen it. Same two
				// signals ConversationExtractor keys its own tree on, so the paths agree by construction.
				if (p.IsDownload && p.TryParse(KnubotCodec.Parse, out var opened) && opened is KnubotOpenChatWindowIIR open)
				{
					npc = open.NpcHandle;
					screen = [p.Bytes];
					fromContentKey = "";
					optionId = 0;
					active = true;
					spentInConversation = null;
					continue;
				}

				// The player clicked: the screen we have been accumulating IS the one that offered the option, so name
				// both before starting the next screen. An index the screen does not offer means we missed its answer
				// list, and a trip we cannot name the branch of is not one we can wire up.
				if (!p.IsDownload && p.TryParse(KnubotCodec.Parse, out var picked) && picked is KnubotAnswerIIR pick)
				{
					if (!active)
						continue;

					var offered = ConversationGraph.OptionIdsOf(ConversationGraph.Columnize(screen));
					fromContentKey = pick.Index < offered.Count ? ConversationGraph.ContentKeyOf(ConversationGraph.Columnize(screen)) : "";
					optionId = pick.Index < offered.Count ? offered[(int)pick.Index] : 0;
					screen = [];
					continue;
				}

				if (p.IsDownload && p.IsIirCarrier && p.TryParse(KnubotCodec.Parse, out _))
				{
					screen.Add(p.Bytes);
					continue;
				}

				// The trip itself. The action rides a GameActionListIIR addressed to the PLAYER, which is what separates
				// it from the hundreds of per-NPC action lists the world stream carries.
				if (p is { IsDownload: true, SubOpcode: GameActionListCodec.Opcode } && active && npc != 0)
				{
					if (!p.TryParse(GameActionListCodec.Parse, out var list) || !list.IsTravelAction)
						continue;

					TravelAction trip;
					try
					{
						trip = TravelActionCodec.Parse(list.ActionTail);
					}
					catch (CodecParseException)
					{
						// A 0xD00D action whose payload is not a travel spec is not a trip - do not guess at one.
						continue;
					}

					if (namesByHandle.GetValueOrDefault(npc) is not { Length: > 0 } name || fromContentKey.Length == 0)
						continue;

					found.Add(new Entry(name, fromContentKey, optionId, trip.DestinationMapId, spentInConversation, trip.X, trip.Y, trip.Z));
				}
			}

			return Merge(found);
		}

		// Dedupes across captures by the row's KEY - the same NPC branch ridden twice is one destination. The richer
		// row wins on cost: a capture that shows the wallet move states the fare, one that missed the update reads 0.
		public static List<Entry> Merge(IEnumerable<Entry> entries)
		{
			ArgumentNullException.ThrowIfNull(entries);
			return entries
				.GroupBy(e => (e.NpcName, e.FromContentKey, e.OptionId))
				.Select(g => g.OrderByDescending(e => e.Cost).First())
				.OrderBy(e => e.NpcName, StringComparer.Ordinal)
				.ThenBy(e => e.FromContentKey, StringComparer.Ordinal)
				.ThenBy(e => e.OptionId)
				.ToList();
		}

		// DispatchUpdateCash is a 12-byte body whose first u64 is the absolute balance. Anything else under that method
		// index is other traffic on the same interface (see CaptureCharacterStateImporter) and must not be read as one.
		private static ulong? TryReadBalance(CapturedPacket p)
		{
			Rpc packet;
			try
			{
				packet = Rpc.Parse(p.Bytes);
			}
			catch
			{
				return null;
			}

			if (packet.Receiver.Type != Endpoints.GameCharInterfaceType || packet.Method != (uint)GameCharInterfaceMethods.DispatchUpdateCash || packet.Body.Length != 12)
				return null;

			return packet.BodyReader().ReadUInt64();
		}
	}
}
