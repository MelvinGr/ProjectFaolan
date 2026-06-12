using System;
using System.Threading.Tasks;
using Faolan.Core.Data;
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
		public static async Task Handle(INetworkClient client, ConanPacket packet, ILogger logger)
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
					logger.LogInformation("Tradepost: {Char} searched {Query}.", CharName(client), DescribeSearch(packet.Body));
					await TradepostResponses.SendSearchResults(client, packet);
					break;

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
		private static string DescribeSearch(FaolanStream body)
		{
			if (body == null) return "(empty)";
			try
			{
				var lang = System.Text.Encoding.ASCII.GetString(body.ReadArrayUInt16Length());
				body.ReadUInt32();             // cat (const 0x7E2 in captures)
				var page = body.ReadUInt32();
				body.ReadUInt32();             // cat2
				var itemId = body.ReadUInt32();
				return $"lang={lang} itemId=0x{itemId:X} page={page}";
			}
			catch (Exception)
			{
				return "(unparsed)";
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
