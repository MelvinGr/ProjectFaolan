using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Economy.Tradepost;
using Faolan.GameServer.FullCharSnapshot;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        // Tradepost (auction house) + mail state-mutating ops, folded off the former TradepostService onto the
        // listener. HandleTradepostPacket (Inbound/HandleTradepost.cs) dispatches to these. They use Database /
        // Logger and the listener's own SendUpdateCash / SendAddItem (its IEconomyOutbound impl) directly.

        // Mail wire/DB constants (MailConstants.EndpointClass / FooterUnread / AttachmentTrailerWord / ExpirySeconds) live in
        // the shared Faolan.Core MailConstants so the server and the MCP economy tools agree on one source.

        // The bank "listed" overlay rides on the SAME item-stat ids as a listing entry (TradepostListingCodec.Stat*:
        // 0=flags, 258=price, 265=timestamp), pushed as 0x201c stat updates on container 105. Only the flag VALUES
        // below are bank-overlay-specific.

        private const uint BankFlagListed = 0x401; // flags value while listed for sale
        private const uint BankFlagMailPending = 0x8001; // flags value while attached to an outgoing mail
        private const uint BankFlagNormal = 0x001; // flags value once unlisted / mail cancelled

        // TradepostBuyItem (0x200E) [saleId:u64][searchResultId:u64]: charge the buyer and deliver the listed item via
        // a system mail. Refuses on unknown listing or insufficient funds (no money moves until the mail is created).
        private async Task HandleTradepostBuy(INetworkClient client, ConanPacket packet)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var saleId = packet.Body.ReadUInt64(); // searchResultId follows but isn't needed to resolve the sale

            var listing = await Database.GetTradepostListingBySaleId(saleId);
            if (listing == null)
            {
                Logger.LogWarning("Tradepost BUY: {Char} bought unknown listing saleId=0x{Sale:X16} (already sold?).", character.Name, saleId);
                return;
            }

            if (character.Money < listing.Price)
            {
                Logger.LogInformation("Tradepost BUY: {Char} can't afford listing 0x{Sale:X16} ({Price} tin, has {Balance}).", character.Name, saleId, listing.Price, character.Money);
                return;
            }

            character.Money -= listing.Price;
            await Database.SetCharacterMoney(character.Id, character.Money);
            await SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

            var mail = await BuildPurchaseMail(character.Name, listing);
            await Database.CreateMail(mail);
            await Database.DeleteTradepostListing(saleId);

            // The listed item stayed in the SELLER's bank (with a "listed" overlay) until now - the sale removes it
            // from their bank. Importer-seeded listings have no real seller character, so this is a no-op for them.
            var seller = await Database.GetCharacterByName(listing.SellerName);
            if (seller != null && seller.Id != character.Id)
                await Database.SetCharacterInventorySlot(seller.Id, (int)FullCharSnapshotBuilder.BankKind, (int)listing.BankSlot, null);

            // Push the mail (base, no attachment): it carries its own MailId, which the client then targets with the
            // GetMail* ops to preview + take the item. Echo the inbound TradepostInterface instance.
            await TradepostResponses.SendMailMessage(client, mail, false, packet.Sender.Instance);

            Logger.LogInformation("Tradepost BUY: {Char} bought listing 0x{Sale:X16} (item 0x{Item:X}) for {Price} tin -> mail 0x{Mail:X16} (wallet {Balance}).",
                character.Name,
                saleId,
                listing.ItemId,
                listing.Price,
                mail.MailId,
                character.Money);
        }

        // CancelMail (0x2009) [mailId:u64]: recall/delete one of the player's mails (capture F403). We delete it; any
        // escrowed attachment is dropped with it (the AH buy-delivery mail is the only attachment source today).
        private async Task HandleTradepostCancelMail(INetworkClient client, ConanPacket packet)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var mailId = packet.Body.ReadUInt64();

            // If the sender (this player) attached a bank item, clear its mail-pending overlay (capture F404: flag -> 1)
            // - the item never left the bank, so cancelling just frees it.
            var mail = await Database.GetMailByWireId(mailId);
            if (mail != null && mail.SenderName == character.Name && (mail.Attachments?.Count ?? 0) > 0)
                await SendBankSlotStat(client, mail.SenderBankSlot, TradepostListingCodec.StatFlags, BankFlagNormal);

            await Database.DeleteMail(mailId);
            Logger.LogInformation("Tradepost mail: {Char} cancelled/deleted mail 0x{Mail:X16}.", character.Name, mailId);
        }

        // TradepostCancelSellItem (0x200C) [nBankItemSlot:u32]: pull the seller's own listing (capture F397 = slot 6).
        // The item never left the bank (it just had the "listed" overlay), so cancelling only deletes the listing -
        // the client clears the overlay itself.
        private async Task HandleTradepostCancelSell(INetworkClient client, ConanPacket packet)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var bankSlot = packet.Body.ReadUInt32();

            var listing = await Database.GetTradepostListingBySellerSlot(character.Name, bankSlot);
            if (listing == null)
            {
                Logger.LogWarning("Tradepost CANCELSELL: {Char} has no listing for bank slot {Slot}.", character.Name, bankSlot);
                return;
            }

            await Database.DeleteTradepostListing(listing.SaleId);
            // Clear the bank-slot overlay (capture F398): flag back to normal, price to 0.
            await SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatFlags, BankFlagNormal);
            await SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatPrice, 0);
            Logger.LogInformation("Tradepost CANCELSELL: {Char} cancelled listing 0x{Sale:X16} (bank slot {Slot}); item stays in the bank.", character.Name, listing.SaleId, bankSlot);
        }

        // TradepostChangeItemPrice (0x200B) [nBankItemSlot:u32][nNewPrice:u64]: reprice the seller's own listing,
        // addressed by the origin bank slot (capture F363 = slot 6 -> 5,000,000 tin). No tradepost reply (re-poll).
        private async Task HandleTradepostChangePrice(INetworkClient client, ConanPacket packet)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var bankSlot = packet.Body.ReadUInt32();
            var newPrice = packet.Body.ReadUInt64();

            var listing = await Database.GetTradepostListingBySellerSlot(character.Name, bankSlot);
            if (listing == null)
            {
                Logger.LogWarning("Tradepost CHANGEPRICE: {Char} has no listing for bank slot {Slot}.", character.Name, bankSlot);
                return;
            }

            await Database.SetTradepostListingPrice(listing.SaleId, newPrice);
            // Update the bank-slot overlay's price stat (capture F364).
            await SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatPrice, (uint)newPrice);
            Logger.LogInformation("Tradepost CHANGEPRICE: {Char} repriced listing 0x{Sale:X16} (bank slot {Slot}) {Old} -> {New} tin.", character.Name, listing.SaleId, bankSlot, listing.Price, newPrice);
        }

        // The mailbox ops (GetAllMails / GetMailByID / GetItemData preview / GetMailItem take / MarkMailRead /
        // DeleteMail). All address the buyer's own mail by the wire MailId; GetMailItem grants the escrowed item.
        private async Task HandleTradepostMailOp(INetworkClient client, ConanPacket packet, TradepostAgentOpcodes method)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var inst = packet.Sender.Instance;

            switch (method)
            {
                case TradepostAgentOpcodes.GetAllMails:
                {
                    var mails = await Database.GetMailsForRecipient(character.Name);
                    foreach (var m in mails)
                        await TradepostResponses.SendMailMessage(client, m, false, inst);
                    await TradepostResponses.SendRetrieveMailResult(client, inst);
                    Logger.LogInformation("Tradepost mail: {Char} polled the mailbox -> {Count} mail(s).", character.Name, mails.Count);
                    break;
                }

                case TradepostAgentOpcodes.GetMailByID:
                {
                    var mail = await Database.GetMailByWireId(packet.Body.ReadUInt64());
                    if (mail != null)
                        await TradepostResponses.SendMailMessage(client, mail, false, inst);
                    break;
                }

                case TradepostAgentOpcodes.GetItemData:
                {
                    // Preview: the mail WITH its attachment (the client shows the item in the mail; nothing is granted).
                    var mail = await Database.GetMailByWireId(packet.Body.ReadUInt64());
                    if (mail != null)
                        await TradepostResponses.SendMailMessage(client, mail, true, inst);
                    break;
                }

                case TradepostAgentOpcodes.GetMailItem:
                {
                    var mailId = packet.Body.ReadUInt64();
                    var itemInstanceId = packet.Body.ReadUInt64();
                    await TakeMailItem(client, mailId, itemInstanceId, inst);
                    break;
                }

                case TradepostAgentOpcodes.MarkMailRead:
                {
                    var mailId = packet.Body.ReadUInt64();
                    await Database.SetMailRead(mailId, true);
                    await TradepostResponses.SendMailResult(client, true, 0, inst);
                    break;
                }

                case TradepostAgentOpcodes.DeleteMail:
                {
                    var mailId = packet.Body.ReadUInt64();
                    await Database.DeleteMail(mailId);
                    Logger.LogInformation("Tradepost mail: {Char} deleted mail 0x{Mail:X16}.", character.Name, mailId);
                    break;
                }
            }
        }

        // TradepostSellItem (0x200A) [nBankItemSlot:u32][nPrice:u64]: list one of the player's BANK items (container
        // 105) for sale. Decoded from the necrol80 sell_item_on_ah capture (F375 = slot 6 @ 1,000,000 tin). Creates a
        // TradepostListing from the bank item's identity, escrows the item out of the bank, and refreshes the bank tab.
        private async Task HandleTradepostSell(INetworkClient client, ConanPacket packet)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var bankSlot = (int)packet.Body.ReadUInt32();
            var price = packet.Body.ReadUInt64();

            var rows = await Database.GetCharacterInventory(character.Id);
            var row = rows.FirstOrDefault(r => r.Container == (int)FullCharSnapshotBuilder.BankKind && r.Slot == bankSlot);
            if (row?.Item == null)
            {
                Logger.LogWarning("Tradepost SELL: {Char} has no bank item at slot {Slot}.", character.Name, bankSlot);
                return;
            }

            // #181: file the listing under the item's AH search CATEGORY (the item-type-search id the client queries
            // by, e.g. 30030) so a normal item-type search surfaces it, recovered from captured listings. Falls back
            // to the template when the category is unknown (no regression vs the old template-filing).
            var searchCategory = await Database.GetItemSearchCategory(row.Item.Id) ?? row.Item.Id;

            var listing = new TradepostListing
            {
                SaleId = await AllocateSaleId(),
                Handle = 0,
                SellerName = character.Name,
                BankSlot = (uint)bankSlot,
                Price = price,
                Quantity = row.Count == 0
                    ? 1u
                    : row.Count,
                ItemId = searchCategory, // #181: the item-type search category (was row.Item.Id, the template)
                InstanceField = 0,
                ItemHandle = 0,
                EntryFlags = TradepostListingCodec.DefaultEntryFlags,
                Timestamp = 0,
                Data1 = row.Item.Data1,
                Data2 = row.Item.Data2,
                Data3 = row.Item.Data3,
                Data4 = row.Item.Data4,
                Template = row.Item.Id,
                ItemLevel = row.Item.Level,
                ItemHash = row.Item.Hash
            };
            await Database.CreateTradepostListing(listing);

            // The listed item STAYS in its bank slot - the client draws a "listed" overlay when the server pushes
            // these three bank-slot stat updates (decoded from the capture, F376/F377): price, timestamp, and the
            // listed flag. It only leaves the seller's bank when someone BUYS it (HandleTradepostBuy clears the cell).
            await SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatPrice, (uint)price);
            await SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatTimestamp, Statics.NowUnixTimeSeconds);
            await SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatFlags, BankFlagListed);

            // Retail pushes a 0x2005 wallet refresh after a listing - but the balance is UNCHANGED: there is NO
            // listing deposit / postage fee. Verified across the sell / send-mail / change-price / cancel captures
            // (necrol80 stays at 1,365,148 tin throughout), #182. Push the current balance (reason 0, as captured) so
            // the HUD stays in sync; no debit.
            await SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

            Logger.LogInformation("Tradepost SELL: {Char} listed \"{Item}\" x{Qty} (bank slot {Slot}, template {Template}) for {Price} tin -> sale 0x{Sale:X16}.",
                character.Name,
                row.Item.Name,
                listing.Quantity,
                bankSlot,
                row.Item.Id,
                price,
                listing.SaleId);
        }

        // SendMail (0x2002) [u16+recipient][u16+body][money:u64][0x7E2:u32][bankItemSlot:u32]: a player-composed mail,
        // optionally with a bank item attached (capture F499 = to "DemoL80", "Hello this is a test", slot 5, no COD).
        // Creates a Mail to the recipient with the sender's bank item escrowed, removes it from the sender's bank, and
        // replies DispatchSendMailResult. Rejects (success=0) an unknown recipient so the client shows "couldn't send".
        private async Task HandleTradepostSendMail(INetworkClient client, ConanPacket packet)
        {
            var character = client.Session.Character;
            if (character == null)
                return;

            var recipientName = Encoding.ASCII.GetString(packet.Body.ReadArrayUInt16Length());
            var bodyText = Encoding.ASCII.GetString(packet.Body.ReadArrayUInt16Length());
            var money = packet.Body.ReadUInt64();
            packet.Body.ReadUInt32(); // 0x7E2 category constant (preserved)
            var bankSlot = (int)packet.Body.ReadUInt32();

            var recipient = await Database.GetCharacterByName(recipientName);
            if (recipient == null)
            {
                Logger.LogInformation("Tradepost mail: {Char} -> unknown recipient '{Recipient}'; rejected.", character.Name, recipientName);
                await TradepostResponses.SendMailSendResult(client, false, packet.Sender.Instance);
                return;
            }

            var mail = new Mail
            {
                MailId = await AllocateMailId(),
                RecipientClass = MailConstants.EndpointClass,
                RecipientName = recipient.Name,
                SenderClass = MailConstants.EndpointClass,
                SenderName = character.Name, // a player mail, NOT a system mail (sender name is set)
                BodyXml = bodyText,
                FooterWord = MailConstants.FooterUnread,
                Money = money,
                Timestamp1 = Statics.NowUnixTimeSeconds,
                Timestamp2 = Statics.NowUnixTimeSeconds + MailConstants.ExpirySeconds
            };

            // Attach the sender's bank item (if a slot was given and an item sits there). The item STAYS in the
            // sender's bank with a "mail-pending" overlay (it does NOT leave on send) - it only leaves when the
            // recipient TAKES it (GetMailItem), and the overlay clears if the sender cancels the mail.
            var rows = await Database.GetCharacterInventory(character.Id);
            var row = rows.FirstOrDefault(r => r.Container == (int)FullCharSnapshotBuilder.BankKind && r.Slot == bankSlot);
            if (row?.Item != null)
            {
                mail.SenderBankSlot = (uint)bankSlot;
                mail.Attachments.Add(new MailAttachment
                {
                    ItemInstanceId = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
                    Quantity = row.Count == 0
                        ? 1u
                        : row.Count,
                    Data1 = row.Item.Data1,
                    Data2 = row.Item.Data2,
                    Data3 = row.Item.Data3,
                    Data4 = row.Item.Data4,
                    Template = row.Item.Id,
                    ItemLevel = row.Item.Level,
                    ItemHash = row.Item.Hash,
                    TrailerWord = MailConstants.AttachmentTrailerWord,
                    TrailerClass = MailConstants.EndpointClass,
                    TrailerTail = 0
                });
            }

            await Database.CreateMail(mail);
            await TradepostResponses.SendMailSendResult(client, true, packet.Sender.Instance);

            // Draw the mail-pending overlay on the kept bank item (capture F500: stat 0 = flags 0x8001).
            if (row?.Item != null)
                await SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatFlags, BankFlagMailPending);

            // Retail pushes a 0x2005 wallet refresh after SendMail too - again with NO postage fee (the captured
            // balance is unchanged across the send-mail-with-attachment capture), #182. Push the current balance so
            // the HUD stays in sync (reflects any attached money debit elsewhere; the item attachment itself is free).
            await SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

            Logger.LogInformation("Tradepost mail: {Char} sent mail 0x{Mail:X16} to {Recipient} (\"{Body}\", money {Money}, attach {HasItem}).", character.Name, mail.MailId, recipient.Name, bodyText, money, row?.Item != null);
        }

        // A unique wire MailId. The id is session/instance-volatile (like entity handles), so a random 64-bit value
        // is fine; retry on the rare collision with an existing persisted mail.
        private async Task<ulong> AllocateMailId()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var id = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                if (await Database.GetMailByWireId(id) == null)
                    return id;
            }

            return (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        }

        // A unique sale id for a new listing (session-volatile, like the mail id); retry on the rare collision.
        private async Task<ulong> AllocateSaleId()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var id = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                if (await Database.GetTradepostListingBySaleId(id) == null)
                    return id;
            }

            return (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        }

        // Builds the system mail for a completed AH purchase: recipient = the buyer, sender empty (system), the bought
        // item escrowed as an attachment, body = the TradePostGUI_MailItemBought remoteformat. The item-name fragment
        // is left empty (the per-item RDB name key is unsynthesizable - see TradepostMailBody); the item still
        // delivers, only its name in the mail text degrades.
        private async Task<Mail> BuildPurchaseMail(string buyer, TradepostListing listing)
        {
            var now = Statics.NowUnixTimeSeconds;

            var mail = new Mail
            {
                MailId = await AllocateMailId(),
                RecipientClass = MailConstants.EndpointClass,
                RecipientName = buyer,
                SenderClass = MailConstants.EndpointClass,
                SenderName = "",
                BodyXml = TradepostMailBody.BuildItemBought(buyer, listing.SaleId, "", listing.SellerName, listing.Price.ToString()),
                FooterWord = MailConstants.FooterUnread,
                Money = 0,
                Timestamp1 = now,
                Timestamp2 = now + MailConstants.ExpirySeconds
            };

            mail.Attachments.Add(new MailAttachment
            {
                ItemInstanceId = listing.SaleId,
                Quantity = listing.Quantity == 0
                    ? 1
                    : listing.Quantity,
                Data1 = listing.Data1,
                Data2 = listing.Data2,
                Data3 = listing.Data3,
                Data4 = listing.Data4,
                Template = listing.Template,
                ItemLevel = listing.ItemLevel,
                ItemHash = listing.ItemHash,
                TrailerWord = MailConstants.AttachmentTrailerWord,
                TrailerClass = MailConstants.EndpointClass,
                TrailerTail = 0
            });

            return mail;
        }

        // The first free backpack cell (0..23), skipping persisted rows + the legacy starter-bag cell while it renders.
        // Null = bag full. Read-only DB helper (mirrors AddItemBuilder.FindFreeBagSlot - kept local to avoid a 4th seam).
        private async Task<int?> FindFreeBagSlot(uint characterId)
        {
            var equipment = await Database.GetCharacterEquipment(characterId);
            var rows = await Database.GetCharacterInventory(characterId);
            var taken = rows.Where(r => r.Container == (int)FullCharSnapshotBuilder.BagKind).Select(r => r.Slot).ToHashSet();
            if (equipment?.FirstPosBag is { Hash: not 0 })
                taken.Add(1);
            return Enumerable.Range(0, 24).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
        }

        // Banks any money attached to a mail into the recipient's wallet and clears it from the mail (#213). Idempotent -
        // a 0-money mail is a no-op - so it's safe to call on every take. Without this the tin was never credited and the
        // unclaimed money kept the client's delete button greyed out (a mail with attachments can't be deleted).
        private async Task GrantMailMoney(INetworkClient client, Mail mail)
        {
            if (mail == null || mail.Money == 0)
                return;

            var character = client.Session.Character;
            var credited = mail.Money;
            character.Money += credited;
            await Database.SetCharacterMoney(character.Id, character.Money);
            await SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
            await Database.SetMailMoney(mail.MailId, 0);
            mail.Money = 0;
            Logger.LogInformation("Tradepost mail: {Char} banked {Tin} tin from mail 0x{Mail:X16}.", character.Name, credited, mail.MailId);
        }

        // Pushes one bank-slot stat update (0x201c on container 105): [kind=105][charInstance][slot][statId][value],
        // byte-exact to the capture (F376/F377/F364/F398). Rides the same GameCharAgent conversation as the bank
        // slot registrations (BankHandler), so the client applies it to the live bank cell.
        private Task SendBankSlotStat(INetworkClient client, uint slot, uint statId, uint value)
        {
            return new PacketStream()
                .WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x58), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, client.Session.Account.ClientInstance), (ushort)0x201C)
                .WriteArray(BuildBankSlotStatBody(client.Session.Character.Id, slot, statId, value))
                .SendAsync(client);
        }

        // GetMailItem: grant the escrowed attachment into the bag, then drop it from the mail and re-send the
        // now-itemless mail so the client clears the attachment. The item is RENDERED live from the attachment's
        // stored INSTANCE identity (Data quad + template + level + hash) - the same fields the AH grid renders from -
        // so it works even when the listed template isn't in our Items table (most AH listings reference retail
        // templates we never harvested, and listing.ItemHash/hash is 0). Persistence is conditional: only a real
        // buildable Items row can be written to CharacterInventory (the ItemId FK requires it) and re-rendered by the
        // world-entry snapshot, so an un-harvested item is a LIVE-ONLY delivery (kept this session, lost on relog).
        // Aborts (item left in the mail) only on a full bag, so the player can retry.
        private async Task TakeMailItem(INetworkClient client, ulong mailId, ulong itemInstanceId, uint inst)
        {
            var character = client.Session.Character;
            var mail = await Database.GetMailByWireId(mailId);
            var attachment = mail?.Attachments?.FirstOrDefault(a => a.ItemInstanceId == itemInstanceId) ?? mail?.Attachments?.FirstOrDefault();
            if (mail == null || attachment == null)
            {
                Logger.LogWarning("Tradepost mail: {Char} tried to take item 0x{Item:X16} from mail 0x{Mail:X16} (no such attachment).", character.Name, itemInstanceId, mailId);
                return;
            }

            var slot = await FindFreeBagSlot(character.Id);
            if (slot == null)
            {
                Logger.LogWarning("Tradepost mail: {Char}'s bag is full; mail 0x{Mail:X16} item kept in mail.", character.Name, mailId);
                return;
            }

            var entry = BuildMailItemEntry(attachment, character.Id, slot.Value);
            await SendAddItem(client, 0x68u, (uint)slot.Value, entry);

            var qty = attachment.Quantity == 0
                ? 1u
                : attachment.Quantity;
            var item = await Database.GetItem(attachment.Template);
            if (item != null)
                // The listing importer harvests every listed item into Items (so the CharacterInventory.ItemId FK
                // holds), letting the bought item persist. Hash-0 items the world-entry snapshot drops are
                // re-rendered on relog by RedeliverNonSnapshotBagItems via the live add path.
                await Database.SetCharacterInventorySlot(character.Id, (int)FullCharSnapshotBuilder.BagKind, slot.Value, attachment.Template, qty);
            else
                Logger.LogInformation("Tradepost mail: delivered template {Template} to {Char} LIVE-ONLY (no Items row; won't persist across relog).", attachment.Template, character.Name);

            // For a PLAYER mail, the attached item was still sitting in the SENDER's bank (with the mail-pending
            // overlay) - now that the recipient has taken it, remove it from the sender's bank so it isn't duplicated.
            // AH system mail has an empty sender (the listed item already left the seller's bank on buy), so skip it.
            if (!string.IsNullOrEmpty(mail.SenderName))
            {
                var sender = await Database.GetCharacterByName(mail.SenderName);
                if (sender != null)
                    await Database.SetCharacterInventorySlot(sender.Id, (int)FullCharSnapshotBuilder.BankKind, (int)mail.SenderBankSlot, null);
            }

            await Database.RemoveMailAttachment(mailId, attachment.ItemInstanceId);
            mail.Attachments.Remove(attachment);
            // #213: also bank any attached money on the take so the re-sent mail shows NO remaining attachment - the
            // client greys out delete while a mail still has unclaimed contents, so leaving the money behind is what
            // pinned the delete button (and "take all" never credited the tin). Retail captures never carry mail money,
            // so there's no observed take-money op; crediting it with the item take matches the player expectation.
            await GrantMailMoney(client, mail);
            await TradepostResponses.SendMailMessage(client, mail, false, inst);
            // Complete the take with a result ack, mirroring MarkMailRead (#213): without it the client's mail-op state
            // machine stays mid-take, so the FIRST delete click after a retrieve is swallowed (the mail survives until the
            // mailbox is reopened, which resets that state); the ack lets the delete go through on the first try.
            await TradepostResponses.SendMailResult(client, true, 0, inst);

            Logger.LogInformation("Tradepost mail: {Char} took item (template {Template}) from mail 0x{Mail:X16} into bag slot {Slot}.", character.Name, attachment.Template, mailId, slot);
        }

        // The 0x201c bank-slot stat-update body: [kind=105][charInstance][slot][statId][value] (all u32 BE). Locked
        // byte-exact to the capture by TradepostBankStatTests.
        internal static byte[] BuildBankSlotStatBody(uint charInstance, uint slot, uint statId, uint value)
        {
            return new FaolanStream().WriteUInt32(FullCharSnapshotBuilder.BankKind).WriteUInt32(charInstance).WriteUInt32(slot).WriteUInt32(statId).WriteUInt32(value).ToArray();
        }

        // Builds the live DispatchAddItem entry for a mail attachment from its stored instance identity (no Items
        // lookup), mirroring SnapshotItemBuilder's bag entry: ProtoAcgItem{key=Data quad, template1/2=template,
        // level, hash} + the bag stat triple (0=flags, 23=recordId, 26=count). The client resolves the visual from
        // the template via its RDB, so this renders even for items we don't have as Items rows.
        private static ProtoInventoryGameEntry BuildMailItemEntry(MailAttachment a, uint charId, int slot)
        {
            var key = new ProtoDecryptionKey
            {
                Key1 = a.Data1,
                Key2 = a.Data2,
                Key3 = a.Data3,
                Key4 = a.Data4
            };
            var qty = a.Quantity == 0
                ? 1u
                : a.Quantity;
            var entry = new ProtoInventoryGameEntry
            {
                ItemId = FullCharSnapshotBuilder.ItemId(charId, FullCharSnapshotBuilder.BagKind, (ulong)slot),
                ItemData = new ProtoAcgItem
                {
                    Key1 = key,
                    Key2 = key,
                    Key3 = key,
                    Template1 = a.Template,
                    Template2 = a.Template,
                    Level = a.ItemLevel,
                    Hash = a.ItemHash
                }
            };
            entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
            {
                Stat = 0,
                Value = 1
            });
            entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
            {
                Stat = 23,
                Value = (uint)FullCharSnapshotBuilder.RecordId(charId, FullCharSnapshotBuilder.BagKind, (ulong)slot)
            });
            entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
            {
                Stat = 26,
                Value = qty
            });
            return entry;
        }
    }
}
