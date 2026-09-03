using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;
using ProtoBuf;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{

		// IGameServerProvider seam: the PAY-OUT half of a guild-bank withdrawal (#132). The CS tier decides it (it owns
		// Guild.Cash and the rank ladder) and this tier pays it, because the wallet lives on the live session and the
		// client only learns of a change through this tier's UpdateCash.
		//
		// Refusing when the character is not connected here is the point, not a technicality: the caller debits the
		// guild only if this returns true, so a payout that nobody could receive never happens rather than vanishing.
		public async Task<bool> CreditCharacterCash(uint characterId, ulong amountTin)
		{
			var client = ClientForCharacter(characterId);
			var character = client?.Session.Character;
			if (character == null)
				return false;

			character.Money += amountTin;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await _economyOutbound.SendUpdateCash(client!, character.Money, UpdateCashReason.Gain);
			Logger.LogInformation("CreditCharacterCash: {Character} received {Amount} tin (wallet {Balance}).", character.Name, amountTin, character.Money);
			return true;
		}

		// The deposit half of the guild bank (#654), the exact inverse of CreditCharacterCash above: there the CS tier
		// debits the guild and this tier credits the wallet, here this tier debits the wallet and the CS tier credits
		// the guild. Both do the debit on the side that HOLDS the money, so a failure between the two steps loses the
		// transfer rather than minting one.
		//
		// A payer who cannot afford it gets retail's own named refusal (`DispatchGuildNotEnoughCash`) rather than
		// silence: unlike the requests this codebase deliberately leaves unanswered, the client CAN legitimately send
		// this one - it does not know the wallet moved - and the client has a message for exactly this case.
		private async Task HandleGuildDeposit(INetworkClient client, GuildTransferMoney request)
		{
			var character = client.Session.Character;
			if (character == null || request.AmountTin == 0)
				return;

			if (request.AmountTin > character.Money)
			{
				Logger.LogInformation("GuildTransferMoney refused: {Character} offered {Amount} tin of their {Balance}.", character.Name, request.AmountTin, character.Money);
				// ⚠️ THE ENDPOINTS ARE THE WEAK PART, and they are not recovered from anything. The client's method
				// table gives the METHOD and its PARAMETERS; it says nothing about the RpcHeader, whose sender is a
				// server OBJECT id we only ever learn from a capture - and no capture performs a guild-bank deposit.
				//
				// Worse, the instance is demonstrably NOT constant per interface: captured GameCharInterface replies
				// arrive from GameCharAgent{0x58} (the login/world-handshake family) AND from GameCharAgent{0x10}
				// (the trade family). Which server object owns the guild bank is unknown, so 0x10 is a COIN FLIP
				// taken from the nearest neighbour, exactly like the quest-share dispatches below.
				//
				// That is the failure mode #101's team-invite window cost a day to: a wrong sender looks like a wrong
				// body and is not one. If this refusal does not render in-client, suspect this line before the codec.
				// [V0: the guild-bank reply endpoints]
				await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GuildNotEnoughCashCodec.Method, GuildNotEnoughCashCodec.Build(request.AmountTin)));
				return;
			}

			character.Money -= request.AmountTin;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await _economyOutbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			if (await _csPlayerAgentDispatcher.DepositToGuildBank(character.Id, request.AmountTin))
				return;

			// Nobody could take it - put it back rather than leave it nowhere (the withdrawal's refund, mirrored).
			character.Money += request.AmountTin;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await _economyOutbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			Logger.LogWarning("GuildTransferMoney rolled back: {Character}'s guild is not on this realm's CS tier.", character.Name);
		}

		// IGameServerProvider seam: pushes the guild tag onto the character's live world connection, if it is
		// connected to this listener. Used by the CsPlayerAgent guild flow (accept/create/leave) so the in-world
		// tag updates without a relog.
		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(GameWire.BuildGuildTag(client, characterId, guildName ?? ""));
			Logger.LogInformation("SendGuildTag: set in-world guild tag of {CharacterId:X8} to \"{GuildName}\".", characterId, guildName);
			return true;
		}

		// IGameServerProvider seam: the world-connection half of a team change. TeamService owns the roster (it spans
		// the CS tier and this one); this only puts the record on the wire for a member connected HERE.
		public async Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined)
		{
			var client = ClientForCharacter(recipientId);
			if (client?.Session.Account == null)
				return false;

			await WorldIir.Send(client, GroupMembershipCodec.Build(joined
				? new AddToGroupIIR(memberId, teamId, leaderId)
				: new RemoveFromGroupIIR(memberId, teamId)));
			return true;
		}

		// IGameServerProvider seam: the two PvP-minigame messages that END a match. The match lives on the CS tier
		// (PvpMatchService owns the roster, the clock and the return trip) but every Minigame* dispatch is a
		// GameCharInterface message and so travels on THIS connection - the same split the match-start clock already
		// crosses in WorldEntryService. ⚠️ Client-recovered, uncaptured: no captured session finishes a
		// minigame (docs/re/minigame-massivepvp-messages.md).
		public async Task<bool> SendMinigameMatchEnded(uint characterId, uint winningSide, MinigameResult yourResult)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchMinigameMatchEnded,
				s => s.WriteUInt32(winningSide).WriteUInt32((uint)yourResult)));
			Logger.LogInformation("PvP minigame: told {CharacterId:X8} the match ended - winning side {Side}, result {Result}.", characterId, winningSide, yourResult);
			return true;
		}

		public async Task<bool> SendMinigameForceStop(uint characterId, string message)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchForceStopMinigame,
				s => s.WriteString(message)));
			Logger.LogInformation("PvP minigame: force-stopped for {CharacterId:X8} - \"{Message}\".", characterId, message);
			return true;
		}

		// The first free backpack cell, skipping occupied ones. The backpack size is 24 base + the equipped bag's extra
		// slots (#12), so a grown bag can be filled. Null = full. Read-only DB helper (mirrors
		// AddItemBuilder.FindFreeBagSlot - kept local to avoid a 4th seam).
		private async Task<int?> FindFreeBagSlot(uint characterId)
		{
			var taken = (await Database.GetCharacterItems(characterId, ContainerKind.Bag)).Select(i => i.Slot).ToHashSet();
			var capacity = await _economyOutbound.BackpackCapacity(characterId);
			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		// #792: the CloseShop ACK. Retail answers `CloseShop` with `DispatchAbortTrade` (GameCharInterface `0x6A`,
		// zero-length) and we answered nothing at all - so the vendor session was left open on the client's side of
		// the model.
		//
		// The name is the CLIENT's: its handler is `CoreTradeHolder::TradeAbort()`, whose own assert string reads
		// *"is closing all trades"* - a vendor session IS a trade to the client, which is why one message serves both.
		// The TRIGGER is measured rather than assumed: of the 153 frames across 132 captures, **152 are the immediate
		// reply to `CloseShop`** (the odd one out follows an `UpdateQuestMarkerNPC`, i.e. a shop closed by walking
		// away). [V2: Hakotep_hox_login_gameplay0.tshark.json fr695 -> fr697, and 151 more like it across the corpus]
		//
		// ⚠️ It aborts EVERY open trade, not just the shop, so it must not be sent while a player-to-player trade is
		// running. Nothing here can do that today - a shop and a trade cannot both be open - but the constraint is the
		// reason this is not a general "close the window" message.
		private static Task SendTradeAbort(INetworkClient client)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAbortTrade, _ => { }));
		}

		// Answers a TradeskillRequestIIR (recipe-book open) with the TradeskillReplyIIR (0x9062D559) recipe list built
		// from the Recipe catalog, so the Crafting -> Recipes window populates. The carrier is the standard world IIR
		// framing (GameAgent 0x58 -> GameInterface <instance>), body = [subop][0xC350][handle][reply], byte-exact vs
		// the capture (docs/subsystems/tradeskill-recipes.md). Empty catalog -> an empty (count=0x3F1) list, which opens the book
		// cleanly with no recipes.
		private async Task SendCraftingRecipeBook(INetworkClient client)
		{
			var handle = client.Session.Character.Id;

			// #735: only what the character has TRAINED for. A fresh character has learned no profession and so gets
			// an EMPTY book, which is retail - the book is what a profession buys. The gate is the recipe's own
			// `RequiredFeatId` against the character's rank on that profession's ladder; see TradeskillService.
			var rows = await _tradeskill.BookFor(handle, await Database.GetRecipes());
			var records = rows.Select(RecipeRecords.FromRow).ToList();

			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TradeskillReplyCodec.Build(handle, records))));

			Logger.LogInformation("Sent crafting recipe book: {Count} recipe(s) to client {Handle} (TradeskillReplyIIR).", records.Count, handle);
		}

		// 0x4F57DC08 GameTime - the live world clock (#351). Both fields are generated: the resync anchor from the wall
		// clock and the time of day from it at 5 game seconds per real second, wrapping every 86400 (see GameTimeCodec
		// for where those come from). `_clock.OffsetGameSeconds` shifts the phase for the `.time` GM command.
		internal Rpc BuildGameTime(INetworkClient client)
		{
			return GameWire.BuildGameTime(client, DateTimeOffset.UtcNow, _clock.OffsetGameSeconds, _clock.Rate);
		}


		// DispatchDisplayItemLootChatMessage (0x2012) - the "you receive N x <item>" chat line. Body is
		// [u32 itemId][u32 itemId][u32 0][u32 count] followed by the two 24-byte identity halves the capture repeats,
		// which we send as zero: the client renders the item from the id, and the halves it echoes are the same value
		// twice, so there is nothing per-session to reproduce.
		//
		// Decoded from the harvesting span of the chat-annotated capture (#494), where the loot line lands beside its
		// annotation each time - Granite 161206 x40 (`just mined`), Oak 161213 x60 (`gethering wood`), Iron 161196 x40
		// (`mining iron`). The item id is repeated in the first two words in every observed record.
		// ⚠️ The 4th word is the item's LEVEL, not a count. The body is the same ProtoAcgItem-shaped layout the
		// client's `itemref://` chat link carries - [template][template][0][level][hash][key1][key2][key3] - and
		// reading it as a count is what put the bogus 40 / 60 / 40 yields into HarvestCatalog (#732): Items.Level for
		// Granite, Oak and Iron is 40, 60 and 40 exactly. The line renders an item LINK, so the level is what its
		// tooltip reads; how many arrived is the ADD's business, not this message's.
		// The 48-byte tail is the hash + the three key quads, which we do not fill - the link still renders.
		private static Task SendItemLootChatMessage(INetworkClient client, uint itemId, uint level)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchDisplayItemLootChatMessage,
				b => b.WriteUInt32(itemId).WriteUInt32(itemId).WriteUInt32(0u).WriteUInt32(level).WriteArray(new byte[48])));
		}

		// Sends the proven captured system line - the test bootstrap for the ChatTextIIR path until a literal-text
		// remoteformat template lands.

		// #637: raises retail's "Do you want to travel to <map>?" box for a zone-line door, and remembers the trip it
		// is confirming. The (threadCtxId, cookie) pair is retail's script-thread handle; the client only ever echoes
		// it, so ours are a correlation token rather than a real thread - the character id plus a per-send counter,
		// which is unique within a session and is all the answer has to match.
		//
		// Retail's own sender endpoint instance varies per box across the corpus (0x54/0x56..0x5A - it is the scry
		// agent that happens to own the script), so 0x58 here is the house default, not a captured constant.
		private async Task SendTravelConfirmation(INetworkClient client, Map destination, Vector3? arrival)
		{
			var threadCtxId = client.Session.Character.Id;
			var cookie = (ulong)Interlocked.Increment(ref _travelConfirmCookie);
			client.Session.PendingTravelConfirm = new PendingTravelConfirm(threadCtxId, cookie, destination.Id, arrival);

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchScryShowMessageBox,
				ScryShowMessageBoxCodec.Build(destination.Id, LdbKey.ToWire(destination.TravelConfirmKey), threadCtxId, cookie)));

			Logger.LogInformation("Travel confirm for '{Map}' ({MapId}) sent to character {CharacterId} (cookie 0x{Cookie:X16}).", destination.Name, destination.Id,
				client.Session.Character.Id, cookie);
		}

		// #821: the ACG INSTANCE BIND box - the confirmation retail raises instead of teleporting when the destination
		// is a personal instance. The client answers with RequestACGTeleport (or, if the player declines, with nothing
		// at all), so the offer is recorded on the session for the handler to check against.
		//
		// The proxy is built for the DESTINATION: its class word is what marks the playfield as ACG, and it is echoed
		// back verbatim in the request. The instance token is 0 - we do not mint per-player instance tokens (#268), and
		// the capture's token is that session's, not a per-map constant.
		private async Task SendAcgTeleportChoice(INetworkClient client, uint destinationMapId, uint playfieldClass, Vector3? arrival)
		{
			client.Session.PendingAcgTeleport = new PendingAcgTeleport(destinationMapId, arrival);

			var proxy = PlayfieldProxy.ForMap(destinationMapId) with { ClassWord = playfieldClass };
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x56), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchACGTeleportChoice,
				AcgTeleportChoiceCodec.Build(new AcgTeleportChoice(client.Session.Character.Id, proxy))));

			Logger.LogInformation("ACG bind box for map {MapId} sent to character {CharacterId}.", destinationMapId, client.Session.Character.Id);
		}


		// WHICH of the two vitals blocks to send is decided by WHAT MOVED, and both forms are retail's (#586).
		//
		// `EntityVitals.ToCurrentsBlock` - the three CURRENT ids - is for a push where only a current moved: a hit, a
		// cast's mana cost, the regen tick, fall damage. It is what retail overwhelmingly sends
		// (`--report vitals-shape`: 3,694 of the 4,900 blocks on a non-own handle carry exactly
		// {HealthCurrent, StaminaCurrent, ManaCurrent}).
		//
		// `EntityVitals.ToVitalsBlock` - current AND max - is for a push where a MAX moved: a level-up, a gear or buff
		// change to a pool, a GM `.set`. Use CombatService.ReseedFromMaxStats(client).ToVitalsBlock() there.
		//
		// ⚠️ The rule is "what moved", NOT "always send everything", and it is not "always send currents" either.
		// There used to be a blanket `SendVitals` pair emitting the three currents alone, justified as "matches the
		// retail full-spawn announce; max rides the snapshot" - and it was removed because the max ids reach the
		// client only when something sends them, so every later max change was invisible and the bar kept its
		// world-entry seed (#450). The replacement over-corrected the other way: sending all six on every swing is
		// three fabricated records per entity per hit on the hottest path in the game, and for an NPC - which has no
		// class curve, so ClassBasePools is null - the max ids fall back to our ceiling, which is not the quantity
		// that slot holds. Neither blanket is right; the split above is.

		// Sends a pre-framed world IIR body ([len]-prefixed) on the world conversation (opcode 0x2000).

	}
}
