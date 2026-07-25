using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Economy;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private const uint MountAllowedFlag = 0x1; // Map.PlayfieldFlags bit 0: the playfield permits mounts

		// Suppress the auto-opening item-shop browser (itemshopbrowser_swf) on world entry. The client runs level/login
		// milestone Lua from its OWN RDB LuaArchive that calls player:OpenSiteInBrowser("http://aocshop.funcom.com/
		// showitem?...") - a Funcom-store upsell - which it renders in the itemshopbrowser SWF. Each open is gated on
		// `not player:HasTag(X)` (a "this bundle was already shown" flag) plus the PvPSagaBundles game tweak. No tweak
		// value skips them all (scripts 396 and 2240 open a bundle whether the tweak reads <1 OR >0), so the only
		// universal suppressor is the tag gate - and the client learns a character's tags from the FullChar snapshot we
		// send at entry. Pre-seeding the "already shown" flag for every shop-open branch makes HasTag(X) true, so the
		// browser never opens. The gating tag ids were recovered from the RDB shop scripts (393/395/396/2239/2240/2287-
		// 2298): 6617-6620 are the i<1 bundles, 7316 + 7416-7421 the i>0 bundles. Purely cosmetic "seen" flags - safe to
		// force on a private server that has no Funcom store.
		// TODO: Confirm
		private static readonly uint[] ItemShopSuppressTags = []; //6617, 6618, 6619, 6620, 7316, 7416, 7417, 7418, 7419, 7420, 7421];

		private async Task FlushPendingPost205FFollowUp(INetworkClient client)
		{
			if (!client.Session.HasPendingPost205FFollowUp)
				return;

			if (client.Session.IsUnnamedCreatePhase)
			{
				Logger.LogInformation("New-character post-0x205F follow-up remains pending on 0x2020; waiting for the retail create handoff instead of flushing early.");
				return;
			}

			// The CREATE-WORLD connection gets the same burst, but NOT from here. Retail answers its CharInPlayIIR with
			// 0x2024 + the ability grants (Blablatest create fr317 -> fr324), and that is the only moment it may fire:
			// this method also runs off the 0x2020 poll, which during the intro means ReadyForPlayScreen landing inside
			// the cutscene - the blue screen. So the create-world path calls SendWorldReadyBurst directly from
			// CharInPlayIIR, and this armed-flush path stays out of it.
			if (client.Session.IsCreateWorldSession)
			{
				Logger.LogInformation("Create-world connection: the ready burst belongs to its CharInPlayIIR, not to this poll.");
				return;
			}

			// Past the early return the character is always named: the create-editor connection returned above (it
			// flushes via the retail create handoff, not here), so every connection that reaches this point is a
			// Login or a TeleportReentry - both of which want the full existing-character burst below. This is the
			// cleanup the old "Remove ...Name != null checks below" TODO asked for, now that the early return is in
			// place: the create-vs-named conditionals that used to guard this body were all unreachable dead code.
			Logger.LogInformation("Flushing pending post-0x205F follow-up on this 0x2020.");
			await SendWorldReadyBurst(client);
		}

		// The create-world answer to CharInPlayIIR - retail's, record for record (Blablatest create fr324/325):
		//   0x2024 ReadyForPlayScreen | the ability grants (0x2052) | CharInPlayIIR echo | RegenIIR | SyncCanSprint
		// and then the intro cutscene, which the caller starts.
		//
		// Deliberately NOT the login burst (SendWorldReadyBurst). Reusing that sent this connection things retail does
		// not send here - the combo book (12 ComboBookIIR), the deferred stat prompt, a quest marker and the post-0x2024
		// login record stream - while missing RegenIIR. "It is a world entry, so send the world-entry burst" is what
		// keeps re-breaking this flow: the create world entry is its OWN sequence, and the captures say so.
		private async Task SendCreateWorldReadyBurst(INetworkClient client)
		{
			var instance = client.Session.Character.Id;

			await SendReadyForPlayScreen(client);
			await GrantCharacterAbilities(client);

			// The CharInPlayIIR echo (retail fr324: [u32 len][864cfef8][C350][instance][00]).
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArray(0x00, 0x00, 0x00, 0x0D, 0x86, 0x4C, 0xFE, 0xF8, 0x00, 0x00, 0xC3, 0x50).WriteUInt32(instance).WriteArray(0x00)));

			// RegenIIR (retail fr325 carries 0 / 0x8F2 / 0 - the middle pool is the only seeded one).
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(x => x.WriteUInt32(GameServer0X2000Opcodes.RegenIIR).WriteArray(CreateEditorRecordBuilder.BuildRegenIir(instance, 0, 0x8F2, 0)))));

			// SyncCanSprint (0x200C, one byte).
			await client.SendAsync(new Rpc(ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(Endpoints.GameCharInterfaceProjectType, instance),
				GameCharInterfaceProjectOpcodes.DispatchSyncCanSprint, b => b.WriteByte(0x01)));
		}

		// Everything the client needs to LEAVE the loading screen and stand in the world: the deferred stat prompt, the
		// quest marker, 0x2024 ReadyForPlayScreen, the ability + combo grants and the post-0x2024 record stream.
		//
		// Two callers, because two flows reach this moment differently: a login/teleport arms it on the client's 0x205F
		// and flushes on the next 0x2020 poll, while a create-world connection sends it on CharInPlayIIR - after the
		// intro cutscene, never during it.
		private async Task SendWorldReadyBurst(INetworkClient client)
		{
			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000Opcodes.StatIIR)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(client.Session.Character.Id)
					.WriteByte(0x01)
					.WriteUInt32(0x00000013)
					.WriteUInt32(0x00000000))));
			Logger.LogInformation("Sent deferred (uint)GameServer0X2000Opcodes.StatIIR prompt.");

			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id).WriteByte(0)));

			// Retail sends 0x2024 ReadyForPlayScreen right after the spawn handshake (0x2076) - the "leave the
			// loading screen and enter the world" signal. We were sending the manifest but never this, so the client
			// finished the handshake (0x2020 -> 0x2008 -> 0x2076) and then sat on the loading screen forever.
			// Confirmed by diffing a 4010 login against the Strie dump: 0x2024 ours=0 / dump=1.
			Logger.LogInformation("Existing-character login: sending 0x2024 ReadyForPlayScreen after the spawn handshake.");
			await SendReadyForPlayScreen(client);
			// Retail grants the starting abilities (0x2052) + combo book right after 0x2024, not during world entry
			// (TestAccount necro login capture: abilities at frame 192, just after ReadyForPlayScreen). Both are granted
			// here, post-snapshot: neither rides the FullChar snapshot known-list (spells do), so a pre-snapshot grant
			// is discarded by the client's identity reconstruction.
			await GrantCharacterAbilities(client);
			await GrantCharacterCombos(client);
			// NB the buff re-asserts that used to live here - the persisted mount (#342), the persisted self-buffs (#274)
			// and Shield of the Risen (#221) - have ALL moved into the shared world-entry flush, where retail sends them
			// (see RestoreEntryBuffs). Sent from here they arrived after the client had left the loading screen, which
			// reads as a NEW application and fires the client's apply feedback on every login and every map change.
			// NB the learned SPELLS (DispatchLearnSpell 0x200A) are NOT re-granted here either: they ride the FullChar
			// snapshot's known-spell list (#74/#71) so the client already knows them when it enters the world, and
			// the pre-snapshot GrantCharacterSpells re-send is absorbed silently. (The old PendingSpellLearns
			// post-ready re-announce - needed only while the snapshot carried an empty known-list - re-toasted "You
			// have learned a new spell" on every login; the known-list makes it redundant.)

			// #348: the area banner is now driven by the world tick's sub-area crossing check (WorldSimulation
			// .TickAreaBanners) - entering a playfield lands the player inside an area and that IS the first
			// crossing, so it banners without a special case here. Clearing the live area makes sure the crossing
			// fires again after a teleport/changemap back into an area the player was already standing in.
			if (_simulation.PlayerWorldEntity(client) is { } areaEntity)
				areaEntity.CurrentAreaNameId = 0;

			// The post-0x2024 login record stream (world state / quest / UI) retail sends right after ReadyForPlayScreen
			// + abilities - the records that let the client leave the loading screen. Rebuilt from the Strie 4010 login
			// capture as explicit PacketStream sends: the live player instance is written inline (the receiver is built via
			// Endpoints.EndpointBytes(type, instance), WriteUInt32(instance) where the body carries it), so no ids are
			// hard-coded. The remaining body
			// bytes are opaque captured payloads (quest/UI blobs). Per-map/DB-driven construction (like the rest of world
			// content) is #352 - blocked on decoding these blobs.
			// #304: the legacy verbatim beach NPC records (spawns/MoveTasks/despawns/BehaviorCC on captured handles) were
			// deleted - WorldBroadcaster interest streaming owns the beach population, confirmed in-client.

			var instance = client.Session.Character.Id;

			// "New claims available" glow (CoreClaimIIR sub 0x0009) - pushed ONLY when the account has unseen claims,
			// matching retail (present on a fresh login with new claims, absent once viewed). Previously this fired
			// unconditionally, so the claims button lit up for every account; see GameServerListener.Claims.cs.
			await ClaimService.SendNewClaimsGlowIfAny(client);

			// record 0: 0x00002000, 17-byte body
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArray(0x00, 0x00, 0x00, 0x0D, 0x86, 0x4C, 0xFE, 0xF8, 0x00, 0x00, 0xC3, 0x50).WriteUInt32(instance).WriteArray(0x00)));

			// record 1: 0x0000200C, 1-byte body
			await client.SendAsync(new Rpc(ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(Endpoints.GameCharInterfaceProjectType, instance),
				GameInterfaceOpcodes.DispatchReportServerID, (byte[])[0x01]));

			// record 2: 0x00002000 - the StartFixed3d destiny step (ScryScriptIIR 0xA36D3B74), constructed (was verbatim;
			// byte-identical, proven by QuestTests.BuildScryStep_MatchesCapture). The body is [u32 len][step bytes].
			// #234: SKIP on the create-world connection - the create destiny cutscene overlay (SendCreateCutsceneStep)
			// already sends StartFixed3d at IntroDone (matching retail, which sends it ONCE there). Sending it here too
			// (before the cutscene) is a DUPLICATE retail doesn't have (loopback f649) and re-engages the cinematic.
			if (!client.Session.IsCreateWorldSession)
			{
				var startFixed3d = ScryScript.BuildStep(instance, "StartFixed3d");
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance), GameInterfaceOpcodes.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(startFixed3d)));
			}

			if (Global.EnableTutorialOverlay)
			{
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance), GameInterfaceOpcodes.DispatchPassBlob,
					b => b
						.WriteArray(0x00, 0x00, 0x00, 0x1F, 0xA3, 0x6D, 0x3B, 0x74, 0x00, 0x00, 0xC3, 0x50)
						.WriteUInt32(instance)
						.WriteArray(0x00, 0x00, 0x00, 0x00, 0x0E, 0x08, 0x05, 0x10, 0x00, 0x18, 0xF6, 0xC6, 0x17, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00)));

				var welcomeTutorial = ScryScript.BuildStep(instance, "WelcomeTutorial");
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance), GameInterfaceOpcodes.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(welcomeTutorial)));
			}

			// #262: apply any after-quest world-object effects up-front (e.g. a relog after freeing Casilda drops the
			// beach chain-wall collision, so the returning player isn't re-blocked by the world-entry burst's chain
			// dynel), and clear the escortee's captive pose if she's already freed. Both data-driven / no-op when N/A.
			await _broadcaster.ApplyQuestWorldEffects(client);
			await _questHandler.ClearCasildaCaptivePose(client); // TODO: This should be gated more / moved to 'quest handler service'

			// Bind the character on entry if it has never bound. Every recall targets BindRespawnPointId - the Totem of
			// Origin, the Path of Asura spell, .recall, and the death-resurrect - so an unbound character (imported or
			// older chars, whose bind was never set) can't recall at all. Bind ONCE to the entry map's default respawn
			// point + persist; an established bind is left untouched (it only moves via a bindstone/Wayfarer, not login).
			Logger.LogInformation("Sent the post-0x2024 login records.");

			// #239/#304 RESOLVED 2026-07-05 - the class force-feed (SetStat 0x43 = Class) was REMOVED as proven
			// redundant, matching retail (which never sends stat 0x43 either). The in-world class is derived
			// CLIENT-SIDE from the char data we already deliver. Verified in-client
			// with the force-feed DISABLED on BOTH paths: an existing lvl80 login read stat 0x43 = Necromancer,
			// and a freshly-created char read stat 0x43 = HeraldOfXotli - both displayed correctly. The earlier
			// interim predated the client-derivation working; no server-side class stat is needed.

			// Re-deliver any persisted bag items the world-entry snapshot dropped for lacking an hash
			// (AH-bought items - harvested template, no hash) via the live add path, so they survive relog.
			await RedeliverNonSnapshotBagItems(client); // TODO: Is this still needed??

			// (The post-load second-snapshot delivery was removed 2026-06-12: the client ignores/rejects a
			// second 0x737A6DF9 for an already-constructed identity. Inventory rides the WORLD-ENTRY snapshot,
			// see BuildBurstlessSnapshotRoot.)

			// Wallet sync: retail does NOT push a 0x2005 at login (the initial balance presumably rides the
			// FullChar snapshot, location undecoded) - this is our interim so a moneyed character doesn't
			// show 0 until the first change. Reason 0 = a plain sync, not a gain celebration.
			if (client.Session.Character.Money > 0) // TODO: Confirm previous comment, remove this call if not retail
				await SendUpdateCash(client, client.Session.Character.Money, UpdateCashReason.Spend);

			// Guilded character: set the in-world guild tag (0x2550C99A). Retail sends this once on guild
			// JOIN; for logins the tag normally rides in the spawn data, which our DB spawn does not carry
			// yet, so re-assert it here - the client applies it the same way. (After the snapshot, like the
			// wallet sync - keep the burst -> snapshot gap clean.)
			if (client.Session.Character.GuildId is { } guildId)
			{
				var guild = await Database.GetGuild(guildId);
				if (guild != null)
					await client.SendAsync(BuildGuildTag(client, client.Session.Character.Id, guild.Name));
			}

			client.Session.HasPendingPost205FFollowUp = false;
			Logger.LogInformation("Cleared pending post-0x205F follow-up flag.");
		}

		// The abilities to grant a character on login: its authoritative per-character set (CharacterAbility, imported
		// from the retail 0x2052 grants) resolved to Ability rows, or the class-starting kit for a freshly-created
		// character with no persisted set. NOT derived from the bar (the bar is only shortcuts).
		// Grants the character's starting abilities so the player has castable attacks in-world (enables the combat
		// test, TODO #3). The set is the universal starting kit every captured character receives via 0x2052 on
		// login - the `Abilities` table, imported from the retail grants by CaptureAbilityImporter (3 Normal Attack
		// directions + toggle-combat / switch-weapon / utility). Sent on the spawn conversation (same family as the
		// 0x201c handshake), after the world has been built. (Class-specific SPELLS are a separate, not-yet-decoded
		// grant - no capture exercises it; see TODO #3.)
		private async Task GrantCharacterAbilities(INetworkClient client)
		{
			var character = client.Session.Character;

			var abilities = await CharacterLoadout.ResolveAbilities(Database, Logger, character);
			// Login is a SILENT re-grant: the abilities were already learned (in the create flow / on first learn), so
			// the announce flag is cleared - otherwise the client pops "You have learned a new ability" for the whole
			// kit on every login. Retail does exactly this: the create-flow grants carry the announce byte
			// 0x01, the per-login re-grants carry 0x00 (the ONLY differing byte; see BuildAddSpecialAbilityBody).
			foreach (var ability in abilities)
				await SendAddSpecialAbility(client, ability, false);

			Logger.LogInformation("GrantCharacterAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class,
				character.Level);
		}

		// Re-emits the character's COMBO book on login as ComboBookIIR grants - the combo counterpart to
		// GrantCharacterAbilities. The book = persisted CharacterCombo rows UNION the class's combos up to its level
		// from the class<->combo ACCESS map (ClassCombo); a MAX-LEVEL imported character never dings, so without this
		// its combo book would be empty. Not in the snapshot.
		private async Task GrantCharacterCombos(INetworkClient client)
		{
			var character = client.Session.Character;

			var instance = character.Id;

			// The combo book = the persisted CharacterCombo rows UNION the class's combos up to its level, from the
			// class<->combo ACCESS map (ClassCombo). This replaces the old class-INDEPENDENT level-keyed catalog, which gave
			// every class the same level-keyed set - so a Necromancer (a caster with no weapon combos) re-"learned"
			// Sweep/Slam/Hell Strikes on every login. ClassCombo has NO rows for casters, and ships empty until
			// populated from dumps, so today this grants only the persisted CharacterCombo rows (the safe default).
			var comboIds = new HashSet<uint>();
			foreach (var combo in await Database.GetCharacterCombos(character.Id))
				comboIds.Add(combo.ComboId);
			foreach (var combo in await Database.GetClassCombos(character.Level, character.Class))
				comboIds.Add(combo.Id);

			foreach (var comboId in comboIds)
			{
				await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, ComboBookIirCodec.Build(instance, comboId)));
			}

			if (comboIds.Count > 0)
				Logger.LogInformation("GrantCharacterCombos: emitted {Count} combos to '{Name}' (class={Class}, level {Level}).", comboIds.Count, character.Name, character.Class, character.Level);
		}

		// Re-grants the character's learned spells on login - the spell counterpart to GrantCharacterAbilities (#74).
		// Retail re-sends a caster's whole learned set via DispatchLearnSpell on every world entry (invariant across
		// every login capture: NecroL80 always relearns her 5, DemoL80 his 9); the client silently no-ops spells it
		// already knows, so this is safe to replay each login. The set is the character's CharacterSpells rows (seeded
		// for captured characters by CaptureSpellImporter, grown live by .learnspell). Sent after GrantCharacterAbilities.
		private async Task GrantCharacterSpells(INetworkClient client)
		{
			var character = client.Session.Character;

			var spells = await Database.GetCharacterSpells(character.Id);
			foreach (var spell in spells)
				await SendLearnSpell(client, spell.SpellId);

			if (spells.Length > 0)
				Logger.LogInformation("GrantCharacterSpells: re-granted {Count} learned spells to '{Name}' (class={Class}).", spells.Length, character.Name, character.Class);
		}

		// The client's world-entry spawn-poll, arriving on wire 0x2020 (GameCharAgent method slot 0x20, RPC-catalog
		// label UpdateQuestMarkerNPC - the login/world flow reuses the opcode). Fires once per spawned entity the
		// client confirms it built (requestId = that entity's handle). Three jobs, all part of finishing world entry:
		//  (1) record the confirmed handle so interest only ever despawns handles the client actually built (#245 -
		//      a despawn for an unbuilt handle crashes a still-loading client: pcDynel == null);
		//  (2) answer with the 0x2008 SpawnCheck that completes/renders that entity (verified: the dikketest world
		//      conn polls Kalanthes 0x535A0 and gets a SpawnCheck for 0x535A0). Both existing-login and post-creation
		//      world entry need it - the create flow's old hand-built batch answered the wrong hardcoded capture
		//      handles, so its entities were never confirmed and the intro cinematic failed;
		//  (3) drive the ready-trigger: on a normal login retail arms the follow-up off the client's 0x205F and
		//      expects 0x2024 ReadyForPlayScreen right after this spawn handshake, so the flush lives HERE
		//      (capture-proven - moving it strands login on the loading screen). FlushPendingPost205FFollowUp no-ops
		//      unless armed, so a create-editor / post-ready poll just falls through.
		// Bodies shorter than the 16-byte poll are not spawn-polls (some other same-opcode traffic); just log them.
		private async Task HandleWorldEntrySpawnPoll(INetworkClient client, Rpc packet, FaolanStream body)
		{
			if (body.Length < sizeof(uint) * 4)
			{
				LogPacketBodyDebug("GameServer 0x2020 short body", body.ToArray());
				return;
			}

			var ack0 = body.ReadUInt32();
			var requestId = body.ReadUInt32();
			var ack1 = body.ReadUInt32();
			var characterId = body.ReadUInt32();

			_simulation.PlayerWorldEntity(client)?.Confirmed.Add(requestId);

			if (client.Session.WorldEntered || client.Session.IsCreateWorldSession)
				await SendSpawnCheckResponse(client, requestId);

			Logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}", ack0, requestId, ack1, characterId);
			Logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}", ack0, requestId, ack1,
				characterId, client.Session.HasPendingPost205FFollowUp);
			await FlushPendingPost205FFollowUp(client);
		}

		// Re-delivers, via the proven live DispatchAddItem (0x2020) path, the persisted BAG items the world-entry
		// snapshot DROPPED because they carry no hash (Hash == 0 - e.g. an AH-bought item whose template
		// we harvested from a listing, which has no hash). The snapshot only renders Hash != 0 rows
		// (FullPlayerCharacterBuilder), and embedding a half-identified item in the chat-critical 0x737A6DF9 root is
		// risky, so these ride the mid-session add-item mechanism (which renders by template / Hash 0) once at login.
		// Items WITH an hash already rode the snapshot, so this only touches the dropped ones (no duplication).
		private async Task RedeliverNonSnapshotBagItems(INetworkClient client)
		{
			var character = client.Session.Character;

			var rows = await Database.GetCharacterInventory(character.Id);
			var pushed = 0;
			foreach (var row in rows.Where(r => r is { Container: FullPlayerCharacterBuilder.BagKind, ItemId: not null, Item.Hash: 0 }))
			{
				var slot = (ulong)row.Slot;
				var entry = SnapshotItemBuilder.BuildInventoryEntry(FullPlayerCharacterBuilder.ItemId(character.Id, FullPlayerCharacterBuilder.BagKind, slot), row.Item!, 0,
				[
					new SnapshotItemBuilder.ItemStat(0, 1), new SnapshotItemBuilder.ItemStat(23, (uint)FullPlayerCharacterBuilder.RecordId(character.Id, FullPlayerCharacterBuilder.BagKind, slot)),
					new SnapshotItemBuilder.ItemStat(26, row.Count)
				], row.Item!.Level);
				await SendAddItem(client, ContainerKind.Bag, (uint)row.Slot, entry);
				pushed++;
			}

			if (pushed > 0)
				Logger.LogInformation("Re-delivered {Count} hash-less bag item(s) to '{Name}' via live add (not in the snapshot).", pushed, character.Name);
		}

		// Resolves the resurrection anchor advertised on the login 0x2024: the closest known / AlwaysKnown RespawnPoint on
		// the character's map (mirrors SendDeathScreen's resolution). Falls back to the universal Tortage Beach default
		// (Map.NewPlayerSpawnSpawnPoint) when the map defines no respawn point.
		private async Task<RespawnPoint> ResolveLoginResurrectionSpot(Character character)
		{
			var points = await Database.GetRespawnPoints(character.MapId);
			if (points.Length == 0)
				return Map.NewPlayerSpawnSpawnPoint;

			var discovered = new HashSet<uint>(await Database.GetDiscoveredRespawnPointIds(character.Id));
			return RespawnResolver.ClosestKnown(character.Position, points, discovered) ?? points.FirstOrDefault(p => p.AlwaysKnown) ?? points[0];
		}

		// Re-seat the character's persisted mount (Character.MountId) after world entry, when the map allows mounts.
		// Sends the ride buff + Mount/MountSpeed StatUpd and marks the live entity mounted - the same seat the
		// SummonMount channel ends with, minus the cast animation. No-op on foot, on a no-mount map (the client
		// auto-dismounted on entry), or if the stored id no longer resolves.
		// Every buff the player should already be wearing when they arrive: the persisted mount, the persisted
		// self-buffs, and the login shield. All of it goes out HERE, immediately after the snapshot + GameTime that end
		// the entry flush, because that is where retail sends it. In the changemap capture
		// NecroL80_..._login_leave_king_conan_castle-Dismount, riding out of the no-mount castle produces
		//   FullChar -> GameTime -> ride buff -> Shield of the Risen -> Mount/MountSpeed StatUpd
		// as five ADJACENT records. We used to send all three from the post-0x205F flush instead, long after the client
		// had left the loading screen - and #342 proved what that costs: a re-assert arriving inside the entry stream is
		// absorbed silently, while the same record after ready reads as a NEW application and fires the client's apply
		// feedback (the mount's was audible; confirmed gone once it moved here). The same reasoning already moved
		// GrantCharacterSpells into this path to stop it re-toasting "You have learned a new spell" on every login.
		//
		// One deliberate deviation: retail's sample puts the shield BETWEEN the ride buff and the mount StatUpd, while
		// RestoreMount emits its buff and its stats together. Splitting the mount restore so an unrelated buff can
		// interleave would couple two subsystems for a single capture's ordering of independent IIRs, so the records go
		// out mount-then-shield instead. If that interleaving ever turns out to matter, this is the place to fix it.
		private async Task RestoreEntryBuffs(INetworkClient client)
		{
			await RestoreMount(client);
			await RestoreBuffs(client);
			await SendShieldOfTheRisen(client);
		}

		// Re-seats a persisted mount on world entry (login and every teleport/changemap re-entry), only where the map
		// allows mounts - so riding into a no-mount area dismounts and riding back re-mounts.
		//
		// This is a FRESH buff apply, and that is retail-correct rather than a shortcut (#342). The changemap capture
		// NecroL80_..._login_leave_king_conan_castle-Dismount settles it: riding out of the castle, retail's entry-restore
		// ride buff is byte-identical to a player-initiated mount cast apart from the per-application effect id and the
		// trailing nonce - 6 bytes of 84. There is no "already active" or "silent" variant of the record, and the
		// FullChar snapshot carries no active-buff region for one to live in, so reconstructing the mount silently
		// inside the snapshot (as #342 originally proposed) would be an invention, not a mirror of retail.
		// What retail does differ on is WHERE the record sits - see the call site.
		private async Task RestoreMount(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character?.MountId is not { } mountId)
				return;

			var map = await Database.GetMap(character.MapId);
			if (((map?.PlayfieldFlags ?? 0) & MountAllowedFlag) == 0)
				return;

			var mount = MountCatalog.ForId(mountId);
			if (mount == null)
				return;

			var self = character.Id;
			await SendMagicBuffIir(client, self, self, MountCatalog.RideSpell, MountCatalog.RideBuffPayload());
			await SendStatUpdate(client, new CharacterStatBlock
			{
				[CharacterStatId.Mount] = mount.RideStat,
				[CharacterStatId.MountSpeed] = 100
			}, self);

			var entity = PlayerWorldEntity(client);
			entity?.IsMounted = true;
			entity?.MountRideStat = mount.RideStat;

			Logger.LogInformation("Restored mount {Mount} for {Name} on map {Map}.", mount.CreatureName, character.Name, character.MapId);
		}

		// Builds + sends the entire world entry from generated/structured pieces - no captured burst (#210). Mirrors
		// SendWorldEntry's world-model setup + record order (MapChange, objects, player spawn, snapshot, vitals, 		// gametime), then runs interest for the NPCs. Beach-shaped (FullCharSnapshotSubClass + BeachPlayfieldId); a non-beach
		// map would get beach tails, so the flag is for a beach login until per-map sourcing lands.
		private async Task SendBurstlessWorldEntry(INetworkClient client, uint mapId)
		{
			var character = client.Session.Character;
			var equipment = await Database.GetCharacterEquipment(character.Id)
			                ?? throw new InvalidOperationException($"Character {character.Id:X8} ({character.Name}) has no CharacterEquipment row - cannot enter the world.");

			// The beach starter quest (10238) is granted ONCE at character creation (CommitCharacterName), not re-seeded
			// here - so a completed/removed quest never reappears. The journal is now driven purely from CharacterQuests.
			var inventoryRows = await Database.GetCharacterInventory(character.Id);
			// Union in the item-shop-suppression flags (see ItemShopSuppressTags) so the client's shop-open Lua sees
			// HasTag(X)==true and never auto-opens the itemshopbrowser on entry.
			var characterTags = (await Database.GetCharacterTags(character.Id)).Concat(ItemShopSuppressTags).Distinct().OrderBy(t => t).ToArray();
			// #262: the player's ACTIVE quest ids, for quest-gated world objects (WorldObject.GateQuestId / SpawnsFor).
			// Empty for a null character; ungated objects (the default) ignore it.
			var activeQuestIds = (await Database.GetCharacterQuests(character.Id)).Where(q => q.State == QuestState.Active).Select(q => q.QuestId).ToHashSet();

			// Populate the shared world model BEFORE building so the player entity exists for the spawn build.
			// Resolve which COPY (instance) of this map the player joins from its instancing mode (#268): register the
			// player first (which picks/keeps their instance), then populate THAT instance's NPCs.
			var map = await Database.GetMap(mapId) ?? throw new  InvalidOperationException();
			// #3: aggregate the equipped-gear stat bonuses so the player's live vitals seed with gear-boosted maxes.
			var gearBonuses = MaxStatAggregator.AggregateGearBonuses(await Database.GetEquippedStatItems(character.Id, character.ActiveWeaponSet));
			_simulation.RegisterPlayerInWorld(client, map.InstanceMode, gearBonuses);
			var instanceId = _simulation.PlayerWorldEntity(client)?.InstanceId ?? WorldState.DefaultInstance;
			await _broadcaster.EnsureZoneNpcs(mapId, instanceId);
			// The map's static world objects, loaded into the zone once (#424) - the entry + interest passes read them
			// from there instead of re-querying per entry.
			await _broadcaster.EnsureZoneWorldObjects(mapId, instanceId);

			// Warm this map's terrain heightmap + collision mesh off the tick (#284) so the first aggro/loot doesn't
			// stall the game loop with the ~12 MB decompress + grid build. Fire-and-forget; the caches are thread-safe.
			_ = Task.Run(() => _simulation.Terrain?.Warm(mapId)); // TODO: Is this still needed?

			// Mark this connection as having entered the world. The conversation system (ConversationService), the
			// 0x2020 -> 0x2008 spawn-check (GameServerListener), and the dialogue-close fallback (Handle0x2000) are
			// all gated on this - without it NPC dialogue silently never opens.
			client.Session.WorldEntered = true;

			// Retail split (proven byte-exact in the *-create vs *-login captures): a CREATE-world entry carries NO quests
			// in the snapshot (empty Quest_c region) and grants the starter quest LIVE afterwards; a LOGIN entry carries
			// the character's held quests in the snapshot. So the snapshot region is data-driven from CharacterQuests on
			// login, and empty on create.
			var isCreateEntry = client.Session.IsCreateWorldSession;
			var (snapshotQuests, primaryQuest) = isCreateEntry
				? (new List<QuestDefinition>(), (QuestDefinition?)null)
				: await LoadSnapshotQuests(character);
			// #74/#71: the character's learned spells ride the snapshot's known-list so they survive relog + place on the
			// action bar (instead of the per-login DispatchLearnSpell band-aid). Seeded at creation (CharacterLoadout),
			// so this is a plain read.
			var knownSpellIds = (await Database.GetCharacterSpells(character.Id)).Select(s => s.SpellId).ToArray();
			// The character's captured stat sheet (level/vitals/attributes/PvP counters/ratings), authored into the
			// snapshot's stat block per-character; empty for a freshly-created character (skeleton + patches then).
			var statSheet = (await Database.GetCharacterStats(character.Id)).Select(s => (s.StatId, s.Value)).ToArray();
			// The playfield-instance token is BeachPlayfieldId - the ONE value already threaded through the map-change, the
			// player-spawn transform, and the world objects. Thread it into the SNAPSHOT (stat slot 0xBF, via
			// BuildFromScratch) and the zone as well, so all of them AGREE BY CONSTRUCTION. The client scopes the character
			// to the playfield it loaded, so a snapshot token that disagrees with the map-change/spawn hides the abilities
			// tab (a server-minted id fails exactly this way). No more extract-the-token-back-out-of-the-snapshot. (Per-map
			// sourcing is a follow-up - the beach id is used for every map for now, exactly as the spawn/map-change do.)
			// #305: the token is now sourced PER-MAP (Map.PlayfieldToken), falling back to the beach default for maps with
			// no captured token. Threaded identically through the map-change, snapshot, world objects, spawn transform and
			// the zone so they still agree by construction - only the source value changes. A wrong (beach) token in a
			// mountable zone like Old Tarantia made the client read it as the no-mount Tortage beach.
			var mapToken = map?.PlayfieldToken ?? 0;
			var playfieldToken = mapToken != 0
				? mapToken
				: BeachPlayfieldId;
			_simulation.World.ZoneFor(mapId, instanceId).PlayfieldToken = playfieldToken;
			// The Completed tab rides the snapshot too (#390); a create-world entry has none.
			var completedJournal = isCreateEntry
				? []
				: await LoadCompletedJournal(character);

			// The quest INDEX needs, per active quest, its AREA's 4CC and that area's full quest catalog ordered by
			// JournalSequence (#390) - neither is per-quest data.
			var (indexAreas, areaCatalog) = await LoadAreaCatalog(snapshotQuests);
			var instance = client.Session.Character.Id;
			var equipped = new Dictionary<ulong, (Item Item, uint Hash)>();
			foreach (var slot in InventoryEquippedSlots)
			{
				var item = equipment![slot];
				if (item is { Hash: not 0 })
					equipped[(ulong)slot] = (item, item.Hash);
			}

			(Item, uint)? bagFirst = equipment!.FirstPosBag is { Hash: not 0 }
				? (equipment!.FirstPosBag, equipment!.FirstPosBag.Hash)
				: null;

			var typedRoot = FullPlayerCharacterBuilder.BuildRoot(characterTags, FullPlayerCharacterBuilder.FullExpansionFlags, FullPlayerCharacterBuilder.FullSubscriptionFlags, false, true,
				FullPlayerCharacterBuilder.BuildContainers(instance, equipped, bagFirst, inventoryRows ?? []));
			var tails = FullCharSnapshotSubClass.BuildFromScratch(snapshotQuests, primaryQuest!, client.Session.Character, instance, playfieldToken, knownSpellIds, statSheet, completedJournal,
				indexAreas, areaCatalog);
			var snapshot = BuildWorldEntrySnapshot(client, typedRoot.ToProtobuf(), tails);

			// TODO: Just send all? no need to batch them
			var toSend = new List<Rpc>
			{
				BuildMapChange(client, map!, playfieldToken)
			};

			// World objects are INTEREST-STREAMED, not dumped (#424): entry sends the ones within range and movement
			// streams the rest, which is what retail does - in Hoxxling_..._gameplay2 only 58 of 158 object handles
			// arrive at entry, the last one 687s later. They keep their retail POSITION here (early in the stream, before
			// the snapshot); BroadcastPlayerMove takes over from there.
			var (worldObjectRecords, worldObjectTotal) = _broadcaster.BuildEntryWorldObjects(client, _simulation.PlayerWorldEntity(client)!, activeQuestIds);
			toSend.AddRange(worldObjectRecords);

			toSend.Add(BuildBurstlessPlayerSpawn(client, equipment, playfieldToken));

			toSend.Add(snapshot);

			// #398: the in-range NPCs ride the ENTRY BURST (before the client's first move), matching retail's shape
			// (fr64-75 in the Hoxxling beach login capture) instead of a post-load interest volley. BuildEntryNpcBurst
			// seeds them into the player's Known set, so the interest layer (BroadcastPlayerMove) then streams only the
			// DELTA as the player walks - and the initial set is never culled mid-load (subsumes #245). The markers /
			// captive pose for these NPCs are separate IIRs and ride SendEntryNpcFollowups after the flush.
			var (entryNpcRecords, entryBurstNpcs) = await _broadcaster.BuildEntryNpcBurst(client);
			toSend.AddRange(entryNpcRecords);

			toSend.Add(BuildVitals(client));
			toSend.Add(BuildGameTime(client));

			foreach (var rpc in toSend)
				await client.SendAsync(rpc);

			await RestoreEntryBuffs(client);

			Logger.LogInformation("Burst-less world entry (map={Map}, '{Name}'): sent {Count} constructed packets ({Npcs} in-range NPCs, {Objects}/{ObjectTotal} in-range world objects).", mapId,
				character.Name, toSend.Count, entryBurstNpcs.Count, worldObjectRecords.Count, worldObjectTotal);

			// The per-NPC quest markers / captive pose for the burst NPCs (separate IIRs, after the spawn records).
			await _broadcaster.SendEntryNpcFollowups(client, entryBurstNpcs);

			// Spawn this player to nearby OTHER players (its NPC set already rode the burst above). The quest journal is
			// NOT re-sent here: retail ships both tabs in the login snapshot and sends no quest packet at all until the
			// first in-session accept (#389/#390), so a live "restore" burst is a fabrication - and the one that used to
			// live here delivered every quest under a single area header, which filled the Completed tab with one bogus
			// group and left Active empty.
			await _broadcaster.BroadcastPlayerEntry(client);

			// On a CREATE-world entry the snapshot carried no quests (empty region above), so deliver the starter quest
			// LIVE exactly as retail does: QuestLogList (J-panel) then QuestFullUpdate (detail). It was persisted at
			// character creation, so it rides the snapshot on every later login - and nothing re-sends it live there
			// (#390), so there is no double-send.
			if (isCreateEntry)
			{
				// The starter quest is seed data every database carries; without it a new character enters the world with
				// no first objective and nothing to explain why, so a missing definition is corruption, not a case.
				var beachDef = await Database.GetQuestDefinition(BeachStarterQuestId)
					?? throw new InvalidOperationException($"The beach starter quest {BeachStarterQuestId} has no QuestDefinition - it is seed data, so this database is incomplete.");
				await WorldBroadcaster.SendQuestLogList(client, beachDef, [BeachStarterQuestId]);
				await _broadcaster.SendQuestFullUpdate(client, beachDef);
				Logger.LogInformation("Create-world entry: delivered starter quest {QuestId} live to '{Name}'.", BeachStarterQuestId, character.Name);
			}

			// #262: if the player logs in mid-escort (an escort stage active), resume the escortee's follow now - the
			// live grant-trigger only fires on the talk itself, so without this a relog leaves her at the cage. Data-
			// driven off the Escort goal + its escortee template id (no hard-coded quest id / name).
			foreach (var q in await Database.GetCharacterQuests(character.Id))
			{
				if (q.State != QuestState.Active)
					continue;

				var qdef = await Database.GetQuestDefinition(q.QuestId);
				if (qdef?.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort) is { TrailingRefId: not 0 } escort)
				{
					_broadcaster.SetNpcFollowByTemplate(mapId, escort.TrailingRefId, client.Session.Character.Id, true);
					break;
				}
			}
		}

		// The change-map prep IIR bodies (begin + destination announce) the retail teleport sends before the 0x2002
		// handoff, built by the named TeleportCodec (#219, World/TeleportCodec.cs). In-client verification pending.
		private async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u)
		{
			var instance = client.Session.Character.Id;
			// Source the destination playfield token from the destination map's burst so the announce matches the token
			// the reconnect's world-entry will stamp (#219 / #211) - it is per-playfield-instance, not a constant.
			var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
			foreach (var body in new[] { TeleportCodec.BuildBegin(instance, destMapId), TeleportCodec.BuildDestination(instance, destMapId, position, destToken, destDescriptor) })
			{
				await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client), GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));
			}
		}

		// Emits the retail level-up dings: ONE NewLevelIIR_t (0x6D314743) per level gained - the event that fires the
		// client's "Congratulations, you have reached level N!" banner + the level-up visual/audio effect (#209). The
		// banner text is client-side and gated on this packet; the raw Level stat + AttributeChanged lines alone show
		// nothing. Retail sends a SEPARATE event per level (Hoxxling dinged L2/L3/L4 as three, frames 9937->), each
		// carrying [newLevel, currentXP, xpThisLevel, xpNextLevel, 1, 8, 0] (fields 5/6/7 constant across every
		// capture). Sent before the AttributeChanged burst (combat-levelup capture ordering).
		private async Task SendLevelUpDings(INetworkClient client, uint fromLevel, uint toLevel, uint finalRemainingXp)
		{
			var instance = client.Session.Character.Id;
			var @class = client.Session.Character.Class;
			for (var lvl = fromLevel + 1; lvl <= toLevel; lvl++)
			{
				var thisXp = LevelingResolver.CumulativeXpForLevel(lvl);
				var nextXp = LevelingResolver.CumulativeXpForLevel(lvl + 1);
				var currentXp = lvl == toLevel
					? thisXp + finalRemainingXp
					: thisXp; // only the final level carries partial XP
				ReadOnlySpan<uint> deltas = [currentXp, thisXp, nextXp, 1u, 8u, 0u];

				await WorldIir.SendFramed(client, NewLevelIirCodec.Build(instance, lvl, deltas));

				// The combos this class unlocks AT this level, from the class<->combo ACCESS map (ClassCombo) - retail
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). ClassCombo replaces the old
				// class-independent level-keyed catalog (casters have no rows); empty until populated from dumps.
				foreach (var combo in await Database.GetClassCombosUnlockedBetween(@class, lvl - 1, lvl))
					await WorldIir.SendFramed(client, ComboBookIirCodec.Build(instance, combo.Id));
			}
		}

		private async Task SendReadyForPlayScreen(INetworkClient client)
		{
			var sender = client.Session.IsUnnamedCreatePhase
				? Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x59)
				: Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58);

			var receiver = client.Session.IsUnnamedCreatePhase
				? Endpoints.EndpointFromBytes(0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xC2, 0xDD, 0xD8, 0x10)
				: client.Endpoint(Endpoints.GameCharInterfaceType);

			using var bodyStream = new FaolanStream();
			if (client.Session.IsUnnamedCreatePhase)
			{
				// New-character create flow: the create-capture's ValidResurrectionSpots body - a sibling of the login
				// WriteValidResurrectionSpotsBody with the create variant's own constants (see ResurrectionSpotsBody).
				ResurrectionSpotsBody.WriteCreateFlowValidResurrectionSpots(bodyStream, client.Session.Character.Position);
			}
			else
			{
				// Existing-character login: this 0x2024 is byte-structurally GameCharInterface.DispatchValidResurrectionSpots
				// (the death/release screen) - at login the client consumes it silently to set the resurrection anchor. Build
				// the body via the shared WriteValidResurrectionSpotsBody from the character's actual resurrection point (the
				// same closest-known/AlwaysKnown resolution SendDeathScreen does), so the anchor is per-character/per-map and
				// advertises our REAL RespawnPoint.Id (the id RespawnMe echoes back) instead of the Strie capture's hardcoded
				// beach point. Falls back to the universal Tortage Beach default when the map defines no point.
				var spot = await ResolveLoginResurrectionSpot(client.Session.Character);
				ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(bodyStream, client.Session.Character.Position, [spot]);
			}

			await client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceOpcodes.DispatchValidResurrectionSpots, bodyStream.ToArray()));
		}

		// Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: body is
		// 0x0000C350 + the polled entity handle + a status byte (0x02 for the player's own avatar, else 0x00).
		private async Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			// The 0x2020 poll reply (0x2008) doubles as the NPC quest marker. Compute it from the player's quest
			// state for the polled handle (#241) instead of the old hardcoded 0x02-for-Casilda, which forced '!' on
			// EVERY poll and overrode the state-driven marker (so it never cleared after accept). Non-giver /
			// not-in-view -> 0x00 (still confirms the spawn). Capture-matched: poll reply was 0x02 before accept,
			// 0x00 after (Ranochar frames 125 -> 698).
			var status = await _broadcaster.QuestMarkerStateForHandle(client, handle);

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteByte(status)));
		}

		// The single existing-character WORLD ENTRY used by both login and the teleport GM command: send the 0x201c
		// channel handshake, then replay the character's current-map world burst from the DB (MapChange + world
		// objects + NPC spawns + the player spawn + the inventory snapshot, all re-addressed and DB-driven). Falls
		// back to the beach burst for any map without a usable burst. The caller has already set Character.MapId/Pos.
		private async Task SendWorldEntry(INetworkClient client)
		{
			await Send0X201C(client);

			// #237: re-grant the character's learned SPELLS (DispatchLearnSpell 0x200A) BEFORE the world snapshot, so
			// the client absorbs them as part of the initial load and stays SILENT - retail sends them pre-entry
			// (NecroL80 login: grants at frame 59, the 0x737A6DF9 snapshot at frame 152). Granting them post-ready
			// (the old PostReadyFollowUp call) made the client treat each as a fresh learn and re-toast "You have
			// learned a new spell" on EVERY login. Done here, the shared world-entry path, so it's silent on login AND
			// on changemap/teleport re-entry. (Abilities 0x2052 stay post-0x2024 - they don't toast.)
			await GrantCharacterSpells(client);

			var mapId = client.Session.Character.MapId;

			// Burst-less entry (#210) is the ONLY world-entry path: the whole world entry is built from
			// generated/structured pieces - no captured burst anywhere. (The FAOLAN_BURSTLESS_ENTRY env toggle and its
			// captured-burst replay fallback were removed once burstless became the verified default. This comment used
			// to add "the replay survives as an internal safety net for a map with no quest definition" - it does not:
			// that case now logs an error and aborts the entry.)
			await SendBurstlessWorldEntry(client, mapId);
		}

		private async Task SendWorldHandoff(INetworkClient client)
		{
			var realm = await Database.GetRealm(client.Session.Character.RealmId);
			var address = $"{realm!.IpAddress}:{realm.Port}";

			await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client), GameInterfaceOpcodes.DispatchRedirectToServer,
				b => b.WriteByte(0x00).WriteStringByteLength(address)));

			Logger.LogInformation("Create-editor: sent enter-world handoff (0x2002) advertising world server {Address}.", address);
		}

		// The complete burst-less player FA015807 spawn packet. Entry is a pure READ of the scaffold: the rows are seeded
		// at CREATION (CreateFlowService.CommitCharacterName), where the class and editor body they are generated from
		// are first known, and a character without them throws in the builder. One character object throughout - the
		// session copy carries the scaffold rows (WithSessionNavigations) - so identity, transform and scaffold cannot
		// disagree.
		private static Rpc BuildBurstlessPlayerSpawn(INetworkClient client, CharacterEquipment equipment, uint playfield)
		{
			var character = client.Session.Character;
			// The CharacterIIR is built from code (PlayerCharacterIir, formerly PlayerCharacterIirTemplate.bin, #258):
			// the fixed render template with this character's name/identity/looks/worn gear applied.
			var message = PlayerCharacterIir.Build(character, equipment);

			// A near-zero stored quaternion (the degenerate W=0 default) can't be realised by the avatar; fall back to
			// identity rather than risk it (the live splice path guards the same way).
			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			return BuildWorldEntrySpawn(client, PlayerSpawnBuilder.BuildFromScratch(character.Id, playfield, character.Position, rotation, message.ToProtobuf(), character));
		}

		// The burst-less world-object packets: each WorldObjects row built via WorldObjectBuilder and framed. Built on
		// the row's captured handle + the live playfield instance (the rewrite the live splice path applies).
		// The character-creator (editor) phase: a not-yet-named character is connected. The client instance is
		// the character's real id now, so the unnamed-character state alone identifies the editor flow.

		private static Task ReportDimensionId(INetworkClient client, uint realmId, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchReportDimesionID, s => s.WriteString(realmId.ToString())));
		}

		private static Task ReportServerId(INetworkClient client, uint value, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			// Body: [u32 0x54 (fixed header)][u32 value]. The create-flow auth handshake reports value 1.
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchReportServerID, s => s.WriteUInt32(0x54u).WriteUInt32(value)));
		}

		private static async Task Send0X201C(INetworkClient client)
		{
			var receiver = client.Endpoint(Endpoints.GameCharInterfaceType);
			var ticketBase = 0x00160000u + ((uint)Environment.TickCount & 0xFFFF);

			// Retail (Strie login) sends five channel registrations before the spawn burst: four with the
			// 0x65 prefix and state ids {5, 6, 0x10, 0x12}, then a final 0x68 prefix with state 1. The old
			// three-entry set (with a duplicated state 6 and missing 5/0x12/the 0x68 entry) left the client
			// stuck before it ever started polling spawns (0x2020), so nothing rendered.
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000005, ticketBase);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000006, ticketBase + 1);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000010, ticketBase + 2);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000012, ticketBase + 3);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000068, 0x00000001, ticketBase + 4);
		}

		private static Task Send0X201CEntry(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, uint prefix, uint state, uint ticket)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceOpcodes.DispatchSetItemStat,
				b => b.WriteUInt32(prefix).WriteUInt32(client.Session.Character.Id).WriteUInt32(state).WriteUInt32(0x00000017).WriteUInt32(ticket)));
		}
	}
}
