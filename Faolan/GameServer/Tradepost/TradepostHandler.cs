using System;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Tradepost
{
	// Entry point for inbound Tradepost (auction house) + mail RPC. First increment (Step 2b): decode and LOG each
	// method against the live session, so an in-client AH interaction confirms our captured grammar
	// (docs/tradepost-protocol.md) on real traffic. Wire responses (DispatchOpenTradepost / search results / mail)
	// are the next increment.
	public static class TradepostHandler
	{
		public static async Task Handle(INetworkClient client, ConanPacket packet, ILogger logger, IDatabaseRepository database)
		{
			var method = (TradepostProtocol.AgentMethod)packet.Opcode;
			switch (method)
			{
				case TradepostProtocol.AgentMethod.UpdateItemTimeouts:
					// The client sends UpdateItemTimeouts (empty body) AFTER it received DispatchOpenTradepost
					// (triggered by the auctioneer interaction / the .ah GM command) - the observed "window
					// opened" signal; nothing to send back, just log.
					logger.LogInformation("Tradepost: {Char} opened the auction house.", CharName(client));
					break;

				case TradepostProtocol.AgentMethod.ItemSearch:
				{
					var query = ParseSearch(packet.Body);
					var listings = await database.GetTradepostListings(query.ItemId);
					logger.LogInformation("Tradepost: {Char} searched lang={Lang} itemId=0x{Item:X} page={Page} -> {Count} listing(s).",
						CharName(client), query.Lang, query.ItemId, query.Page, listings.Count);

					if (listings.Count > 0)
						await TradepostResponses.SendSearchResults(client, packet, query.Category, listings);
					else
						// No imported/seeded listing for this item yet - replay the captured row so the grid still
						// renders (PoC). Drops out once listings exist for the searched item.
						await TradepostResponses.SendSearchResultsReplay(client, packet);
					break;
				}

				case TradepostProtocol.AgentMethod.BuyItem:
				{
					var (saleId, resultId) = ReadBuy(packet.Body);
					logger.LogInformation("Tradepost: {Char} bought listing saleId=0x{Sale:X16} (resultId=0x{Result:X16}).",
						CharName(client), saleId, resultId);
					break;
				}

				case TradepostProtocol.AgentMethod.Close:
					logger.LogInformation("Tradepost: {Char} closed the auction house.", CharName(client));
					break;

				case TradepostProtocol.AgentMethod.GetMailByID:
				case TradepostProtocol.AgentMethod.GetMailItems:
				case TradepostProtocol.AgentMethod.GetItemData:
				case TradepostProtocol.AgentMethod.GetMailItem:
				case TradepostProtocol.AgentMethod.MarkMailRead:
				case TradepostProtocol.AgentMethod.GetAllMails:
					logger.LogInformation("Tradepost/mail: {Char} {Method} (bodylen={Len}).",
						CharName(client), method, packet.Body?.Length ?? 0);
					break;

				default:
					logger.LogInformation("Tradepost: {Char} unknown method 0x{Method:X4} (bodylen={Len}).",
						CharName(client), packet.Opcode, packet.Body?.Length ?? 0);
					break;
			}
		}

		// TradepostItemSearch query: [u16 len][lang ascii][cat:u32][page:u32][cat2:u32][itemId:u32][filter+price...].
		// Category is echoed back as the search-results `head` (const 0x7E2 in the captures).
		private readonly record struct SearchQuery(string Lang, uint Category, uint Page, uint ItemId);

		private static SearchQuery ParseSearch(FaolanStream body)
		{
			if (body == null) return new SearchQuery("", 0x7e2, 0, 0);
			try
			{
				var lang = System.Text.Encoding.ASCII.GetString(body.ReadArrayUInt16Length());
				var cat = body.ReadUInt32();   // category root (const 0x7E2 in captures)
				var page = body.ReadUInt32();
				body.ReadUInt32();             // cat2 (mirror of cat)
				var itemId = body.ReadUInt32();
				return new SearchQuery(lang, cat, page, itemId);
			}
			catch (Exception)
			{
				return new SearchQuery("", 0x7e2, 0, 0);
			}
		}

		private static (ulong saleId, ulong resultId) ReadBuy(FaolanStream body)
		{
			if (body == null || body.Length < 16) return (0, 0);
			return (body.ReadUInt64(), body.ReadUInt64());
		}

		private static string CharName(INetworkClient client) =>
			client.Session?.Character?.Name ?? "?";
	}
}
