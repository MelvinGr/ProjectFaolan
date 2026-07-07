using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        // Inbound Tradepost (auction house) + mail dispatch, routed here by receiver class (TradepostAgentType) from
        // ReceivedPacket. Mutating ops are folded onto the listener (TradepostOps.cs); ItemSearch parses + replies via
        // TradepostResponses. Grammar in docs/tradepost-protocol.md.
        private async Task HandleTradepostPacket(INetworkClient client, ConanPacket packet)
        {
            var method = (TradepostAgentOpcodes)packet.Opcode;
            switch (method)
            {
                case TradepostAgentOpcodes.UpdateItemTimeouts:
                {
                    // Sent (empty body) AFTER the client received DispatchOpenTradepost (auctioneer / .ah) - the
                    // observed "window opened" signal; nothing to send back, just log.
                    Logger.LogInformation("Tradepost: {Char} opened the auction house.", TradepostCharName(client));
                    break;
                }

                case TradepostAgentOpcodes.ItemSearch:
                {
                    var query = TradepostResponses.ParseSearch(packet.Body);
                    var listings = await Database.GetTradepostListings(query.ItemId);
                    Logger.LogInformation("Tradepost: {Char} searched lang={Lang} itemId=0x{Item:X} page={Page} -> {Count} listing(s).", TradepostCharName(client), query.Lang, query.ItemId, query.Page, listings.Count);

                    // Always send the real result: an empty match list yields an empty grid (head = (0+1)*0x3F1, no rows).
                    await TradepostResponses.SendSearchResults(client, packet, listings);
                    break;
                }

                case TradepostAgentOpcodes.BuyItem:
                {
                    // BUY -> debit the buyer + deliver the item via a system mail.
                    await HandleTradepostBuy(client, packet);
                    break;
                }

                case TradepostAgentOpcodes.TradepostSellItem:
                {
                    // SELL -> list a bank item for sale (creates a listing + escrows the item out of the bank).
                    await HandleTradepostSell(client, packet);
                    break;
                }

                case TradepostAgentOpcodes.TradepostChangeItemPrice:
                {
                    await HandleTradepostChangePrice(client, packet);
                    break;
                }

                case TradepostAgentOpcodes.TradepostCancelSellItem:
                {
                    await HandleTradepostCancelSell(client, packet);
                    break;
                }

                case TradepostAgentOpcodes.SendMail:
                {
                    await HandleTradepostSendMail(client, packet);
                    break;
                }

                case TradepostAgentOpcodes.CancelMail:
                {
                    await HandleTradepostCancelMail(client, packet);
                    break;
                }

                case TradepostAgentOpcodes.Close:
                {
                    Logger.LogInformation("Tradepost: {Char} closed the auction house.", TradepostCharName(client));
                    break;
                }

                // Mailbox retrieval ops: poll / preview / take the bought item / mark-read / delete.
                case TradepostAgentOpcodes.GetAllMails:
                case TradepostAgentOpcodes.GetMailByID:
                case TradepostAgentOpcodes.GetItemData:
                case TradepostAgentOpcodes.GetMailItem:
                case TradepostAgentOpcodes.MarkMailRead:
                case TradepostAgentOpcodes.DeleteMail:
                {
                    await HandleTradepostMailOp(client, packet, method);
                    break;
                }

                default:
                {
                    Logger.LogInformation("Tradepost: {Char} unknown method 0x{Method:X4} (bodylen={Len}).", TradepostCharName(client), packet.Opcode, packet.Body?.Length ?? 0);
                    break;
                }
            }
        }

        private static string TradepostCharName(INetworkClient client)
        {
            return client.Session?.Character?.Name ?? "?";
        }
    }
}
