using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
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
		//
		// ⚠️ Left EMPTY, and that is now a decision rather than an unconfirmed guess. The corpus CONFIRMS 6617 is a real
		// tag of this family: retail awards it with `DispatchAddTag` in three captures (a guild-invite session, a
		// group/trade session, a beach-to-Tortage run), and it is the one captured tag id of fourteen that resolves to
		// NOTHING in the achievement catalog (LDB 55701) - see #748. So the id and its kind are settled.
		//
		// What is NOT retail is pre-seeding them at world entry. Retail awards the flag when the bundle has actually
		// been shown; stamping it into every login snapshot is our own suppression, which is why it stays off until
		// somebody decides the upsell is worth suppressing that way.
		private static readonly uint[] ItemShopSuppressTags = []; //6617, 6618, 6619, 6620, 7316, 7416, 7417, 7418, 7419, 7420, 7421];

		private async Task FlushPendingPost205FFollowUp(INetworkClient client)
		{
			if (!client.Session.HasPendingPost205FFollowUp)
				return;

			// Whether this connection wants the LOGIN ready burst is a question about its ROLE, so ask EntryMode
			// directly and cover every mode (#432). Two of them must never flush here:
			//   CreateEditor - it is showing the character creator; its ready sequence is the editor burst, sent on
			//                  CharInPlayIIR, and the client keeps polling 0x2020 throughout.
			//   CreateWorld  - it is running the beach/Kalanthes intro. Retail sends its ready burst in answer to
			//                  CharInPlayIIR (fr317 -> fr324), so firing it from a poll lands inside the cutscene:
			//                  the blue screen. SendCreateWorldReadyBurst owns that moment instead.
			switch (client.Session.EntryMode)
			{
				case WorldEntryMode.CreateEditor:
				{
					Logger.LogInformation("Create-editor connection: the post-0x205F follow-up waits for the create handoff, not for this 0x2020 poll.");
					return;
				}

				case WorldEntryMode.CreateWorld:
				{
					Logger.LogInformation("Create-world connection: the ready burst belongs to its CharInPlayIIR, not to this poll.");
					return;
				}

				case WorldEntryMode.Login:
				case WorldEntryMode.TeleportReentry:
				{
					break; // the two modes this burst is FOR
				}

				default:
				{
					Logger.LogError("Post-0x205F flush on an unhandled entry mode {Mode}: sending the login burst by default, which may be wrong for it.", client.Session.EntryMode);
					break;
				}
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
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CharInPlayCodec.Build(new CharInPlayIIR(instance)))));

			// RegenIIR - retail's fr325 sits here, and a RegenIIR is a DELTA the client ADDS to its own current
			// vitals (RegenCodec). So the value is that character's deficit at that moment, not a constant: replaying
			// the captured 0 / 0x8F2 / 0 verbatim handed EVERY new character +22.90 mana on top of a pool this same
			// world entry had just announced as full, and a level-1 Necromancer stood up reading 269 / 246 until the
			// first relog recomputed it. What belongs here is this character's own shortfall, which for a freshly
			// created one is zero in all three pools - the record still rides, because it is retail's sequence and it
			// is what starts the client's regen loop.
			// A world entity with no live vitals has nothing to say here, and a fabricated delta is exactly what this
			// record must not carry - so it rides at zero.
			var (health, mana, stamina) = _simulation.PlayerWorldEntity(client)?.Vitals.ToRefillDeltas() ?? default;
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(RegenCodec.Build(instance, health, mana, stamina))));

			// SyncCanSprint (0x200C, one byte) - the sprint pool starts available (#867).
			await client.SendAsync(new Rpc(ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, instance),
				GameCharInterfaceProjectMethods.DispatchSyncCanSprint, SprintFlagCodec.Build(true)));

		}

		// Everything the client needs to LEAVE the loading screen and stand in the world: the deferred stat prompt, the
		// quest marker, 0x2024 ReadyForPlayScreen, the ability + combo grants and the post-0x2024 record stream.
		//
		// Two callers, because two flows reach this moment differently: a login/teleport arms it on the client's 0x205F
		// and flushes on the next 0x2020 poll, while a create-world connection sends it on CharInPlayIIR - after the
		// intro cutscene, never during it.
		private async Task SendWorldReadyBurst(INetworkClient client)
		{
			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(StatCodec.Build(StatIirs.ForCharacter(client.Session.Character.Id, 0x00000013, 0x00000000)))));
			Logger.LogInformation("Sent deferred StatIIR prompt.");

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetQuestMarker,
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
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CharInPlayCodec.Build(new CharInPlayIIR(instance)))));

			// record 1: 0x0000200C, 1-byte body
			await client.SendAsync(new Rpc(ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, instance),
				GameInterfaceMethods.DispatchReportServerID, (byte[])[0x01]));

			// record 2: 0x00002000 - the StartFixed3d destiny step (ScryScriptIIR 0xA36D3B74), constructed (was verbatim;
			// byte-identical, proven by QuestTests.BuildScryStep_MatchesCapture). The body is [u32 len][step bytes].
			// #234: SKIP on the create-world connection - the create destiny cutscene overlay (SendCreateCutsceneStep)
			// already sends StartFixed3d at IntroDone (matching retail, which sends it ONCE there). Sending it here too
			// (before the cutscene) is a DUPLICATE retail doesn't have (loopback f649) and re-engages the cinematic.
			if (client.Session.EntryMode != WorldEntryMode.CreateWorld)
			{
				var startFixed3d = ScryScriptCodec.Build(instance, "StartFixed3d");
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(startFixed3d)));
			}

			if (Global.EnableTutorialOverlay)
			{
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
					b => b
						.WriteArray(0x00, 0x00, 0x00, 0x1F, 0xA3, 0x6D, 0x3B, 0x74, 0x00, 0x00, 0xC3, 0x50)
						.WriteUInt32(instance)
						.WriteArray(0x00, 0x00, 0x00, 0x00, 0x0E, 0x08, 0x05, 0x10, 0x00, 0x18, 0xF6, 0xC6, 0x17, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00)));

				var welcomeTutorial = ScryScriptCodec.Build(instance, "WelcomeTutorial");
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(welcomeTutorial)));
			}

			// #262: apply any after-quest world-object effects up-front (e.g. a relog after freeing Casilda drops the
			// beach chain-wall collision, so the returning player isn't re-blocked by the world-entry burst's chain
			// dynel), and clear the escortee's captive pose if she's already freed. Both data-driven / no-op when N/A.
			await _broadcaster.ApplyQuestWorldEffects(client);
			await _tortageIntro.ClearCasildaCaptivePose(client); // it IS in the intro service now; the gating is Gitea's, not a TODO here

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

			// (The post-load second-snapshot delivery was removed 2026-06-12: the client ignores/rejects a
			// second 0x737A6DF9 for an already-constructed identity. Inventory rides the WORLD-ENTRY snapshot,
			// see BuildBurstlessSnapshotRoot.)

			// Wallet sync. Retail does NOT push a 0x2005 at login - the balance rides the FullChar snapshot, and
			// the slot this comment used to call "undecoded" is CharacterStatId.Money in the stat sheet, authored
			// from the character since #447. So this push is now REDUNDANT rather than a stand-in for something
			// missing; it stays until an in-client login confirms the sheet alone shows the right balance, because
			// removing it on reasoning alone would show a moneyed character 0 if the sheet slot turned out to be
			// read some other way. Reason 0 = a plain sync, not a gain celebration.
			if (client.Session.Character.Money > 0)
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
				await WorldIir.Send(client, ComboBookCodec.Build(instance, comboId));
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

			var viewer = _simulation.PlayerWorldEntity(client);
			viewer?.Confirmed.Add(requestId);

			if (client.Session.WorldEntered || client.Session.EntryMode == WorldEntryMode.CreateWorld)
				await SendSpawnCheckResponse(client, requestId);

			// #225: and this is where the "this entity is alive and idling" assert belongs - ONCE, to the client that has
			// just told us it built the dynel. Retail sends p50 1 per actor (37,965 records over 7,969 actors); the pulse
			// this replaces sent one per NPC per second, which is where #716's 119.6 BCC/s came from. Sending it here
			// rather than from the tick is also what makes the Confirmed gate structural instead of a check.
			if (viewer != null && _simulation.World.ZoneForEntity(viewer).TryGet(requestId, out var confirmed) && !confirmed.IsPlayer && !confirmed.IsLoot)
				await _broadcaster.SendNpcIdleAssert(client, confirmed);

			Logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}", ack0, requestId, ack1, characterId);
			Logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}", ack0, requestId, ack1,
				characterId, client.Session.HasPendingPost205FFollowUp);
			await FlushPendingPost205FFollowUp(client);
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
			return RespawnResolver.Resolve(character.Position, points, discovered, character.IsCriminal) ?? points[0];
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
			await _buffs.RestoreBuffs(client);

			// NOT on the character's very first entry. Shield of the Risen is newbie protection for someone arriving in
			// the world, and the create-world entry is not that: it is the intro cutscene, where the character is
			// washed ashore and the player has no control - so the buff icon and its "you are protected" line appear
			// over a cinematic, and the shield is then spent by the first swing of a fight the player has not had yet.
			// Every LATER login gets it as usual, including the first ordinary login after creation.
			if (client.Session.EntryMode == WorldEntryMode.CreateWorld)
			{
				Logger.LogInformation("Create-world entry for '{Name}': skipping Shield of the Risen (it belongs to a login, not to the intro).", client.Session.Character.Name);
				return;
			}

			await _buffs.SendShieldOfTheRisen(client);
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

			var mount = await Database.GetMount(mountId);
			if (mount == null)
				return;

			var self = character.Id;
			await _buffs.SendMagicBuffIir(client, self, self, MountCatalog.RideSpell, MountCatalog.RideBuffPayload(BuffService.CasterLevel(client)));
			await SendStatUpdate(client, new CharacterStatBlock
			{
				[CharacterStatId.Mount] = mount.CreatureId,
				[CharacterStatId.MountSpeed] = MountCatalog.MountSpeed
			}, self);

			var entity = _simulation.PlayerWorldEntity(client);
			entity?.IsMounted = true;
			entity?.MountRideStat = mount.CreatureId;
			entity?.MountDamageTaken = 0; // #142

			Logger.LogInformation("Restored mount {Mount} for {Name} on map {Map}.", mount.Spell.Name, character.Name, character.MapId);
		}

		// Builds + sends the entire world entry from generated/structured pieces - no captured burst (#210). Mirrors
		// SendWorldEntry's world-model setup + record order (MapChange, objects, player spawn, snapshot, vitals, 		// gametime), then runs interest for the NPCs. Beach-shaped (FullCharSnapshotSubClass + BeachPlayfieldId); a non-beach
		// map would get beach tails, so the flag is for a beach login until per-map sourcing lands.
		private async Task SendBurstlessWorldEntry(INetworkClient client, uint mapId)
		{
			var character = client.Session.Character;

			// Nothing has been handed to the client for THIS entry yet - so the tick's interest sweep must stay off
			// them until the burst below is flushed (see INetworkClientSession.WorldEntryDelivered). Cleared here
			// rather than only initialised, because a re-entry (create-world -> world) reuses the connection.
			client.Session.WorldEntryDelivered = false;

			// EVERY item the character holds - worn (container Equipped) and carried alike, since #750 - which is both
			// the four snapshot containers and the spawn's visible gear, from one read.
			// The beach starter quest (10238) is granted ONCE at character creation (CommitCharacterName), not re-seeded
			// here - so a completed/removed quest never reappears. The journal is now driven purely from CharacterQuests.
			var heldItems = await Database.GetCharacterItems(character.Id);
			// Union in the item-shop-suppression flags (see ItemShopSuppressTags) so the client's shop-open Lua sees
			// HasTag(X)==true and never auto-opens the itemshopbrowser on entry.
			var characterTags = (await Database.GetCharacterTags(character.Id)).Concat(ItemShopSuppressTags).Distinct().OrderBy(t => t).ToArray();
			// #262: the player's ACTIVE quest ids, for quest-gated world objects (WorldObject.GateQuestId / SpawnsFor).
			// Empty for a null character; ungated objects (the default) ignore it.
			var activeQuestIds = (await Database.GetCharacterQuests(character.Id, QuestState.Active)).Select(q => q.QuestId).ToHashSet();

			// Populate the shared world model BEFORE building so the player entity exists for the spawn build.
			// Resolve which COPY (instance) of this map the player joins from its instancing mode (#268): register the
			// player first (which picks/keeps their instance), then populate THAT instance's NPCs.
			var map = await Database.GetMap(mapId) ?? throw new  InvalidOperationException();
			// #3/#251/#863: the additive stat bonuses the player carries into the world - equipped GEAR, the feats they
			// TRAINED, and the feats the client trains for them - so their live vitals seed with all three.
			//
			// A character routinely gets NOTHING from the TRAINED feat half, and that is a BUILD, not a gap (#589).
			// The feat pipeline is whole: FeatIdMaps carries all 6,593 entries of the client's own FeatTable_t, 2,939
			// of them have stat effects and 808 grant HealthMax (`Mage: HP 1`..`5` among them). Our three imported
			// characters spend every point they have - both level-80 mages come to exactly 79 - on class-tree
			// abilities and spell nodes, so their trained contribution is a true zero. Do not read an empty result
			// here as missing data. The AUTO-TRAINED half is never empty, and is never in a feat list either.
			var statBonuses = await CharacterStatBonuses.For(Database, character);
			// #444: the class/level BASE the character's attributes start from. The client computes this locally (retail
			// sends a flat 10 for every attribute), so it is decoded data, not wire data - see ClassAttributeBaseModule.
			var classBase = await Database.GetClassAttributeBase(character.Class, character.Level);
			if (classBase.Count == 0)
			{
				Logger.LogWarning("World entry: no class attribute base for {Class} at level {Level} (#444) - {Name}'s attributes are gear+buffs only, so regen and mitigation will read low.",
					character.Class, character.Level, character.Name);
			}

			// #590: what the character's trained feats do to their SPELLS - "+35% on Flesh to Worms" and the like.
			// Keyed by spell id (the feat names a line; the fan-out to its ranks is resolved in the query), so the
			// cast path never has to look one up.
			var spellBoosts = await Database.GetCharacterSpellBoosts(character.Id);
			// #592: the handful of the character's spells that override which ATTRIBUTE their damage scales off. The
			// class rule covers the rest, so this is usually empty.
			var spellScaling = await Database.GetCharacterSpellScalingAttributes(character.Id);
			// #697: and what they do to the character's COMBOS - the opening/finishing weapon attacks' damage and
			// crit, plus the whole-combo damage that rides the finisher's spell.
			var comboBonuses = await Database.GetCharacterComboBonuses(character.Id);

			// #309: whether the armed teleport picked the EPIC difficulty - consumed here (the reconnect is a new
			// connection, so the flag rode PendingEpicEntry) and threaded into BOTH the instance selection (an epic
			// copy never shares a zone with a normal one) and the MapChange's class word below.
			var epicEntry = PendingEpicEntry.TryRemove(character.Id, out _);
			// #512: a GROUP map is one copy per TEAM, so the entry needs the team's id - which the CS tier owns, and
			// which this listener reaches through the same seam LeaveTeam/KickFromTeam already use.
			_simulation.RegisterPlayerInWorld(client, map.InstanceMode, statBonuses, classBase, spellBoosts, spellScaling, comboBonuses,
				_csPlayerAgentDispatcher.TeamIdOf(client.Session.Character.Id), epicEntry);
			var instanceId = _simulation.PlayerWorldEntity(client)?.InstanceId ?? WorldState.DefaultInstance;

			// #305: the playfield-instance token is sourced PER-MAP (Map.PlayfieldToken), falling back to the beach
			// default for maps with no captured token. Threaded identically through the map-change, snapshot, world
			// objects, spawn transform and the ZONE, so they agree BY CONSTRUCTION - the client scopes the character to
			// the playfield it loaded, so a token that disagrees across those packets hides the abilities tab, and a
			// spawn stamped with a disagreeing one is silently dropped. (A wrong (beach) token in a mountable zone like
			// Old Tarantia made the client read it as the no-mount Tortage beach.)
			//
			// ⚠️ Stamped HERE, on the line after the player is placed, and not further down where it is first USED. The
			// moment the player entity is in the zone the world tick can see it and stream spawns to them (#778's
			// sweep), and every one of those reads Zone.PlayfieldToken. This entry then spends seconds in DB work
			// before it reaches its own burst, so a stamp any later ships the zone's MINTED fallback token to the
			// client and it drops the lot. The zone must never be reachable with a stale token, which is a property of
			// WHERE this line is - so keep it adjacent to the placement.
			var mapToken = map.PlayfieldToken;
			var playfieldToken = mapToken != 0
				? mapToken
				: BeachPlayfieldId;
			_simulation.World.ZoneFor(mapId, instanceId).PlayfieldToken = playfieldToken;

			await _broadcaster.EnsureZoneNpcs(mapId, instanceId, character.Class);
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
			var isCreateEntry = client.Session.EntryMode == WorldEntryMode.CreateWorld;
			// A CREATE entry carries no quests, so it also carries no progress - the resolver is only ever asked about
			// a goal of a quest in `snapshotQuests`.
			var (snapshotQuests, primaryQuest, solvedTimesOf) = isCreateEntry
				? (new List<QuestDefinition>(), (QuestDefinition?)null, static (QuestGoalDefinition _) => 0u)
				: await LoadSnapshotQuests(character);
			// #74/#71: the character's learned spells ride the snapshot's known-list so they survive relog + place on the
			// action bar (instead of the per-login DispatchLearnSpell band-aid). Seeded at creation (CharacterLoadout),
			// so this is a plain read.
			var knownSpellIds = (await Database.GetCharacterSpells(character.Id)).Select(s => s.SpellId).ToArray();
			// The character's captured stat sheet (level/vitals/attributes/PvP counters/ratings), authored into the
			// snapshot's stat block per-character; empty for a freshly-created character (skeleton + patches then).
			var statSheet = (await Database.GetCharacterStats(character.Id)).Select(s => (s.StatId, s.Value)).ToArray();
			// The Completed tab rides the snapshot too (#390); a create-world entry has none.
			var completedJournal = isCreateEntry
				? []
				: await LoadCompletedJournal(character);

			// The quest INDEX needs, per active quest, its LINE's 4CC and that line's full quest catalog ordered by
			// JournalSequence (#390) - neither is per-quest data.
			var (indexLines, lineCatalog) = await LoadLineCatalog(snapshotQuests);
			var instance = client.Session.Character.Id;
			// Every item renders: the token is harvested when a capture carried one and minted otherwise
			// (AcgHash.ForWire, #723), so a `Hash: not 0` gate here would drop exactly the claim/AH items this is
			// meant to deliver.
			var typedRoot = FullCharacterRoots.ForCharacter(characterTags, FullCharacterRoots.FullExpansionFlags, FullCharacterRoots.FullSubscriptionFlags, false, true,
				FullCharacterRoots.Containers(instance, heldItems));
			// The snapshot's current-side vitals are the LIVE ceiling (gear included), the same value the vitals push
			// after it carries - so the sheet does not flick from one max to the other on entry (#580).
			var liveMax = _simulation.PlayerWorldEntity(client)!.Vitals.ToMaxStats();
			var tails = SnapshotTails.FromScratch(snapshotQuests, primaryQuest!, client.Session.Character, liveMax, instance, playfieldToken, knownSpellIds, statSheet, completedJournal,
				indexLines, lineCatalog, solvedTimesOf);
			var snapshot = BuildWorldEntrySnapshot(client, typedRoot, tails);

			// TODO: Just send all? no need to batch them
			var toSend = new List<Rpc>
			{
				// #512: if this copy of the playfield is a team's, the proxy names the TEAM as its owner rather than
				// the player. Asked of the ZONE, so every member is told the same owner.
				BuildMapChange(client, map!, playfieldToken,
					_simulation.PlayerWorldEntity(client) is { } placed ? _simulation.World.TeamOwning(placed.MapId, placed.InstanceId) : null, epicEntry)
			};

			// The MapChange is the one place a player is told which playfield they are in, so it is also where their
			// ~Playfield CHAT channel has to follow - the channel's id IS the map id, and chat routing is the client's
			// job, so a stale subscription means hearing the playfield you left and not the one you are in (#562).
			// The call is idempotent, so re-entering the same map costs nothing.
			if (client.Session.Character is { } chatCharacter)
				await _agentServerDispatcher.RefreshPlayfieldChannel(chatCharacter.Id, chatCharacter.MapId);

			// World objects are INTEREST-STREAMED, not dumped (#424): entry sends the ones within range and movement
			// streams the rest, which is what retail does - in Hoxxling_..._gameplay2 only 58 of 158 object handles
			// arrive at entry, the last one 687s later. They keep their retail POSITION here (early in the stream, before
			// the snapshot); BroadcastPlayerMove takes over from there.
			var (worldObjectRecords, worldObjectTotal) = _broadcaster.BuildEntryWorldObjects(client, _simulation.PlayerWorldEntity(client)!, activeQuestIds);
			toSend.AddRange(worldObjectRecords);

			// The worn half of the same read: the spawn shows what the character is wearing, and it must be the SAME set the
			// snapshot's Equipped container carries or the avatar and the paper doll disagree.
			var worn = heldItems
				.Where(i => i.Container == ContainerKind.Equipped && Enum.IsDefined((CharacterEquipmentSlot)i.Slot))
				.ToDictionary(i => (CharacterEquipmentSlot)i.Slot);
			toSend.Add(BuildBurstlessPlayerSpawn(client, worn, playfieldToken, await Database.GetMorphTargetRegistry(), liveMax));

			toSend.Add(snapshot);

			// #262: if the player logs in mid-escort (an escort stage active), resume the escortee's follow now - the
			// live grant-trigger only fires on the talk itself, so without this a relog leaves her at the cage. Data-
			// driven off the Escort goal + its escortee template id (no hard-coded quest id / name).
			//
			// ⚠️ BEFORE the entry burst, and that ordering is load-bearing (#827): the burst is where the escortee's
			// spawn RECORD is built, and a freed escortee is described by a different body (ordinary idle, walk mode)
			// chosen on `Following`. Resuming the follow after the burst - where this used to sit - meant her record
			// was built as still-captive, so she came back chained on every relog no matter what was sent afterwards.
			foreach (var (_, qdef) in await Database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				if (qdef.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort) is { TrailingRefId: not 0 } escort)
				{
					_broadcaster.SetNpcFollowByTemplate(mapId, escort.TrailingRefId, client.Session.Character.Id, true);
					break;
				}
			}

			// #398: the in-range NPCs ride the ENTRY BURST (before the client's first move), matching retail's shape
			// (fr64-75 in the Hoxxling beach login capture) instead of a post-load interest volley. BuildEntryNpcBurst
			// seeds them into the player's Known set, so the interest layer (BroadcastPlayerMove) then streams only the
			// DELTA as the player walks - and the initial set is never culled mid-load (subsumes #245). The markers /
			// captive pose for these NPCs are separate IIRs and ride SendEntryNpcFollowups after the flush.
			var (entryNpcRecords, entryBurstNpcs) = await _broadcaster.BuildEntryNpcBurst(client);
			toSend.AddRange(entryNpcRecords);

			toSend.Add(BuildVitals(client, _combat.GetOrAddPlayerVitals(client)));
			toSend.Add(BuildGameTime(client));

			foreach (var rpc in toSend)
				await client.SendAsync(rpc);

			// The client now HAS the world - MapChange first, then the objects/spawn/snapshot/NPC burst - so the tick's
			// interest sweep may start streaming to it. Until this point the sweep would have been the first sender to
			// reach the client, with a spawn for a playfield it had not loaded yet.
			client.Session.WorldEntryDelivered = true;

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
				await _broadcaster.SendQuestLogList(client, beachDef, [BeachStarterQuestId]);
				await _broadcaster.SendQuestFullUpdate(client, beachDef);
				Logger.LogInformation("Create-world entry: delivered starter quest {QuestId} live to '{Name}'.", BeachStarterQuestId, character.Name);
			}
		}

		// The change-map prep IIR bodies (begin + destination announce) the retail teleport sends before the 0x2002
		// handoff, built by the named TeleportCodec (#219, World/TeleportCodec.cs). In-client verification pending.
		private async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u, bool epic = false)
		{
			var instance = client.Session.Character.Id;
			// Source the destination playfield token from the destination map's burst so the announce matches the token
			// the reconnect's world-entry will stamp (#219 / #211) - it is per-playfield-instance, not a constant.
			var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
			// #792: the zero-length 0x1B first - retail precedes EVERY IntendedTeleport with it, 102 of 102 across the
			// corpus. The client discards it (its handler is an empty override); we send it because retail does.
			await SendTeleportPending(client);
			foreach (var body in new[] { TeleportCodec.Build(instance, destMapId, epic), TeleportCodec.Build(instance, destMapId, position, destToken, destDescriptor, epic) })
			{
				await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));
			}
		}

		private async Task SendReadyForPlayScreen(INetworkClient client)
		{
			var sender = client.Session.IsUnnamedCreatePhase
				? Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59)
				: Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58);

			var receiver = client.Session.IsUnnamedCreatePhase
				? Endpoints.EndpointFromBytes(0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xC2, 0xDD, 0xD8, 0x10)
				: client.Endpoint(RpcInterfaces.GameCharInterfaceType);

			ValidResurrectionSpots body;
			if (client.Session.IsUnnamedCreatePhase)
			{
				// New-character create flow: the create-capture's ValidResurrectionSpots body - a sibling of the login
				// one with the create variant's own constants (see ValidResurrectionSpotLists).
				body = ValidResurrectionSpotLists.CreateFlow(client.Session.Character.Position);
			}
			else
			{
				// Existing-character login: this 0x2024 is byte-structurally GameCharInterface.DispatchValidResurrectionSpots
				// (the death/release screen) - at login the client consumes it silently to set the resurrection anchor. Build
				// the body via the shared ValidResurrectionSpotLists.Known from the character's actual resurrection point (the
				// same closest-known/AlwaysKnown resolution SendDeathScreen does), so the anchor is per-character/per-map and
				// advertises our REAL RespawnPoint.Id (the id RespawnMe echoes back) instead of the Strie capture's hardcoded
				// beach point. Falls back to the universal Tortage Beach default when the map defines no point.
				var spot = await ResolveLoginResurrectionSpot(client.Session.Character);
				body = ValidResurrectionSpotLists.Known(client.Session.Character.Position, [spot]);
			}

			await client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceMethods.DispatchValidResurrectionSpots, ValidResurrectionSpotsCodec.Build(body)));
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

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetQuestMarker,
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

			// #310: restore the character's Khitai faction standings. Sent AFTER the world entry for the same reason
			// abilities are - they draw into a window rather than into the initial load, so nothing toasts - and only
			// for the factions the character has a row for: a faction at neutral has no row and needs no message,
			// since zero is what the client shows for one it has never been told about.
			await _factions.SendAll(client);
		}

		private async Task SendWorldHandoff(INetworkClient client)
		{
			var realm = await Database.GetRealm(client.Session.Character.RealmId);
			var address = $"{realm!.IpAddress}:{realm.Port}";

			await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client), GameInterfaceMethods.DispatchRedirectToServer,
				b => b.WriteByte(0x00).WriteStringByteLength(address)));

			Logger.LogInformation("Create-editor: sent enter-world handoff (0x2002) advertising world server {Address}.", address);
		}

		// The complete burst-less player FA015807 spawn packet. Entry is a pure READ of the scaffold: the rows are seeded
		// at CREATION (CreateFlowService.CommitCharacterName), where the class and editor body they are generated from
		// are first known, and a character without them throws in the builder. One character object throughout - the
		// session copy carries the scaffold rows (WithSessionNavigations) - so identity, transform and scaffold cannot
		// disagree.
		private static Rpc BuildBurstlessPlayerSpawn(INetworkClient client, IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance> worn, uint playfield,
			IReadOnlyDictionary<string, MorphTarget> morphRegistry,
			CharacterMaxStats maxVitals)
		{
			var character = client.Session.Character;
			// The CharacterIIR is built from code (PlayerCharacterIirs, formerly PlayerCharacterIirTemplate.bin, #258):
			// the fixed render template with this character's name/identity/looks/worn gear applied.
			var message = PlayerCharacterIirs.ForCharacter(character, worn, maxVitals);

			// A near-zero stored quaternion (the degenerate W=0 default) can't be realised by the avatar; fall back to
			// identity rather than risk it (the live splice path guards the same way).
			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			return BuildWorldEntrySpawn(client, PlayerSpawnBuilder.BuildFromScratch(character.Id, playfield, character.Position, rotation, message, character, morphRegistry));
		}

		// The burst-less world-object packets: each WorldObjects row built via WorldObjectCodec and framed. Built on
		// the row's captured handle + the live playfield instance (the rewrite the live splice path applies).
		// The character-creator (editor) phase: a not-yet-named character is connected. The client instance is
		// the character's real id now, so the unnamed-character state alone identifies the editor flow.

		private static Task ReportDimensionId(INetworkClient client, uint realmId, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceMethods.DispatchReportDimesionID, s => s.WriteString(realmId.ToString())));
		}

		private static Task ReportServerId(INetworkClient client, uint value, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			// Body: [u32 0x54 (fixed header)][u32 value]. The create-flow auth handshake reports value 1.
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceMethods.DispatchReportServerID, s => s.WriteUInt32(0x54u).WriteUInt32(value)));
		}

		private static async Task Send0X201C(INetworkClient client)
		{
			var receiver = client.Endpoint(RpcInterfaces.GameCharInterfaceType);
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

		// The item at `slot` has BOUND to its owner: re-send that cell's Flags word (stat 0) with bit 26 set (#756).
		// Retail sends it as the last message of an equip that binds, after the two 0x17 slot registrations.
		private Task SendItemBound(INetworkClient client, CharacterEquipmentSlot slot, ItemInstance instance)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), SetItemStatCodec.Method,
				SetItemStatCodec.Build(SetItemStats.Bound(ContainerKind.Equipped, client.Session.Character.Id, (uint)slot, instance.Template.WireFlags))));
		}

		private static Task Send0X201CEntry(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, uint prefix, uint state, uint ticket)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceMethods.DispatchSetItemStat,
				b => b.WriteUInt32(prefix).WriteUInt32(client.Session.Character.Id).WriteUInt32(state).WriteUInt32(0x00000017).WriteUInt32(ticket)));
		}
	}
}
