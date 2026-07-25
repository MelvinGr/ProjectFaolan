using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The backpack's slot count: base 24 + the equipped bag's Item.BagExtraSlots (equip slot 3 / Bag) - the same
		// sizing the snapshot uses (FullPlayerCharacterBuilder), so a grown bag can actually be filled. #12.
		private const uint BaseBackpackSlots = 24;

		Task<int?> IEconomyOutbound.GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count, bool allowHashless)
		{
			return GrantItemToBackpack(client, itemTemplateId, count, allowHashless: allowHashless);
		}

		Task IEconomyOutbound.SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback)
		{
			return SendAddItem(client, containerType, itemPos, entry, feedback);
		}

		// --- IEconomyOutbound: the wallet + item-grant ops the economy/inventory services reach back to (forwarding
		// to the private Outbound builders, which the listener also calls directly from loot/GM paths) ---------------
		Task IEconomyOutbound.SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			return SendUpdateCash(client, balanceTin, reason);
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

		private async Task<int> BackpackCapacity(uint characterId)
		{
			var equipment = await Database.GetCharacterEquipment(characterId);
			return (int)(BaseBackpackSlots + (equipment?.Bag?.BagExtraSlots ?? 0));
		}

		// The first free backpack cell, skipping persisted rows + the legacy starter-bag cell while it renders. The
		// backpack size is 24 base + the equipped bag's extra slots (#12), so a grown bag can be filled. Null = full.
		// Read-only DB helper (mirrors AddItemBuilder.FindFreeBagSlot - kept local to avoid a 4th seam).
		private async Task<int?> FindFreeBagSlot(uint characterId)
		{
			var equipment = await Database.GetCharacterEquipment(characterId);
			var rows = await Database.GetCharacterInventory(characterId);
			var taken = rows.Where(r => r.Container == FullPlayerCharacterBuilder.BagKind).Select(r => r.Slot).ToHashSet();
			if (equipment?.FirstPosBag is { Hash: not 0 })
				taken.Add(1);
			var capacity = (int)(BaseBackpackSlots + (equipment?.Bag?.BagExtraSlots ?? 0));
			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		// The first free cell (0..capacity-1) in `container`, skipping persisted rows.
		private async Task<int?> FindFreeContainerSlot(uint characterId, ContainerKind container, int capacity)
		{
			var rows = await Database.GetCharacterInventory(characterId);
			var taken = rows.Where(r => r.Container == container).Select(r => r.Slot).ToHashSet();
			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		// Grants an item to the player's backpack: persists it to a free CharacterInventory bag cell (so it
		// survives relog via the world-entry snapshot) AND pushes the live DispatchAddItem so it appears
		// immediately. The entry is the proven snapshot entry (SnapshotItemBuilder.BuildListEntry().Item - the
		// 3-stat form that renders in-client), and itemId/recordId use FullPlayerCharacterBuilder's deterministic
		// formula so the live add and the next login's snapshot agree on the entry's identity. Returns the bag
		// slot it landed in, or null when the item isn't snapshot-buildable or the bag is full.
		// `pushLiveAdd`: send the live DispatchAddItem so the item appears immediately. LOOT passes false: retail sends
		// NO inventory-add for normal loot - the client moves the looted item from the bag to the backpack LOCALLY
		// (confirmed by LootRemoveItems), so a server push would double it. Loot still PERSISTS here (DB write) so the
		// item survives relog via the world-entry snapshot.
		private async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, bool pushLiveAdd = true, bool allowHashless = false)
		{
			var item = await Database.GetItem(itemTemplateId);
			if (item == null)
				return null;

			var characterId = client.Session.Character.Id;

			// Quest items (Type 113, e.g. the Scavenger's Key) route to the QUEST container, not the backpack, so they
			// land in the player's quest-items tab (#261). They are kept even with hash 0 (quest items often have
			// none); normal bag items still require an hash so the snapshot can render them - EXCEPT when allowHashless
			// (claim redeems): a claim list carries the item's key but no hash, so the item delivers live (the 0x2020
			// path renders by template without a hash) but won't survive a relog until we harvest its hash. See #claims.
			var isQuestItem = item.Type == ItemType.Quest;
			if (!isQuestItem && item.Hash == 0 && !allowHashless)
				return null;

			var kind = isQuestItem
				? FullPlayerCharacterBuilder.QuestKind
				: FullPlayerCharacterBuilder.BagKind;
			var capacity = isQuestItem
				? 48 // quest tab holds 48 (NewContainer(QuestKind, 48))
				: await BackpackCapacity(characterId); // backpack = 24 base + the equipped bag's extra slots
			var slot = await FindFreeContainerSlot(characterId, kind, capacity);
			if (slot == null)
				return null;

			await Database.SetCharacterInventorySlot(characterId, kind, slot.Value, itemTemplateId, count);

			if (!pushLiveAdd)
				return slot;

			// Live DispatchAddItem so the item appears without a relog. Quest items go to the QUEST container (0x71), 			// matching retail's chain-capture delivery; everything else to the backpack (0x68).
			var containerType = isQuestItem
				? ContainerKind.Quest
				: ContainerKind.Bag;
			var entry = SnapshotItemBuilder.BuildListEntry((ulong)slot.Value, FullPlayerCharacterBuilder.ItemId(characterId, kind, (ulong)slot.Value),
					FullPlayerCharacterBuilder.RecordId(characterId, kind, (ulong)slot.Value), count, item, item.Hash)
				.Item;
			await SendAddItem(client, containerType, (uint)slot.Value, entry);
			return slot;
		}

		// Removes an item from the player's backpack: clears the persisted CharacterInventory bag cell (so it stays
		// gone across relog) AND pushes the live DispatchRemoveItem so it disappears immediately. The mirror of
		// GrantItemToBackpack. Returns true if a row was cleared.
		private async Task<bool> RemoveItemFromBackpack(INetworkClient client, int slot, uint amountToRemove = 0)
		{
			var characterId = client.Session.Character.Id;
			var rows = await Database.GetCharacterInventory(characterId);
			var present = false;
			foreach (var r in rows)
			{
				if (r.Container == FullPlayerCharacterBuilder.BagKind && r.Slot == slot)
					present = true;
			}

			if (!present)
				return false;

			await Database.SetCharacterInventorySlot(characterId, FullPlayerCharacterBuilder.BagKind, slot, null);
			await SendRemoveItem(client, ContainerKind.Bag, (uint)slot, amountToRemove);
			return true;
		}

		// Answers a TradeskillRequestIIR (recipe-book open) with the TradeskillReplyIIR (0x9062D559) recipe list built
		// from the Recipe catalog, so the Crafting -> Recipes window populates. The carrier is the standard world IIR
		// framing (GameAgent 0x58 -> GameInterface <instance>), body = [subop][0xC350][handle][reply], byte-exact vs
		// the capture (docs/subsystems/tradeskill-recipes.md). Empty catalog -> an empty (count=0x3F1) list, which opens the book
		// cleanly with no recipes.
		private async Task SendCraftingRecipeBook(INetworkClient client)
		{
			var handle = client.Session.Character.Id;
			var rows = await Database.GetRecipes();
			var records = rows.Select(TradeskillRecipeCodec.ToRecord).ToList();

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TradeskillRecipeCodec.BuildReplyIir(handle, records))));

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
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No map matching '{search?.Trim()}'.</span>");
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, matches.Select(m => $"{m.Id}: {m.Name}").JoinAsString("<br />"));
		}

		// Tells the client WHERE the character is bound (GameCharInterface DispatchUpdateBindLocation, 0x2002 - one
		// field, the playfield id; recovered method signature `nPlayfieldInstanceID`).
		//
		// Sent UNSOLICITED when the server changes the bind, not only in reply to RequestBindLocation. Retail's
		// "You are now bound here" banner is client-generated - it appears in no capture, and the text is a localized
		// string in the client's LDB (#346) - so the client has to be told the bind moved for it to render anything.
		// Without this the client's bind state stays stale until it next asks.
		private async Task SendUpdateBindLocation(INetworkClient client, uint bindMapId)
		{
			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchUpdateBindLocation,
				s => s.WriteUInt32(bindMapId)));

			Logger.LogInformation("Bind: told {Character} their bind playfield is {MapId}.", client.Session.Character?.Name, bindMapId);
		}

		// Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C
		// record stream (the retail money updates ride that conversation).
		private async Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchUpdateCash,
				b => UpdateCashBody.WriteUpdateCashBody(b, balanceTin, reason)));

			Logger.LogInformation("UpdateCash: {Balance} tin ({Reason}) -> {Character}.", balanceTin, reason, client.Session.Character?.Name);
		}

		// The 0x201c bank-slot stat-update body: [kind=105][charInstance][slot][statId][value] (all u32 BE). Locked
		// byte-exact to the capture by TradepostBankStatTests.
		internal static byte[] BuildBankSlotStatBody(uint charInstance, uint slot, uint statId, uint value)
		{
			return new FaolanStream().WriteUInt32(FullPlayerCharacterBuilder.BankKind).WriteUInt32(charInstance).WriteUInt32(slot).WriteUInt32(statId).WriteUInt32(value).ToArray();
		}

		// 0x4F57DC08 GameTime - the world clock. The resync timestamp is generated from `now` (wall-clock derived, see
		// GameTimeCodec); dayTime is still the captured constant until its unit is pinned in-client (#351). `now` is a
		// parameter so a golden test can feed the capturing instant and get the captured bytes back.
		internal static Rpc BuildGameTime(INetworkClient client, DateTimeOffset now)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(GameTimeCodec.Build(now)));
		}

		internal static Rpc BuildGameTime(INetworkClient client)
		{
			return BuildGameTime(client, DateTimeOffset.UtcNow);
		}

		// Frames the GuildUpdateIIR_c (GuildTagBody) on the world conversation: [RpcHeader][DispatchPassBlob]
		// [u32 len][GuildTagBody]. Sent on login for guilded characters and pushed live by the CsPlayerAgent
		// guild-accept flow through the dispatcher seam. The body grammar + round-trip live on GuildTagBody.
		internal static Rpc BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(x => new GuildTagBody(characterId, guildName).Write(x)));
		}

		// 0x5D85BFC7 MapChange = the PlayfieldFIIR: tells the client which playfield/instance the player is entering.
		// This is now a thin wrapper over the shared CreateEditorRecordBuilder.BuildPlayfieldFiir codec (the create-flow
		// builds the SAME packet) - it just prepends the MapChange subop and sources the PER-MAP rule fields from the Map
		// row (#308). The client keys per-area rules on this packet (mount permission = PlayfieldFlags); those fields are
		// NOT universal (beach vs mainland differ), so they come from the DB (Map.PlayfieldCount/FieldB/Flags/Instanced), 		// defaulting to the beach values for un-sourced maps. Position is the character's entry transform (per-session);
		// nearClip 15.0 + the farClip float are camera params (left constant). `map` may be null (fresh/unmapped -> beach
		// defaults).
		internal static Rpc BuildMapChange(INetworkClient client, Map map, uint playfieldToken)
		{
			var character = client.Session.Character;
			var charInstance = map?.PlayfieldInstanced ?? true
				? character.Id
				: 0u;
			var count = map?.PlayfieldCount ?? 2u;
			var fieldB = map?.PlayfieldFieldB ?? 3u;
			var flags = map?.PlayfieldFlags ?? 0u;

			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(MapChangeCodec.BuildIir(CreateEditorRecordBuilder.BuildPlayfieldFiir(character.PositionX, character.PositionY, character.PositionZ, character.MapId,
					charInstance, count, playfieldToken, 15.0f, BitConverter.UInt32BitsToSingle(0x40E3944Fu), flags, 0x01000000u, fieldB))));
		}

		// 0x96B8DC59 - entity vitals / stat-update packet. The wire builder now lives on World.VitalsPacket (shared
		// with the CombatService); this delegates so the existing SendStatUpdate/SendVitals/BuildVitals callers
		// (login replay, GM, the regen tick) are unchanged.
		internal static Rpc BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return VitalsPacket.BuildStatUpdate(client, stats, entityHandle);
		}

		// Builds the player's own vitals (0x96B8DC59) from the live character's MaxStats - the world-entry replay
		// emits this instead of replaying the captured player's vitals.
		internal static Rpc BuildVitals(INetworkClient client)
		{
			var character = client.Session.Character;
			var stats = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
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
				.Set(CharacterStatId.Level, character.Level)
				.Set(CharacterStatId.Exp, character.Experience)
				.Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));

			return BuildStatUpdate(client, block, client.Session.Character.Id);
		}

		// Assembles the complete world-entry snapshot packet for `client` from a pre-built root + tails. The record
		// body ([IirType=0x737A6DF9][TypeTag][Handle=instance][prefix][root][tails]) is built by
		// FullCharSnapshotWriter, which owns the prefix model next to the reader that parses it - it used to be
		// stated here as well, and the two drifted (see that type's header). This adds only the client-dependent
		// outer wrap: the RpcHeader + outer length/CRC. No captured packet is consulted.
		internal static Rpc BuildWorldEntrySnapshot(INetworkClient client, byte[] rootBytes, byte[] tailsBytes)
		{
			var body = FullCharSnapshotCodec.BuildBody(client.Session.Character.Id, rootBytes, tailsBytes);
			return FrameWorldEntryRecord(client, body);
		}

		// Assembles the complete world-entry player/NPC FA015807 spawn packet for `client` from a pre-built spawn body.
		// `spawnBody` is the `packet[bodyStart..]` form ([u32 bodyLen][IirType=0xFA015807][TypeTag][Handle][FieldA]
		// [transform + CharacterIIR + scaffold]) that the SpawnPacketBuilder override engine produces; this wraps it in
		// the shared world-entry header so the spawn no longer needs the captured packet's header (the burst-less path).
		internal static Rpc BuildWorldEntrySpawn(INetworkClient client, byte[] spawnBody)
		{
			ArgumentNullException.ThrowIfNull(spawnBody);
			return FrameWorldEntryRecord(client, spawnBody);
		}

		// Prepends the shared world-entry RpcHeader + outer length/CRC (the WriteHeader/WriteLengthHash path) to a
		// record body that ALREADY carries its own [u32 bodyLen] prefix - i.e. the `packet[bodyStart..]` form that
		// SpawnPacketBuilder and WriteArrayUInt32Length produce. This is the keystone that lets a
		// from-scratch record body reach the wire without a captured packet header.
		internal static Rpc FrameWorldEntryRecord(INetworkClient client, params ReadOnlySpan<byte> bodyWithLenPrefix)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, bodyWithLenPrefix.ToArray());
		}

		// Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		// updates (0x2005) ride.
		private static async Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchAddItem,
				b => AddItemBody.WriteAddItemBody(b, containerType, charId, itemPos, AddItemBody.SerializeInventoryEntry(entry), feedback)));
		}

		// #348: the top-of-screen FIFO banner naming the SUB-AREA the player just walked into. Retail fires one on
		// every sub-area crossing, not once per playfield - Old Tarantia alone has six (Bridge of Vilerus I, The East
		// Gate, The West Gate, ...) - so the world tick drives this off MapArea, and the entry banner is simply the
		// first crossing. `areaNameId` is the client's localized-string id; the client renders the text itself.
		//
		// This used to take a mapId and read the single `Map.AreaNameId`, which could only ever name one area per
		// playfield (it had picked "The West Gate" for Old Tarantia, an arbitrary one of the six).
		private static Task SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return SendFeedback(client, Encoding.Latin1.GetString(FormatFeedbackCodec.BuildAreaNameXml(areaNameId)));
		}

		// Pushes one bank-slot stat update (0x201c on container 105): [kind=105][charInstance][slot][statId][value], 		// byte-exact to the capture (F376/F377/F364/F398). Rides the same GameCharAgent conversation as the bank
		// slot registrations (BankHandler), so the client applies it to the live bank cell.
		private static Task SendBankSlotStat(INetworkClient client, uint slot, uint statId, uint value)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchSetItemStat,
				BuildBankSlotStatBody(client.Session.Character.Id, slot, statId, value)));
		}

		// Pushes a FormatFeedback message (raw remoteformat XML) to the client's on-screen feedback. typeFlags =
		// FifoBannerTypeFlags for the top-of-screen FIFO banner. Addressed to the player's GameInterface instance.
		private static async Task SendFeedback(INetworkClient client, string xml, uint typeFlags = FormatFeedbackCodec.FifoBannerTypeFlags, uint footer = 1)
		{
			var instance = client.Session.Character.Id;
			var body = FormatFeedbackCodec.Build(instance, typeFlags, footer, Encoding.Latin1.GetBytes(xml));

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, body));
		}

		// Pushes a live item removal to the client (the inverse of SendAddItem).
		private static async Task SendRemoveItem(INetworkClient client, ContainerKind containerType, uint itemPos, uint amountToRemove = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchRemoveItem,
				b => RemoveItemBody.WriteRemoveItemBody(b, containerType, charId, itemPos, amountToRemove)));
		}

		// #372: the "resurrection point discovered" FIFO banner. Retail sends it in the SAME frame as the
		// resurrection-spots re-send that adds the point to the player's list, and it is what the player actually
		// sees; only the point id goes on the wire (the client resolves the name from LDB category 270).
		private static Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return SendFeedback(client, Encoding.Latin1.GetString(FormatFeedbackCodec.BuildRespawnPointDiscoveredXml(pointId, nameKey)));
		}

		// Sends the proven captured system line - the test bootstrap for the ChatTextIIR path until a literal-text
		// remoteformat template lands.

		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(BuildStatUpdate(client, stats, entityHandle));
		}

		// Pushes the death/release screen: the player's death position + the resurrection spots they can pick.
		private static Task SendValidResurrectionSpots(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x59), client.Endpoint(Endpoints.GameCharInterfaceType),
				GameCharInterfaceOpcodes.DispatchValidResurrectionSpots, b => ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(b, deathPosition, spots)));
		}

		// Pushes the character's own vitals from its persisted MaxStats (or class/level defaults if a legacy
		// character has none). Current==max until per-vital current values are tracked.
		private static Task SendVitals(INetworkClient client)
		{
			var character = client.Session.Character;
			var stats = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);

			return SendVitals(client, stats.Health, stats.Stamina, stats.Mana);
		}

		// Convenience: push explicit vitals (Health/Stamina/Mana). Values are player-facing; the
		// CharacterStatBlock applies the ×100 vitals scale.
		private static Task SendVitals(INetworkClient client, uint health, uint stamina, uint mana)
		{
			// Emit the values under the CURRENT ids (0x001B/0x01F8/0x01FA) - matches the retail full-spawn announce, 			// which sends current (== max at full health) and lets the client show full bars; max rides the snapshot.
			var stats = new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, health).Set(CharacterStatId.StaminaCurrent, stamina).Set(CharacterStatId.ManaCurrent, mana);

			return SendStatUpdate(client, stats, client.Session.Character.Id);
		}

		// Pushes a ChatTextIIR (raw remoteformat XML) to the client's chat window. Addressed to the player's
		// GameInterface instance, on the world conversation.
		private static async Task SendWorldChat(INetworkClient client, string xml)
		{
			var instance = client.Session.Character.Id;
			var body = ChatTextCodec.Build(instance, Encoding.Latin1.GetBytes(xml));

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, body));
		}

		// Sends a pre-framed world IIR body ([len]-prefixed) on the world conversation (opcode 0x2000).
		private static Task SendWorldIir(INetworkClient client, byte[] body)
		{
			return client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, body));
		}
	}
}
