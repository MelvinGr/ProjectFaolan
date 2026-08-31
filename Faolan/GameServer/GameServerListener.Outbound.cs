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
using Microsoft.Extensions.Logging;
using ProtoBuf;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The backpack's slot count: base 24 + the equipped bag's Item.BagExtraSlots (equip slot 3 / Bag) - the same
		// sizing the snapshot uses (FullCharacterRoots), so a grown bag can actually be filled. #12.
		private const uint BaseBackpackSlots = 24;

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

			await client.SendAsync(BuildGuildTag(client, characterId, guildName ?? ""));
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
		// crosses in GameServerListener.WorldEntry. ⚠️ Client-recovered, uncaptured: no captured session finishes a
		// minigame (docs/re/minigame-massivepvp-messages.md).
		public async Task<bool> SendMinigameMatchEnded(uint characterId, uint winningSide, MinigameResult yourResult)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchMinigameMatchEnded,
				s => s.WriteUInt32(winningSide).WriteUInt32((uint)yourResult)));
			Logger.LogInformation("PvP minigame: told {CharacterId:X8} the match ended - winning side {Side}, result {Result}.", characterId, winningSide, yourResult);
			return true;
		}

		public async Task<bool> SendMinigameForceStop(uint characterId, string message)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchForceStopMinigame,
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

		// Removes an item from the player's backpack: deletes the persisted bag item (so it stays gone across relog) AND
		// pushes the live DispatchRemoveItem so it disappears immediately. The mirror of GrantItemToBackpack. Returns
		// true if an item was there to remove.
		private async Task<bool> RemoveItemFromBackpack(INetworkClient client, int slot, uint amountToRemove = 0)
		{
			var characterId = client.Session.Character.Id;
			if (await Database.GetCharacterItemSlot(characterId, ContainerKind.Bag, slot) == null)
				return false;

			await Database.SetCharacterItemSlot(characterId, ContainerKind.Bag, slot, null);
			await EconomyOutbound.SendRemoveItem(client, ContainerKind.Bag, (uint)slot, amountToRemove);

			// Retail follows a SERVER-initiated removal with both containers in full (#380). This path is exactly that
			// case - the server took something out of the bag, the player did not move it - which is the shape the
			// ShacklesOff capture shows. A player-initiated move is NOT followed by one: the client already knows.
			await _economyOutbound.SendUpdateInventory(client);
			return true;
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
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAbortTrade, _ => { }));
		}

		// #792: the zero-length GameCharInterface `0x1B` retail sends immediately before every `IntendedTeleportIIR` -
		// **102 of 102 across the corpus, no exceptions**, on cross-map handoffs and same-map revives alike.
		//
		// ⚠️ The client does NOTHING with it: `GamecodeClient_t`'s override of the vtable slot it dispatches to
		// (+0x70) is an empty function. So this is sent for fidelity with retail's byte stream and for nothing else -
		// do not hang behaviour off it, and do not go looking for what it "should" do.
		internal static Task SendTeleportPending(INetworkClient client)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchTeleportStarted, _ => { }));
		}

		// Awards a TAG (#748): `DispatchAddTag`, body = the tag id and nothing else, 4 bytes. That covers two
		// phenomena sharing one id space - an ACHIEVEMENT (the catalog is LDB 55701/55700, imported into
		// `Achievements`: 7917 "Seeing Red" / *"Achieve a kill streak."*, 716 "Old Tarantia Docks") and the item-shop
		// "bundle shown" flags whose `HasTag` gates live in the client's own shop Lua (6617-6620, 7316, 7416-7421).
		// [V2: NecroL80_..._gameplay_annotated_2 t+285.5 tag 0x2CC; 23 awards over 14 captures]
		//
		// ⚠️ There is no login re-send, and nothing needs one: NOT ONE of the 23 captured awards lands in a login
		// burst, because the client learns the set it already holds from the FullChar snapshot (SendBurstlessWorldEntry
		// unions `GetCharacterTags` into it). So this message is strictly the moment-of-award event, which is why the
		// caller must not send it for a tag the character already has.
		//
		// A REMOVE twin exists in the client's method table - `DispatchRemoveTag` (0x17) - with zero captured frames, so
		// nothing is built for it.
		private static Task SendAddTag(INetworkClient client, uint tagId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddTag,
				b => b.WriteUInt32(tagId)));
		}

		// Grants a tag and tells the client, once. Persists first (so a relog keeps it, via the snapshot) and only
		// sends the award if the row was new - re-awarding a held tag would pop the client's banner again.
		// Returns the achievement it named, or null for an id the client's catalog does not carry (an item-shop flag,
		// or an entry the catalog is short of); false in `Granted` means they already had it.
		private async Task<(bool Granted, Achievement? Achievement)> GrantTag(INetworkClient client, uint tagId)
		{
			var granted = await Database.AddCharacterTag(client.Session.Character.Id, tagId);
			if (granted)
				await SendAddTag(client, tagId);

			return (granted, await Database.GetAchievement(tagId));
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

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TradeskillReplyCodec.Build(handle, records))));

			Logger.LogInformation("Sent crafting recipe book: {Count} recipe(s) to client {Handle} (TradeskillReplyIIR).", records.Count, handle);
		}

		// Lists maps for `.maps`, optionally filtered to those whose name CONTAINS `search` (case-insensitive) -
		// e.g. `.maps castle`. A blank search lists all maps.
		private async Task SendListAllMaps(uint characterId, string? search = null)
		{
			var maps = (await Database.GetMaps()).OrderBy(m => m.Name).AsEnumerable();
			if (!string.IsNullOrWhiteSpace(search))
				maps = maps.Where(m => m.Name != null && m.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

			var matches = maps.ToList();
			if (matches.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{search?.Trim()}'."));
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. matches.Select(ChatMarkup (m) => $"{m.Id}: {m.Name}")]));
		}

		// 0x4F57DC08 GameTime - the live world clock (#351). Both fields are generated: the resync anchor from the wall
		// clock and the time of day from it at 5 game seconds per real second, wrapping every 86400 (see GameTimeCodec
		// for where those come from). `_gameTimeOffset` shifts the phase for the `.time` GM command.
		internal Rpc BuildGameTime(INetworkClient client)
		{
			return BuildGameTime(client, DateTimeOffset.UtcNow, _gameTimeOffset, _gameTimeRate);
		}

		// `now` and the offset are parameters so a golden test can feed the capturing instant and the captured dayTime
		// and get the captured bytes back.
		internal static Rpc BuildGameTime(INetworkClient client, DateTimeOffset now, long offsetGameSeconds, float rate = 1.0f)
		{
			return BuildGameTime(client, GameClock.TimeOfDay(now, offsetGameSeconds), now, rate);
		}

		internal static Rpc BuildGameTime(INetworkClient client, float dayTimeSeconds, DateTimeOffset now, float rate = 1.0f)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(GameTimeCodec.Build(dayTimeSeconds, now, rate)));
		}

		// Frames the GuildUpdateIIR_c (GuildUpdateCodec) on the world conversation: [RpcHeader][DispatchPassBlob]
		// [u32 len][GuildUpdateIIR]. Sent on login for guilded characters and pushed live by the CsPlayerAgent
		// guild-accept flow through the dispatcher seam. The body grammar + round-trip live on GuildUpdateCodec.
		internal static Rpc BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(GuildUpdateCodec.Build(characterId, guildName)));
		}

		// 0x5D85BFC7 MapChange = the PlayfieldFIIR: tells the client which playfield/instance the player is entering.
		// This is now a thin wrapper over the shared CreateEditorRecordBuilder.BuildPlayfieldFiir codec (the create-flow
		// builds the SAME packet) - it just prepends the MapChange subop and sources the PER-MAP rule fields from the Map
		// row (#308). The client keys per-area rules on this packet (mount permission = PlayfieldFlags); those fields are
		// NOT universal (beach vs mainland differ), so they come from the DB (Map.PlayfieldCount/BlobLastWord/Flags/Instanced), 		// defaulting to the beach values for un-sourced maps. Position is the character's entry transform (per-session);
		// nearClip 15.0 + the farClip float are camera params (left constant). `map` may be null (fresh/unmapped -> beach
		// defaults).
		// The word at body [55:59] (client obj+0x54). We send 1 for every map, which is what 97 of the 151 captured
		// records carry; the other 54 carry 0, and the split is not per-map (4000 and 4010 carry both). Was written
		// as 0x01000000 while the builder's framing was a byte out - see BuildPlayfieldFiir (#451). [V0]
		private const uint SceneWord = 1u;

		// ⭐ #512: `teamId` names the TEAM that owns this copy of the playfield, when one does. The proxy's owner pair
		// is `(type, id)`, and retail's own group entry carries `(0xDEA9, <teamId>)` there where a personal instance
		// carries `(PlayfieldFieldA, <characterId>)` - the Amphitheatre of Karutonia entry names the exact id
		// `DispatchYouJoinedTeam` had announced three seconds earlier. So the pair is switched WHOLE rather than
		// having the id swapped under the map's own type word: a team id under type 2 would be a shape retail never
		// sends. Null (the common case) leaves the personal form exactly as it was.
		internal static Rpc BuildMapChange(INetworkClient client, Map map, uint playfieldToken, uint? teamId = null, bool epic = false)
		{
			var character = client.Session.Character;
			var charInstance = map?.PlayfieldInstanced ?? true
				? character.Id
				: 0u;
			var count = map?.PlayfieldCount ?? 2u;
			var blobLastWord = map?.PlayfieldBlobLastWord ?? 3u;
			var flags = map?.PlayfieldFlags ?? 0u;
			// #451: the two per-map words that used to be builder constants. The `??` defaults are those constants, so
			// a map with no imported MapChange behaves exactly as before rather than silently getting a zero.
			var fieldA = map?.PlayfieldFieldA ?? 0u;
			var fieldC = map?.PlayfieldFieldC ?? 7u;

			// The owner pair, switched as a unit when a team owns the copy (see the remark above).
			var ownerType = teamId is null ? fieldA : PlayfieldProxy.TeamOwnerType;
			var ownerId = teamId ?? charInstance;

			// #309: the proxy's class word is the copy's DIFFICULTY - 0xCABC on the captured epic Spider Caverns
			// entry against 0xC79C everywhere else. Everything else in the record is mode-independent.
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(PlayfieldFCodec.Build(new Vector3(character.PositionX, character.PositionY, character.PositionZ), character.MapId,
					ownerId, count, playfieldToken, 15.0f, BitConverter.UInt32BitsToSingle(0x40E3944Fu), flags, SceneWord, blobLastWord, ownerType, fieldC,
					epic ? PlayfieldProxy.EpicPlayfieldClass : PlayfieldProxy.PlayfieldClass)));
		}

		// 0x96B8DC59 - entity vitals / stat-update packet. The wire builder now lives on World.VitalsPacket (shared
		// with the CombatService); this delegates so the existing SendStatUpdate/SendVitals/BuildVitals callers
		// (login replay, GM, the regen tick) are unchanged.
		internal static Rpc BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return VitalsPacket.BuildStatUpdate(client, stats, entityHandle);
		}

		// Builds the player's own vitals (0x96B8DC59) from the player's LIVE world-model vitals - the world-entry
		// replay emits this instead of replaying the captured player's vitals.
		//
		// `vitals` must be the live EntityVitals, not `character.MaxVitals()`. The raw class/level curve does not
		// include worn gear, so sending it told the client a current the server did not agree with: an L23 Assassin
		// with +156 stamina of gear was announced at 742 while the world model held 898, and the bar sat short of
		// where regen would immediately put it.
		internal static Rpc BuildVitals(INetworkClient client, EntityVitals vitals)
		{
			var character = client.Session.Character;
			var stats = new CharacterMaxStats { Health = vitals.MaxHealth, Mana = vitals.MaxMana, Stamina = vitals.MaxStamina };
			// Emit the full value under the CURRENT ids (0x001B/0x01F8/0x01FA) - the retail full-spawn announce sends
			// current (== max at full health); the max ids ride the snapshot/tails. (in-client proof: CapturedCharacterStats.)
			// Also carry Level + Exp/MaxExp so the XP bar reflects the character's REAL progress: the snapshot tails
			// carry the captured template's stale to-next-level max, so overriding it here (the same Exp/MaxExp the
			// combat XP path sends) scales the bar correctly for this character's level.
			// #214: a dead character must NOT get its current health refilled here - the world-entry snapshot just set
			// HealthCurrent = 0 to latch the client's dead state, and this post-snapshot vitals update would otherwise
			// refill the bar (and un-latch the release window). Send 0 current health while dead.
			var currentHealth = character.IsDead
				? 0u
				: stats.Health;
			var block = new CharacterStatBlock()
				.Set(CharacterStatId.HealthCurrent, currentHealth)
				.Set(CharacterStatId.StaminaCurrent, stats.Stamina)
				.Set(CharacterStatId.ManaCurrent, stats.Mana)
				// Level AND ApprenticeLevel - the client divides by the latter for armour mitigation, and the
				// nameplate renders max() of the two. See CharacterStatBlock.SetLevel.
				.SetLevel(character.Level)
				.SetExperience(character.Level, character.Experience);

			return BuildStatUpdate(client, block, client.Session.Character.Id);
		}

		// Assembles the complete world-entry snapshot packet for `client` from a pre-built root + tails. The
		// RECORD ([IirType=0x737A6DF9][TypeTag][Handle=instance][blob frame][root][tails]) is built by
		// FullPlayerCharacterCodec, which owns the blob frame next to the reader that parses it - it used to be
		// stated here as well, and the two drifted (see that type's header). The carrier's u32 length is written
		// HERE, because it belongs to the sender (#530 rule 2), together with the RpcHeader + outer length/CRC. No
		// captured packet is consulted.
		internal static Rpc BuildWorldEntrySnapshot(INetworkClient client, ProtoFullCharacterIIR root, FullCharSnapshotSubClass tails)
		{
			var record = FullPlayerCharacterCodec.Build(new FullPlayerCharacterIIR(client.Session.Character.Id, root, tails));

			using var body = new FaolanStream();
			body.WriteArrayUInt32Length(record);
			return FrameWorldEntryRecord(client, body.ToArray());
		}

		// Assembles the complete world-entry player/NPC FA015807 spawn packet for `client` from a pre-built spawn
		// RECORD ([IirType=0xFA015807][TypeTag][Handle][FieldA][transform + pre-IIR + CharacterIIR + post-IIR]) - the
		// form SimpleCharFullUpdateCodec builds. The carrier's u32 length is written HERE, because it belongs to the
		// sender (#530 rule 2), which is what lets a from-scratch record reach the wire without a captured header.
		internal static Rpc BuildWorldEntrySpawn(INetworkClient client, byte[] spawnRecord)
		{
			ArgumentNullException.ThrowIfNull(spawnRecord);
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(spawnRecord));
		}

		// Prepends the shared world-entry RpcHeader + outer length/CRC (the WriteHeader/WriteLengthHash path) to a
		// record body that ALREADY carries its own [u32 bodyLen] prefix - i.e. the `packet[bodyStart..]` form that
		// SpawnPacketBuilder and WriteArrayUInt32Length produce. This is the keystone that lets a
		// from-scratch record body reach the wire without a captured packet header.
		internal static Rpc FrameWorldEntryRecord(INetworkClient client, params ReadOnlySpan<byte> bodyWithLenPrefix)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, bodyWithLenPrefix.ToArray());
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
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchDisplayItemLootChatMessage,
				b => b.WriteUInt32(itemId).WriteUInt32(itemId).WriteUInt32(0u).WriteUInt32(level).WriteArray(new byte[48])));
		}

		// #348: the top-of-screen FIFO banner naming the SUB-AREA the player just walked into. Retail fires one on
		// every sub-area crossing, not once per playfield - Old Tarantia alone has six (Bridge of Vilerus I, The East
		// Gate, The West Gate, ...) - so the world tick drives this off MapArea, and the entry banner is simply the
		// first crossing. `areaNameId` is the client's localized-string id; the client renders the text itself.
		//
		// This used to take a mapId and read the single `Map.AreaNameId`, which could only ever name one area per
		// playfield (it had picked "The West Gate" for Old Tarantia, an arbitrary one of the six).
		internal static Task SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return SendFeedback(client, new AreaNameBanner(areaNameId));
		}

		// Pushes a FormatFeedback message to the client's on-screen feedback, addressed to their GameInterface
		// instance. The PAYLOAD carries its own typeFlags/footer (#539) - this used to take the child's text plus those
		// two words, which is how the same message came to be framed differently in two services.
		//
		// The carrier's [u32 len] is the SENDER's (#530 rule 2), which is what WorldIir.Send writes - the codec emits
		// the record alone.
		internal static Task SendFeedback(INetworkClient client, FeedbackPayload payload)
		{
			return WorldIir.Send(client, FormatFeedbackCodec.Build(client.Session.Character.Id, payload));
		}

		// #372: the "resurrection point discovered" FIFO banner. Retail sends it in the SAME frame as the
		// resurrection-spots re-send that adds the point to the player's list, and it is what the player actually
		// sees; only the point id goes on the wire (the client resolves the name from LDB category 270).
		internal static Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return SendFeedback(client, new RespawnPointDiscovered(pointId, nameKey));
		}

		// Sends the proven captured system line - the test bootstrap for the ChatTextIIR path until a literal-text
		// remoteformat template lands.

		internal static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(BuildStatUpdate(client, stats, entityHandle));
		}

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

		// Pushes the death/release screen: the player's death position + the resurrection spots they can pick.
		// 0x2014 DispatchResurrectRequest: "somebody is offering to resurrect you" - for now always YOURSELF, from a
		// self-resurrection consumable (#890). The client holds the offer and answers on the GameCharAgent tier with
		// ResurrectMe(spellId) or CancelResurrectionRequest(who, spellId), so the spell id is a token that comes back
		// rather than something the client has to resolve.
		//
		// `castSpellOnResurrect` is true: the captured request carries 1, and the effect that arms the offer carries 1
		// in the matching field. A player-to-player resurrect would send the CASTER as the resurrector; nothing sends
		// one yet.
		private static Task SendResurrectRequest(INetworkClient client, uint characterId, uint resurrectSpell)
		{
			// Same server endpoint as the release screen next door - the capture addresses both identically
			// (GameCharAgent instance 0x57 there, 0x59 in the beach capture: an arbitrary server handle, not session
			// data), and the receiver is the player's own GameCharInterface instance.
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				ResurrectRequestCodec.Method, ResurrectRequestCodec.Build(characterId, resurrectSpell)));
		}

		internal static Task SendValidResurrectionSpots(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchValidResurrectionSpots, ValidResurrectionSpotsCodec.Build(ValidResurrectionSpotLists.Known(deathPosition, spots))));
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

		// ---- SPELL RESISTANCE (#869) ----
		//
		// `DispatchAddSpellResistance` / `DispatchRemoveSpellResistance` - a per-character resistance to one spell
		// METATYPE (LDB category 55200), on its own registry rather than on the stat sheet or the buff list.
		//
		// ⚠️ Retail never sends this ALONE: every captured frame accompanies a `Criminal Protection` apply or end in
		// the same frame (`CriminalStatus.CriminalProtectionSpellId`). When #503 grants that buff, the matching
		// resistance belongs beside it - which is why this takes the add/remove verb rather than being two methods.
		private static Task SendSpellResistance(INetworkClient client, uint metaType, uint percentage, bool add)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				add ? GameCharInterfaceMethods.DispatchAddSpellResistance : GameCharInterfaceMethods.DispatchRemoveSpellResistance,
				SpellResistanceCodec.Build(metaType, percentage)));
		}

		// ---- MAP MARKERS (#868) ----
		//
		// `DispatchAddWayPoint` / `DispatchRemoveWayPoint` on GameCharInterface. The endpoint pair is the ordinary
		// GameCharAgent -> GameCharInterface one every other per-character dispatch uses; the captured sender
		// instances (0x57 / 0x59 / 0x5A) are the per-session conversation ids, not a distinct conversation.
		private static Task SendAddWayPoint(INetworkClient client, uint tombstoneInstance, uint playfieldId, float x, float y, float z,
			uint label = WayPointCodec.TombstoneLabel, uint toolTip = WayPointCodec.TombstoneToolTip)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchAddWayPoint, WayPointCodec.Build(tombstoneInstance, playfieldId, x, y, z, label, toolTip)));
		}

		private static Task SendRemoveWayPoint(INetworkClient client, EntityId id, uint playfieldId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchRemoveWayPoint, RemoveWayPointCodec.Build(id, playfieldId)));
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// tools/capture/decode_learn_spell.py, byte-exact in SpellBookChangeTests). The client pops the "learned a new
		// spell" toast itself. (The siblings RemoveSpecialAbility 0x2053 / UntrainSingleFeat 0x2011 have no capture
		// oracle yet, so they are not built - see #73.)
		internal static Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchLearnSpell,
				SpellBookChangeCodec.Build(spellId)));
		}

		// The revoke half (DispatchForgetSpell, s2c 0x200B) - takes one spell back out of the client's spellbook
		// (#915). Same conversation and same one-u32 body as the grant; the client's own dispatch reader for the two
		// is the same function twice (see SpellBookChangeCodec). No capture carries a forget - retail only sends one
		// when something un-teaches a spell, which the corpus's sessions never do - but the client's reader settles
		// the body, so this is recovered, not invented.
		internal static Task SendForgetSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchForgetSpell,
				SpellBookChangeCodec.Build(spellId)));
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		// `sender` defaults to the login/world grant sender (GameCharAgent 0x58); the create-editor world-prep grant
		// rides its own conversation (GameCharAgent 0x0C), so it passes that in - the only thing that differs between
		// the two grant sites (the 81-byte body incl. the TEA key comes wholly from the Ability row either way).
		internal static Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true, ProtoEndpoint? sender = null)
		{
			return client.SendAsync(new Rpc(sender ?? LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddSpecialAbility,
				AddSpecialAbilityCodec.Build(ability, announce)));
		}

		// Pushes a ChatTextIIR (raw remoteformat XML) to the client's chat window. Addressed to the player's
		// GameInterface instance, on the world conversation.
		internal static Task SendWorldChat(INetworkClient client, string xml)
		{
			// As SendFeedback: the [u32 len] is the sender's (#530 rule 2), written by WorldIir.Send.
			return WorldIir.Send(client, ChatTextCodec.Build(client.Session.Character.Id, Encoding.Latin1.GetBytes(xml)));
		}

		// Sends a pre-framed world IIR body ([len]-prefixed) on the world conversation (opcode 0x2000).

		// The level-scaled rating QUAD (#685) - AttackRating / DefenseRating / MagicRating / MagicalResist, four
		// player-addressed StatIIRs carrying one number, in the wire order the corpus sends them.
		//
		// It rides the login snapshot (`SnapshotStatSheet`), and until now that was the ONLY place it ever went - so a
		// character who dinged read the level it logged in at until the next relog. Retail restates it, and the corpus
		// names two of the occasions outright:
		//
		//   - a LEVEL-UP. 15 of the 18 captured `NewLevelIIR` events are followed by a quad burst, 11 of them within
		//     0.1 s, i.e. inside the ding burst itself. That one is a correctness fix, not fidelity: the value is
		//     `400 x level - 300` and the level just changed.
		//   - the COMBAT TOGGLE (#505). 13 of 13 captured 0x4E2 presses draw a burst 0.31-1.61 s later, against a
		//     median burst interval of 10.4 s across the corpus - so it is causal, not adjacency. Mechanically it is
		//     what a sheathe/draw IS: the active weapon changes, so the ratings are restated.
		//
		// ⚠️ Those are two occasions, not THE rule: bursts arrive every ~10 s in a live capture and most of them
		// follow neither. [V0: what makes retail re-send the quad] stands - what is settled is the VALUE.
		internal static async Task SendCombatRatings(INetworkClient client)
		{
			var character = client.Session.Character;
			var rating = CombatRatingCurve.For(character.Level);
			foreach (var stat in CombatRatingCurve.Stats)
				await WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(character.Id, (uint)stat, rating)));
		}

		// The SEARCH stance (#505): retail's answer to PerformSpecialAction 0x064 is one player-addressed
		// BehaviorControlCenterIIR at slot BehaviorId.Search, and nothing else - no stat change, no buff. The client
		// waits for it before the stance takes, which is why a press with no answer reads as "the client sent
		// nothing".
		//
		// The animation action is the CHARACTER's, not the stance's, so it comes from a per-class map holding only the
		// two classes a capture shows. An unmapped class logs and does nothing, the same call the spellweave toggle
		// makes: a borrowed id would play some other class's animation and read as working.
		private async Task PerformSearchStance(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SearchStanceAction.TryGetValue(character.Class, out var animAction))
			{
				Logger.LogInformation("Search stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			// #272: a stance is something the OTHER players watch you do, so the same record is relayed to everyone who
			// can see the actor. `BehaviorControlCenterIIR` is 11/11 broadcast in the two-client capture
			// (`--report broadcast-check`), and a search crouch nobody else sees is a player frozen upright to them.
			var searchStance = BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(character.Id, animAction, entity?.NextBehaviorPhase() ?? 3));
			await WorldIir.Send(client, searchStance);
			await _broadcaster.BroadcastBehaviorToViewers(client, searchStance);
		}

		// The HIDE stance (#505) - a TOGGLE on one selector, so the server holds which way the next press goes.
		// Entering carries the shadow coverage the client itself reported a moment earlier (SetShadowCoverageIIR);
		// leaving is the shared stand-up action, the one part of the pair that is NOT class-specific.
		//
		// ⚠️ The EXIT record's shape is taken from the two necromancer sightings (flags 8, no echoed command id). The
		// one Herald of Xotli exit in the corpus carries flags 10 and a command id instead, on an otherwise identical
		// 8-byte-tailed record. Three sightings that disagree two-to-one is not enough to model the difference, so the
		// majority form is sent for both and the divergence is written down rather than silently averaged away.
		private async Task ToggleSneakHide(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SneakHideEnterAction.TryGetValue(character.Class, out var enterAction))
			{
				Logger.LogInformation("Hide stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			var hidden = entity?.IsHidden ?? false;
			var phase = entity?.NextBehaviorPhase() ?? 3;

			var record = hidden
				? BehaviorAnimations.SneakHideExit(character.Id, StartingData.SneakHideExitAction, phase)
				: BehaviorAnimations.SneakHideEnter(character.Id, enterAction, phase, entity?.NextBehaviorCommandId() ?? 3, entity?.ShadowCoverage ?? 100);

			// #272: relayed like the search stance - entering or leaving hiding is visible on the actor, and a rogue
			// who slips into the shadows only on their own screen is still standing there for everyone else.
			var stance = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, stance);
			await _broadcaster.BroadcastBehaviorToViewers(client, stance);
			if (entity != null)
				entity.IsHidden = !hidden;

			Logger.LogInformation("Hide stance: '{Name}' {Action}.", character.Name, hidden
				? "left hiding"
				: "entered hiding");
		}

		// REST (#505). Unlike search and hide this is not a stance at all: retail answers with a self-CAST of the
		// spell the client calls `Resting` - "Recovering health, mana, energy, and stamina" - on slot 4 MagicAttack,
		// with the spell row's own 20,500 ms cast time in the record. The player regains vitals over that channel,
		// which is what a 20-second cast bar with no spell at the end of it IS.
		//
		// The record is class-independent (the cast animation 177216 is shared with the Path of Asura recall) and is
		// byte-identical across four captured characters, so there is no per-class map here.
		//
		// ⚠️ What is emitted is the CAST. The regen RATE while resting is not modelled: no capture measures it, and
		// the tick's rates are corpus-verified numbers (RegenRates, #442) that a guessed rest multiplier would
		// quietly break. `IsResting` is set so the tick can be taught the real rate once one is measured, and it is
		// cleared when the channel elapses - the channel length is the spell row's, not invented.
		private async Task PerformRestCast(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);

			var record = BehaviorAnimations.RestCast(character.Id, entity?.NextBehaviorPhase() ?? 1);
			// #272: a 20-second rest cast is an animation on the actor - relayed, like every other player behaviour.
			var restCast = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, restCast);
			await _broadcaster.BroadcastBehaviorToViewers(client, restCast);

			// ⚠️ Rest is a CAST, so it has to arm the cast path's cancel state or it can never be cancelled (#505).
			// The client ends this channel the same way it ends any other: it sends the c2s cancel BCC (seq 0x0002)
			// when the player moves, and `HandleBccCastSpell` answers it with retail's 0x0007 abort - but ONLY if it
			// finds the BEGIN's args here. Rest is answered to a `PerformSpecialAction` rather than to a c2s cast, so
			// nothing ever set them, `beginArgs` came back null, and the cancel fell through to a plain echo instead
			// of the abort. The client then stayed channeling: the bar hung, and a channeling client sends no further
			// cast OR melee, so one press of rest silently disabled every combat action the player had.
			//
			// Verified against `NecroL80_..._cast-general-abilities` frames 867/921/927 - begin seq 0x0004 (phase 8,
			// activation 2050) -> c2s cancel seq 0x0002 -> s2c abort seq 0x0007, whose args are these begin args with
			// the cancel's own command id spliced in. So the record to send back is not a completion at all.
			client.Session.PendingCastArgs = record.Args;
			client.Session.PendingCastMode = 0; // no client command id at begin - the cancel brings its own

			if (entity == null)
				return;

			entity.RestingUntil = DateTimeOffset.UtcNow.AddMilliseconds(RestingChannelMs);
			Logger.LogInformation("Rest: '{Name}' began resting for {Ms} ms.", character.Name, RestingChannelMs);
		}

	}
}
