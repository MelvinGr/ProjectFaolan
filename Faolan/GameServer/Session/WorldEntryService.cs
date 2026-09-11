using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Session
{
	/// <summary>
	///     World entry: everything between a connection reaching the world listener and the character standing in the
	///     world - the login / teleport-reentry / create-world ready bursts, the FullChar snapshot, the spawn poll, the
	///     ability/combo/spell grants and the buff + mount restores.
	/// </summary>
	/// <remarks>
	///     A composed service rather than a listener partial because <see cref="CreateFlowService" /> needs the
	///     burst-less entry as an ordinary constructor dependency.
	/// </remarks>
	public sealed class WorldEntryService
	{
		private const uint MountAllowedFlag = 0x1; // Map.PlayfieldFlags bit 0: the playfield permits mounts

		/// <summary>
		///     Tag ids that, when pre-seeded into the FullChar snapshot, suppress the client's auto-opening item-shop browser
		///     (itemshopbrowser_swf). Deliberately empty.
		/// </summary>
		/// <remarks>
		///     The client's level/login milestone Lua (RDB LuaArchive scripts 393/395/396/2239/2240/2287-2298) opens a
		///     Funcom-store upsell via player:OpenSiteInBrowser, each gated on <c>not player:HasTag(X)</c> plus the
		///     PvPSagaBundles tweak. No tweak value skips them all, so the tag gate is the only universal suppressor: tags
		///     6617-6620 cover the i&lt;1 bundles, 7316 and 7416-7421 the i&gt;0 bundles. The client learns a character's
		///     tags from the snapshot sent at entry.
		///     <para>
		///         Retail awards these tags (6617 arrives via DispatchAddTag in three captures, and it is the one captured
		///         tag id that resolves to nothing in the achievement catalog, LDB 55701) only once a bundle has actually
		///         been shown. Stamping them into every login snapshot is our own suppression, not a mirror of retail, so
		///         the list stays empty until that upsell is judged worth suppressing this way.
		///     </para>
		/// </remarks>
		private static readonly uint[] ItemShopSuppressTags = [];

		private readonly ILogger _logger;
		private readonly IDatabaseRepository _database;
		private readonly WorldSimulation _simulation;
		private readonly WorldBroadcaster _broadcaster;
		private readonly QuestBroadcaster _questBroadcaster;
		private readonly NpcBroadcaster _npcs;
		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;
		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly TeleportService _teleport;
		private readonly BuffService _buffs;
		private readonly TortageIntroService _tortageIntro;
		private readonly MountService _mounts;
		private readonly FactionService _factions;
		private readonly EconomyOutbound _economyOutbound;
		private readonly RealmClock _clock;

		public WorldEntryService(ILogger logger, IDatabaseRepository database, WorldSimulation simulation, WorldBroadcaster broadcaster, QuestBroadcaster questBroadcaster,
			NpcBroadcaster npcs, ICsPlayerAgentDispatcher csPlayerAgentDispatcher, IChatServerDispatcher agentServerDispatcher, TeleportService teleport, BuffService buffs, TortageIntroService tortageIntro,
			MountService mounts, FactionService factions, EconomyOutbound economyOutbound, RealmClock clock)
		{
			_logger = logger;
			_database = database;
			_simulation = simulation;
			_broadcaster = broadcaster;
			_questBroadcaster = questBroadcaster;
			_npcs = npcs;
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;
			_agentServerDispatcher = agentServerDispatcher;
			_teleport = teleport;
			_buffs = buffs;
			_tortageIntro = tortageIntro;
			_mounts = mounts;
			_factions = factions;
			_economyOutbound = economyOutbound;
			_clock = clock;
		}

		internal async Task FlushPendingPost205FFollowUp(INetworkClient client)
		{
			if (!client.Session.HasPendingPost205FFollowUp)
				return;

			// Whether this connection wants the login ready burst is a question about its role, so ask EntryMode
			// directly and cover every mode. Two of them must never flush here:
			//   CreateEditor - it is showing the character creator; its ready sequence is the editor burst, sent on
			//                  CharInPlayIIR, and the client keeps polling 0x2020 throughout.
			//   CreateWorld  - it is running the beach/Kalanthes intro. Retail sends its ready burst in answer to
			//                  CharInPlayIIR (fr317 -> fr324), so firing it from a poll lands inside the cutscene:
			//                  the blue screen. SendCreateWorldReadyBurst owns that moment instead.
			switch (client.Session.EntryMode)
			{
				case WorldEntryMode.CreateEditor:
				{
					_logger.LogInformation("Create-editor connection: the post-0x205F follow-up waits for the create handoff, not for this 0x2020 poll.");
					return;
				}

				case WorldEntryMode.CreateWorld:
				{
					_logger.LogInformation("Create-world connection: the ready burst belongs to its CharInPlayIIR, not to this poll.");
					return;
				}

				case WorldEntryMode.Login:
				case WorldEntryMode.TeleportReentry:
				{
					break; // the two modes this burst is for
				}

				default:
				{
					_logger.LogError("Post-0x205F flush on an unhandled entry mode {Mode}: sending the login burst by default, which may be wrong for it.", client.Session.EntryMode);
					break;
				}
			}

			// Past the switch the character is always named: every connection that reaches this point is a Login or a
			// TeleportReentry, both of which want the full existing-character burst.
			_logger.LogInformation("Flushing pending post-0x205F follow-up on this 0x2020.");
			await SendWorldReadyBurst(client);
		}

		/// <summary>
		///     The create-world answer to CharInPlayIIR, record for record as retail sends it (Blablatest create
		///     fr324/325): 0x2024 ReadyForPlayScreen | the ability grants (0x2052) | CharInPlayIIR echo | RegenIIR |
		///     SyncCanSprint, and then the intro cutscene, which the caller starts.
		/// </summary>
		/// <remarks>
		///     Deliberately not the login burst (<see cref="SendWorldReadyBurst" />): that one carries the combo book, the
		///     deferred stat prompt, a quest marker and the post-0x2024 login record stream, none of which retail sends
		///     here, and it lacks the RegenIIR. The create world entry is its own sequence.
		/// </remarks>
		internal async Task SendCreateWorldReadyBurst(INetworkClient client)
		{
			var instance = client.Session.Character.Id;

			await SendReadyForPlayScreen(client);
			await GrantCharacterAbilities(client);

			// The CharInPlayIIR echo (retail fr324: [u32 len][864cfef8][C350][instance][00]).
			await SendCharInPlay(client);

			// RegenIIR (retail fr325). A RegenIIR is a delta the client adds to its own current vitals (RegenCodec), so
			// the value is this character's deficit at this moment, not a constant - zero in all three pools for a
			// freshly created one. The record still rides because it is what starts the client's regen loop. A world
			// entity with no live vitals rides at zero too: a fabricated delta is exactly what this record must not carry.
			var (health, mana, stamina) = _simulation.PlayerWorldEntity(client)?.Vitals.ToRefillDeltas() ?? default;
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(RegenCodec.Build(instance, health, mana, stamina))));

			// SyncCanSprint (0x200C, one byte) - the sprint pool starts available.
			await client.SendAsync(new Rpc(GameEndpoints.ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, instance),
				GameCharInterfaceProjectMethods.DispatchSyncCanSprint, SprintFlagCodec.Build(true)));

		}

		/// <summary>
		///     Everything the client needs to leave the loading screen and stand in the world: the deferred stat prompt,
		///     the quest marker, 0x2024 ReadyForPlayScreen, the ability + combo grants and the post-0x2024 record stream.
		/// </summary>
		/// <remarks>
		///     A login/teleport arms it on the client's 0x205F and flushes on the next 0x2020 poll, while a create-world
		///     connection sends it on CharInPlayIIR - after the intro cutscene, never during it.
		/// </remarks>
		private async Task SendWorldReadyBurst(INetworkClient client)
		{
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(StatCodec.Build(StatIirs.ForCharacter(client.Session.Character.Id, 0x00000013, 0x00000000)))));
			_logger.LogInformation("Sent deferred StatIIR prompt.");

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id).WriteByte(0)));

			// Retail sends 0x2024 ReadyForPlayScreen right after the spawn handshake (0x2020 -> 0x2008 -> 0x2076): it is
			// the "leave the loading screen and enter the world" signal, and without it the client sits on the loading
			// screen forever (Strie 4010 login capture).
			_logger.LogInformation("Existing-character login: sending 0x2024 ReadyForPlayScreen after the spawn handshake.");
			await SendReadyForPlayScreen(client);
			// Retail grants the starting abilities (0x2052) + combo book right after 0x2024, not during world entry
			// (TestAccount necro login capture: abilities at frame 192). Both are granted post-snapshot: neither rides the
			// FullChar snapshot known-list (spells do), so a pre-snapshot grant is discarded by the client's identity
			// reconstruction.
			await GrantCharacterAbilities(client);
			await GrantCharacterCombos(client);

			// In-play. Until this arrives the client keeps the character at inplay=0 and drops every runtime stat update
			// for it: a StatUpdateIIR is received and parsed but never reaches CMsg_StatChange, so the health bar stays
			// frozen at whatever the snapshot seeded while damage lands, and only the RegenIIR deltas (not gated on it)
			// still move the number. Retail sends it in this order - frame 124 of PriestofMitra-login is
			// ValidResurrectionSpots, seven AddSpecialAbility, then CharInPlay.
			await SendCharInPlay(client);
			// The persisted mount, the persisted self-buffs and Shield of the Risen are re-asserted inside the shared
			// world-entry flush (RestoreEntryBuffs), where retail sends them: a re-assert arriving after the client has
			// left the loading screen reads as a new application and fires the client's apply feedback on every login
			// and map change. Learned spells are not re-granted here either: they ride the FullChar snapshot's
			// known-spell list, so the client already knows them when it enters the world.

			// The area banner is driven by the world tick's sub-area crossing check (WorldSimulation.TickAreaBanners):
			// entering a playfield lands the player inside an area and that is the first crossing. Clearing the live
			// area makes the crossing fire again after a teleport/changemap back into an area the player was already
			// standing in.
			if (_simulation.PlayerWorldEntity(client) is { } areaEntity)
				areaEntity.CurrentAreaNameId = 0;

			// The post-0x2024 login record stream (world state / quest / UI) retail sends right after ReadyForPlayScreen
			// + abilities (Strie 4010 login capture). The live player instance is written inline wherever a receiver or
			// body carries it, so no ids are hard-coded.

			var instance = client.Session.Character.Id;

			// "New claims available" glow (CoreClaimIIR sub 0x0009) - pushed only when the account has unseen claims,
			// matching retail (present on a fresh login with new claims, absent once viewed).
			// Timed like the entry itself: the breakdown says whether a gap here is our reads or the client not draining
			// its socket while it loads the map (a SendAsync that blocks on backpressure shows up under "sends").
			var timing = new WorldEntryTiming();
			await ClaimService.SendNewClaimsGlowIfAny(client);
			timing.Mark("claims-glow");

			// DispatchInDungeonPlayfield: the one-byte per-playfield flag that tells the client it is standing in a
			// dungeon. Retail sends it here - both captured `01` frames sit between the ability grants and the
			// CharInPlayIIR echo below, and the `00` one at the same point of an ordinary city login. The classification
			// is GroupFinderActivityCatalog.IsFinderDestination, whose remark carries what the corpus does and does not
			// separate.
			await client.SendAsync(new Rpc(GameEndpoints.ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, instance),
				GameCharInterfaceProjectMethods.DispatchInDungeonPlayfield, DungeonPlayfieldCodec.Build(GroupFinderActivityCatalog.IsFinderDestination(client.Session.Character.MapId))));

			// record 0: 0x00002000, 17-byte body
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CharInPlayCodec.Build(new CharInPlayIIR(instance)))));

			// record 1: 0x0000200C, 1-byte body
			await client.SendAsync(new Rpc(GameEndpoints.ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, instance),
				GameInterfaceMethods.DispatchReportServerID, (byte[])[0x01]));

			// record 2: 0x00002000 - the StartFixed3d destiny step (ScryScriptIIR 0xA36D3B74); the body is
			// [u32 len][step bytes]. Skipped on the create-world connection: the create destiny cutscene overlay
			// (SendCreateCutsceneStep) sends StartFixed3d at IntroDone, and retail sends it once, there - a second copy
			// before the cutscene re-engages the cinematic.
			if (client.Session.EntryMode != WorldEntryMode.CreateWorld)
			{
				var startFixed3d = ScryScriptCodec.Build(instance, "StartFixed3d");
				await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(startFixed3d)));
			}

			if (Global.EnableTutorialOverlay)
			{
				await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
					b => b
						.WriteArray(0x00, 0x00, 0x00, 0x1F, 0xA3, 0x6D, 0x3B, 0x74, 0x00, 0x00, 0xC3, 0x50)
						.WriteUInt32(instance)
						.WriteArray(0x00, 0x00, 0x00, 0x00, 0x0E, 0x08, 0x05, 0x10, 0x00, 0x18, 0xF6, 0xC6, 0x17, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00)));

				var welcomeTutorial = ScryScriptCodec.Build(instance, "WelcomeTutorial");
				await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(welcomeTutorial)));
			}

			// Apply any after-quest world-object effects up-front (a relog after freeing Casilda drops the beach
			// chain-wall collision, so the returning player is not re-blocked by the entry burst's chain dynel), and
			// clear the escortee's captive pose if she is already freed. Both are data-driven and no-ops when not
			// applicable.
			timing.Mark("sends");
			await _questBroadcaster.ApplyQuestWorldEffects(client);
			timing.Mark("quest-world-effects");
			await _tortageIntro.ClearCasildaCaptivePose(client);
			timing.Mark("casilda-pose");

			_logger.LogInformation("Sent the post-0x2024 login records - {Timing}.", timing);

			// No class stat (0x43) is sent: retail never sends it either, and the client derives the in-world class from
			// the character data already delivered.

			// Wallet sync. Retail does not push a 0x2005 at login - the balance rides the FullChar snapshot as
			// CharacterStatId.Money in the stat sheet - so this push is redundant rather than a stand-in. It stays until an
			// in-client login confirms the sheet alone shows the right balance. Reason 0 = a plain sync, not a gain
			// celebration.
			if (client.Session.Character.Money > 0)
				await _economyOutbound.SendUpdateCash(client, client.Session.Character.Money, UpdateCashReason.Spend);

			// Guilded character: set the in-world guild tag (0x2550C99A). Retail sends this once on guild join; for
			// logins the tag normally rides in the spawn data, which our DB spawn does not carry, so it is re-asserted
			// here - the client applies it the same way. After the snapshot, like the wallet sync, so the
			// burst -> snapshot gap stays clean.
			if (client.Session.Character.GuildId is { } guildId)
			{
				var guild = await _database.GetGuild(guildId);
				if (guild != null)
					await client.SendAsync(GameWire.BuildGuildTag(client, client.Session.Character.Id, guild.Name));
			}

			client.Session.HasPendingPost205FFollowUp = false;
			_logger.LogInformation("Cleared pending post-0x205F follow-up flag.");
		}

		/// <summary>
		///     Tells the client its own character is in play. Until it arrives the client keeps the entity at inplay=0 and
		///     drops every runtime stat update for it. Both ready bursts send it; retail puts it right after the ability
		///     grants.
		/// </summary>
		private static Task SendCharInPlay(INetworkClient client)
		{
			var instance = client.Session.Character.Id;
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, instance), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CharInPlayCodec.Build(new CharInPlayIIR(instance)))));
		}

		/// <summary>
		///     Re-grants the character's abilities (0x2052) on login: its persisted per-character set resolved to Ability
		///     rows, or the class-starting kit for a freshly created character with no persisted set. Not derived from the
		///     shortcut bar.
		/// </summary>
		private async Task GrantCharacterAbilities(INetworkClient client)
		{
			var character = client.Session.Character;

			var abilities = await CharacterLoadout.ResolveAbilities(_database, _logger, character);
			// Login is a silent re-grant: the abilities were already learned, so the announce flag is cleared - otherwise
			// the client pops "You have learned a new ability" for the whole kit on every login. Retail does the same:
			// the create-flow grants carry the announce byte 0x01, the per-login re-grants 0x00 (the only differing byte).
			foreach (var ability in abilities)
				await GameWire.SendAddSpecialAbility(client, ability, false);

			_logger.LogInformation("GrantCharacterAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class,
				character.Level);
		}

		/// <summary>
		///     Re-emits the character's combo book on login as ComboBookIIR grants - the combo counterpart to
		///     <see cref="GrantCharacterAbilities" />. The book is the persisted CharacterCombo rows union the class's
		///     combos up to its level from the class-combo access map (ClassCombo); a max-level imported character never
		///     dings, so without this its combo book would be empty. The book is not in the snapshot.
		/// </summary>
		private async Task GrantCharacterCombos(INetworkClient client)
		{
			var character = client.Session.Character;

			var instance = character.Id;

			// ClassCombo is class-keyed, so a caster with no weapon combos does not re-learn Sweep/Slam on every login. It
			// has no rows for casters and ships empty until populated from dumps, so in practice this grants the persisted
			// CharacterCombo rows.
			var comboIds = new HashSet<uint>();
			foreach (var combo in await _database.GetCharacterCombos(character.Id))
				comboIds.Add(combo.ComboId);
			foreach (var combo in await _database.GetClassCombos(character.Level, character.Class))
				comboIds.Add(combo.Id);

			foreach (var comboId in comboIds)
			{
				await WorldIir.Send(client, ComboBookCodec.Build(instance, comboId));
			}

			if (comboIds.Count > 0)
				_logger.LogInformation("GrantCharacterCombos: emitted {Count} combos to '{Name}' (class={Class}, level {Level}).", comboIds.Count, character.Name, character.Class, character.Level);
		}

		/// <summary>
		///     Re-grants the character's learned spells on login - the spell counterpart to
		///     <see cref="GrantCharacterAbilities" />. Retail re-sends a caster's whole learned set via DispatchLearnSpell
		///     on every world entry (NecroL80 always relearns her 5, DemoL80 his 9); the client silently no-ops spells it
		///     already knows. The set is the character's CharacterSpells rows.
		/// </summary>
		private async Task GrantCharacterSpells(INetworkClient client)
		{
			var character = client.Session.Character;

			var spells = await _database.GetCharacterSpells(character.Id);
			foreach (var spell in spells)
				await GameWire.SendLearnSpell(client, spell.SpellId);

			if (spells.Length > 0)
				_logger.LogInformation("GrantCharacterSpells: re-granted {Count} learned spells to '{Name}' (class={Class}).", spells.Length, character.Name, character.Class);
		}

		/// <summary>
		///     The client's world-entry spawn poll, arriving on wire 0x2020 (GameCharAgent method slot 0x20, RPC-catalog
		///     label UpdateQuestMarkerNPC - the login/world flow reuses the opcode). Fires once per spawned entity the
		///     client confirms it built (requestId = that entity's handle).
		/// </summary>
		/// <remarks>
		///     Three jobs, all part of finishing world entry:
		///     <list type="number">
		///         <item>
		///             record the confirmed handle so interest only ever despawns handles the client actually built (a
		///             despawn for an unbuilt handle crashes a still-loading client: pcDynel == null);
		///         </item>
		///         <item>
		///             answer with the 0x2008 SpawnCheck that completes/renders that entity (the dikketest world
		///             connection polls Kalanthes 0x535A0 and gets a SpawnCheck for 0x535A0). Both existing-login and
		///             post-creation world entry need it;
		///         </item>
		///         <item>
		///             drive the ready trigger: on a normal login retail arms the follow-up off the client's 0x205F and
		///             expects 0x2024 ReadyForPlayScreen right after this spawn handshake, so the flush lives here (moving
		///             it strands login on the loading screen). <see cref="FlushPendingPost205FFollowUp" /> no-ops unless
		///             armed, so a create-editor / post-ready poll falls through.
		///         </item>
		///     </list>
		///     Bodies shorter than the 16-byte poll are not spawn polls (other same-opcode traffic) and are only logged.
		/// </remarks>
		internal async Task HandleWorldEntrySpawnPoll(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// The client sends one of these per NPC it loads and waits for each answer, so the handling time here is the
			// player's post-load lag, multiplied by the NPC count. Logged on the summary line.
			var clock = System.Diagnostics.Stopwatch.StartNew();
			if (body.Length < sizeof(uint) * 4)
			{
				_logger.LogBodyDump("GameServer 0x2020 short body", body.ToArray());
				return;
			}

			var ack0 = body.ReadUInt32();
			var requestId = body.ReadUInt32();
			var ack1 = body.ReadUInt32();
			var characterId = body.ReadUInt32();

			var viewer = _simulation.PlayerWorldEntity(client);
			viewer?.Knowledge.Confirmed.Add(requestId);

			if (client.Session.WorldEntered || client.Session.EntryMode == WorldEntryMode.CreateWorld)
				await SendSpawnCheckResponse(client, requestId);

			// The "this entity is alive and idling" assert is sent once, to the client that has just confirmed it built the
			// dynel - retail sends a median of one per actor (37,965 records over 7,969 actors), never a per-tick pulse.
			// Sending it here rather than from the tick is what makes the Confirmed gate structural.
			if (viewer != null && _simulation.World.ZoneForEntity(viewer).TryGet(requestId, out var confirmed) && !confirmed.IsPlayer && !confirmed.IsLoot)
			{
				await NpcBroadcaster.SendNpcIdleAssert(client, confirmed);

				// A mob that was already fighting when this viewer built its dynel would otherwise never get the
				// combat-mode stat: BroadcastNpcCombatMode fires on the engage transition and only reaches viewers who
				// already know the handle. This is the moment the viewer can render it, behind the same gate as the idle
				// assert.
				if (confirmed.InCombat)
					await NpcBroadcaster.SendNpcCombatMode(client, confirmed);
			}

			_logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}, handled in {Ms} ms", ack0, requestId, ack1, characterId, clock.ElapsedMilliseconds);
			_logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}", ack0, requestId, ack1,
				characterId, client.Session.HasPendingPost205FFollowUp);
			await FlushPendingPost205FFollowUp(client);
		}

		/// <summary>
		///     Resolves the resurrection anchor advertised on the login 0x2024: the closest known / AlwaysKnown
		///     RespawnPoint on the character's map (the same resolution SendDeathScreen uses). Falls back to the universal
		///     Tortage Beach default (<see cref="Map.NewPlayerSpawnSpawnPoint" />) when the map defines no respawn point.
		/// </summary>
		private async Task<RespawnPoint> ResolveLoginResurrectionSpot(Character character)
		{
			var points = await _database.GetRespawnPoints(character.MapId);
			if (points.Length == 0)
				return Map.NewPlayerSpawnSpawnPoint;

			var discovered = new HashSet<uint>(await _database.GetDiscoveredRespawnPointIds(character.Id));
			return RespawnResolver.Resolve(character.Position, points, discovered, character.IsCriminal) ?? points[0];
		}

		/// <summary>
		///     Every buff the player should already be wearing when they arrive: the persisted mount, the persisted
		///     self-buffs, and the login shield.
		/// </summary>
		/// <remarks>
		///     Sent immediately after the snapshot + GameTime that end the entry flush, because that is where retail
		///     sends it: in the changemap capture NecroL80_..._login_leave_king_conan_castle-Dismount, riding out of the
		///     no-mount castle produces FullChar -> GameTime -> ride buff -> Shield of the Risen -> Mount/MountSpeed
		///     StatUpd as five adjacent records. A re-assert arriving inside the entry stream is absorbed silently, while
		///     the same record after ready reads as a new application and fires the client's apply feedback.
		///     <para>
		///         One deliberate deviation: retail's sample puts the shield between the ride buff and the mount StatUpd,
		///         while <see cref="RestoreMount" /> emits its buff and its stats together. Splitting the mount restore so
		///         an unrelated buff can interleave would couple two subsystems for a single capture's ordering of
		///         independent IIRs, so the records go out mount-then-shield.
		///     </para>
		/// </remarks>
		private async Task RestoreEntryBuffs(INetworkClient client)
		{
			await RestoreMount(client);
			await _buffs.RestoreBuffs(client);

			// Not on the character's very first entry. Shield of the Risen is newbie protection for someone arriving in
			// the world, and the create-world entry is the intro cutscene, where the player has no control - the buff
			// icon and its "you are protected" line would appear over a cinematic, and the shield would be spent by the
			// first swing of a fight the player has not had yet. Every later login gets it as usual.
			if (client.Session.EntryMode == WorldEntryMode.CreateWorld)
			{
				_logger.LogInformation("Create-world entry for '{Name}': skipping Shield of the Risen (it belongs to a login, not to the intro).", client.Session.Character.Name);
				return;
			}

			await _buffs.SendShieldOfTheRisen(client);
		}

		/// <summary>
		///     Re-seats a persisted mount (Character.MountId) on world entry - login and every teleport/changemap
		///     re-entry - only where the map allows mounts, so riding into a no-mount area dismounts and riding back
		///     re-mounts. No-op on foot or if the stored id no longer resolves.
		/// </summary>
		/// <remarks>
		///     This is a fresh buff apply, which is retail-correct: in the changemap capture
		///     NecroL80_..._login_leave_king_conan_castle-Dismount the entry-restore ride buff is byte-identical to a
		///     player-initiated mount cast apart from the per-application effect id and the trailing nonce (6 bytes of
		///     84). There is no "already active" or "silent" variant of the record, and the FullChar snapshot carries no
		///     active-buff region for one to live in.
		/// </remarks>
		private async Task RestoreMount(INetworkClient client)
		{
			if (client.Session.Character?.MountId is { } mountId)
				await _mounts.RestoreOnWorldEntry(client, mountId, await _database.GetMap(client.Session.Character.MapId), MountAllowedFlag);
		}

		/// <summary>
		///     Builds and sends the entire world entry from structured pieces: the world-model setup, then MapChange,
		///     world objects, the player spawn, the snapshot, the in-range NPC burst, vitals and GameTime, then the entry
		///     buffs and follow-ups.
		/// </summary>
		/// <remarks>
		///     The snapshot tails are beach-shaped (FullCharSnapshotSubClass + WorldState.DefaultPlayfieldToken) for a
		///     map without its own sourcing.
		/// </remarks>
		internal async Task<WorldEntryTiming> SendBurstlessWorldEntry(INetworkClient client, uint mapId)
		{
			var character = client.Session.Character;
			var timing = new WorldEntryTiming();

			// A character entering a minigame map with no started match of theirs on it does not belong there - a relog
			// mid-match (or across a server restart) must not strand them on the PvP map. The entry goes to the match's
			// persisted return location; a character with none (they were already standing on the minigame map when the
			// match formed) falls back to their bound respawn point, else the beach default. Nothing has been sent for
			// this entry yet, so changing the map here is safe - a login client learns its map from the packets this
			// method builds. A reconnect into a live match is untouched.
			if (StartingData.MinigameQueueCapacity.ContainsKey(mapId) && _csPlayerAgentDispatcher.PvpMatchEntryOf(character.Id, mapId) == null)
			{
				uint destinationMap;
				Vector3 destination;
				if (character.PvpReturnMapId is { } pvpReturnMap)
				{
					destinationMap = pvpReturnMap;
					destination = new Vector3(character.PvpReturnX, character.PvpReturnY, character.PvpReturnZ);
				}
				else if (character.BindRespawnPointId is { } bindPoint && await _database.GetRespawnPoint(bindPoint) is { } bound)
				{
					destinationMap = bound.MapId;
					destination = bound.Position;
				}
				else
				{
					destinationMap = Map.TortageBeachId;
					destination = Map.NewPlayerSpawnSpawnPoint.Position;
				}

				character.MapId = destinationMap;
				character.Position = destination;
				await _database.SaveCharacterTransform(character.Id, character.Position, character.Rotation, destinationMap);
				_logger.LogInformation("World entry: '{Name}' entered minigame map {MinigameMap} with no running match - redirected to map {ReturnMap} at {Pos}.",
					character.Name, mapId, destinationMap, character.Position);
				mapId = destinationMap;
			}

			// A consumed or stale return never survives an entry - the live leave path clears its own, so a value
			// still set here has either just been used above or describes a match the character long since left.
			if (character.PvpReturnMapId != null)
			{
				character.PvpReturnMapId = null;
				await _database.SaveCharacterPvpReturn(character.Id, null, default);
			}

			// Nothing has been handed to the client for this entry yet - so the tick's interest sweep must stay off
			// them until the burst below is flushed (see INetworkClientSession.WorldEntryDelivered). Cleared here
			// rather than only initialised, because a re-entry (create-world -> world) reuses the connection.
			client.Session.WorldEntryDelivered = false;

			// Every item the character holds - worn (container Equipped) and carried alike - which is both the four
			// snapshot containers and the spawn's visible gear, from one read.
			// The beach starter quest (10238) is granted once at character creation (CommitCharacterName), never
			// re-seeded here, so a completed/removed quest never reappears; the journal is driven purely from
			// CharacterQuests.
			timing.Mark("minigame-return");
			var heldItems = await _database.GetCharacterItems(character.Id);
			timing.Mark("items");
			// Union in the item-shop-suppression flags (see ItemShopSuppressTags) so the client's shop-open Lua sees
			// HasTag(X)==true and never auto-opens the itemshopbrowser on entry.
			var characterTags = (await _database.GetCharacterTags(character.Id)).Concat(ItemShopSuppressTags).Distinct().OrderBy(t => t).ToArray();
			// The player's active quest ids, for quest-gated world objects (WorldObject.GateQuestId / SpawnsFor).
			// Ungated objects (the default) ignore it.
			var activeQuestIds = (await _database.GetCharacterQuests(character.Id, QuestState.Active)).Select(q => q.QuestId).ToHashSet();
			timing.Mark("tags+active-quests");

			// Populate the shared world model before building so the player entity exists for the spawn build. Which
			// copy (instance) of the map the player joins follows from its instancing mode: register the player first
			// (which picks/keeps their instance), then populate that instance's NPCs.
			var map = await _database.GetMap(mapId) ?? throw new  InvalidOperationException();
			timing.Mark("map");
			// The additive stat bonuses the player carries into the world - equipped gear, the feats they trained, and
			// the feats the client trains for them - so their live vitals seed with all three.
			//
			// A character routinely gets nothing from the trained-feat half, and that is a build, not a gap: FeatIdMaps
			// carries all 6,593 entries of the client's FeatTable_t (2,939 with stat effects, 808 granting HealthMax),
			// and a character that spends every point on class-tree abilities and spell nodes has a true zero here. The
			// auto-trained half is never empty, and is never in a feat list either.
			var statBonuses = await CharacterStatBonuses.For(_database, character);
			timing.Mark("stat-bonuses");
			// The class/level base the character's attributes start from. The client computes this locally (retail
			// sends a flat 10 for every attribute), so it is decoded data, not wire data - see ClassAttributeBaseModule.
			var classBase = await _database.GetClassAttributeBase(character.Class, character.Level);
			if (classBase.Count == 0)
			{
				_logger.LogWarning("World entry: no class attribute base for {Class} at level {Level} (#444) - {Name}'s attributes are gear+buffs only, so regen and mitigation will read low.",
					character.Class, character.Level, character.Name);
			}

			// What the character's trained feats do to their spells - "+35% on Flesh to Worms" and the like. Keyed by
			// spell id (the feat names a line; the fan-out to its ranks is resolved in the query), so the cast path never
			// has to look one up.
			var spellBoosts = await _database.GetCharacterSpellBoosts(character.Id);
			// The handful of the character's spells that override which attribute their damage scales off. The class
			// rule covers the rest, so this is usually empty.
			var spellScaling = await _database.GetCharacterSpellScalingAttributes(character.Id);
			// What the trained feats do to the character's combos - the opening/finishing weapon attacks' damage and
			// crit, plus the whole-combo damage that rides the finisher's spell.
			var comboBonuses = await _database.GetCharacterComboBonuses(character.Id);
			// The procs the trained feats hang off combos and spell lines - resolved here so a landing is a list walk.
			var featProcs = await _database.GetCharacterFeatProcs(character.Id);
			timing.Mark("class-base+spell-mods");

			// Whether the armed teleport picked the epic difficulty - consumed here (the reconnect is a new connection,
			// so the flag rode TeleportService) and threaded into both the instance selection (an epic copy never
			// shares a zone with a normal one) and the MapChange's class word below.
			var epicEntry = _teleport.ConsumeEpicEntry(character.Id);
			// A group map is one copy per team, so the entry needs the team's id, which the CS tier owns. A started
			// custom PvP match likewise owns one copy of its minigame map - both sides land in it - so the entry asks
			// the CS tier for the match the same way it asks for the team.
			var pvpMatch = _csPlayerAgentDispatcher.PvpMatchEntryOf(client.Session.Character.Id, map.Id);
			_simulation.RegisterPlayerInWorld(client, map.InstanceMode, statBonuses, classBase, spellBoosts, spellScaling, comboBonuses,
				_csPlayerAgentDispatcher.TeamIdOf(client.Session.Character.Id), epicEntry, pvpMatch?.MatchId, featProcs);

			// The buffs held over the zone-change handoff, onto the entity the registration just built. The entity is
			// new on every map change (PlayerRegistry is keyed by connection), so anything the previous entity held
			// that nothing persists - a live NPC debuff above all - would otherwise be dropped, leaving the client's
			// icon stuck at 0s with no removal twin ever coming. Restoring them puts the tick back in charge of the
			// expiry.
			//
			// TryAdd, not an assignment: the entry burst can re-apply a buff of its own before this runs, and the
			// carried copy is the staler of the two - it was applied on the far side of the door.
			var carried = _teleport.ConsumeCarriedBuffs(character.Id);
			if (carried.Count > 0 && _simulation.PlayerWorldEntity(client) is { } entered)
			{
				foreach (var (buffId, buff) in carried)
					entered.ActiveBuffs.TryAdd(buffId, buff);

				_logger.LogInformation("World entry: carried {Count} live buff(s) across the zone change for {Name} (#907).", carried.Count, character.Name);
			}
			var instanceId = _simulation.PlayerWorldEntity(client)?.InstanceId ?? WorldState.DefaultInstance;

			// The playfield-instance token is sourced per map (Map.PlayfieldToken), falling back to the beach default
			// for maps with no captured token. Threaded identically through the map-change, snapshot, world objects,
			// spawn transform and the zone, so they agree by construction: the client scopes the character to the
			// playfield it loaded, so a token that disagrees across those packets hides the abilities tab, a spawn
			// stamped with a disagreeing one is silently dropped, and a beach token in a mountable zone reads as the
			// no-mount Tortage beach.
			//
			// Stamped here, on the line after the player is placed, and not further down where it is first used. The
			// moment the player entity is in the zone the world tick can see it and stream spawns to them, and every
			// one of those reads Zone.PlayfieldToken. This entry then spends time in DB work before it reaches its own
			// burst, so a stamp any later ships the zone's minted fallback token to the client, which drops the lot.
			// The zone must never be reachable with a stale token, which is a property of where this line is.
			var mapToken = map.PlayfieldToken;
			var playfieldToken = mapToken != 0
				? mapToken
				: WorldState.DefaultPlayfieldToken;
			_simulation.World.ZoneFor(mapId, instanceId).PlayfieldToken = playfieldToken;

			timing.Mark("place-in-world");
			await _broadcaster.EnsureZoneNpcs(mapId, instanceId, character.Class);
			timing.Mark("zone-npcs");
			// The map's static world objects, loaded into the zone once - the entry + interest passes read them from
			// there instead of re-querying per entry.
			await _broadcaster.EnsureZoneWorldObjects(mapId, instanceId);
			timing.Mark("zone-objects");

			// Warm this map's terrain heightmap + collision mesh off the tick so the first aggro/loot does not stall the
			// game loop with the decompress + grid build: the first touch of a map's bake is 124-184 ms (Tortage Beach
			// 116k triangles, Tortage 196k), once per map per process. Fire-and-forget; the caches are thread-safe.
			_ = Task.Run(() => _simulation.Terrain?.Warm(mapId));

			// Mark this connection as having entered the world. The conversation system (ConversationService), the
			// 0x2020 -> 0x2008 spawn-check and the dialogue-close fallback are all gated on this - without it NPC
			// dialogue silently never opens.
			client.Session.WorldEntered = true;

			// Retail split (byte-exact in the *-create vs *-login captures): a create-world entry carries no quests in
			// the snapshot (empty Quest_c region) and grants the starter quest live afterwards; a login entry carries
			// the character's held quests in the snapshot.
			var isCreateEntry = client.Session.EntryMode == WorldEntryMode.CreateWorld;
			// A create entry carries no quests, so it also carries no progress - the resolver is only ever asked about
			// a goal of a quest in `snapshotQuests`.
			var (snapshotQuests, primaryQuest, solvedTimesOf) = isCreateEntry
				? ([], (QuestDefinition?)null, static (QuestGoalDefinition _) => 0u)
				: await LoadSnapshotQuests(character);
			// The character's learned spells ride the snapshot's known-list so they survive relog and place on the
			// action bar. Seeded at creation (CharacterLoadout), so this is a plain read.
			timing.Mark("snapshot-quests");
			var knownSpellIds = (await _database.GetCharacterSpells(character.Id)).Select(s => s.SpellId).ToArray();
			timing.Mark("spells");
			// The character's captured stat sheet (level/vitals/attributes/PvP counters/ratings), authored into the
			// snapshot's stat block per character; empty for a freshly created character (skeleton + patches then).
			// Filtered to the sheet's own 91 ids: CharacterStats also holds stats that are not part of the login
			// snapshot - the daily-challenge counters (ids 1000066+) land there the first time a quest completes - and
			// a sheet of any other size fails the guard in the builder. Retail pushes those on the live StatUpdate
			// channel; every captured login sheet is exactly the 91.
			var statSheet = (await _database.GetCharacterStats(character.Id))
				.Where(s => SubClassStatBlock.SheetStatIds.Contains((uint)s.StatId))
				.Select(s => (s.StatId, s.Value)).ToArray();
			timing.Mark("stat-sheet");
			// The Completed tab rides the snapshot too; a create-world entry has none.
			var completedJournal = isCreateEntry
				? []
				: await LoadCompletedJournal(character);
			timing.Mark("completed-journal");

			// The quest index needs, per active quest, its line's 4CC and that line's full quest catalog ordered by
			// JournalSequence - neither is per-quest data.
			var (indexLines, lineCatalog) = await LoadLineCatalog(snapshotQuests);
			// The journal records' reward lists carry each rewarded item's TEA key, read off its Item row.
			var rewardItems = await _database.GetItemsByTemplate(snapshotQuests.SelectMany(q => q.Rewards.Select(r => r.ItemTemplateId))
				.Concat(indexLines.Values.SelectMany(l => l.Rewards.Select(r => r.ItemTemplateId))).ToArray());
			var instance = client.Session.Character.Id;
			// Every item renders: the token is harvested when a capture carried one and minted otherwise
			// (AcgHash.ForWire), so a `Hash: not 0` gate here would drop exactly the claim/AH items this is meant to
			// deliver.
			var typedRoot = FullCharacterRoots.ForCharacter(characterTags, FullCharacterRoots.FullExpansionFlags, FullCharacterRoots.FullSubscriptionFlags, false, true,
				FullCharacterRoots.Containers(instance, heldItems));
			// The snapshot's current-side vitals are the live ceiling (gear included), the same value the vitals push
			// after it carries - so the sheet does not flick from one max to the other on entry.
			var liveMax = _simulation.PlayerWorldEntity(client)!.Vitals.ToMaxStats();
			var tails = SnapshotTails.FromScratch(snapshotQuests, primaryQuest!, client.Session.Character, liveMax, instance, playfieldToken, knownSpellIds, statSheet, completedJournal,
				indexLines, lineCatalog, solvedTimesOf, id => rewardItems.GetValueOrDefault(id));
			timing.Mark("line-catalog+rewards");
			var snapshot = GameWire.BuildWorldEntrySnapshot(client, typedRoot, tails);
			timing.Mark("build-snapshot");

			var toSend = new List<Rpc>
			{
				// If this copy of the playfield is a team's, the proxy names the team as its owner rather than the
				// player. Asked of the zone, so every member is told the same owner.
				GameWire.BuildMapChange(client, map!, playfieldToken,
					_simulation.PlayerWorldEntity(client) is { } placed ? _simulation.World.TeamOwning(placed.MapId, placed.InstanceId) : null, epicEntry)
			};

			// The MapChange is the one place a player is told which playfield they are in, so it is also where their
			// ~Playfield chat channel has to follow - the channel's id is the map id, and chat routing is the client's
			// job, so a stale subscription means hearing the playfield you left and not the one you are in. The call is
			// idempotent, so re-entering the same map costs nothing.
			if (client.Session.Character is { } chatCharacter)
				await _agentServerDispatcher.RefreshPlayfieldChannel(chatCharacter.Id, chatCharacter.MapId);

			// World objects are interest-streamed, not dumped: entry sends the ones within range and movement streams
			// the rest, as retail does - in Hoxxling_..._gameplay2 only 58 of 158 object handles arrive at entry, the
			// last one 687s later. They keep their retail position here (early in the stream, before the snapshot);
			// BroadcastPlayerMove takes over from there.
			var (worldObjectRecords, worldObjectTotal) = _broadcaster.BuildEntryWorldObjects(client, _simulation.PlayerWorldEntity(client)!, activeQuestIds);
			toSend.AddRange(worldObjectRecords);

			// The worn half of the same read: the spawn shows what the character is wearing, and it must be the same set
			// the snapshot's Equipped container carries or the avatar and the paper doll disagree.
			var worn = heldItems
				.Where(i => i.Container == ContainerKind.Equipped && Enum.IsDefined((CharacterEquipmentSlot)i.Slot))
				.ToDictionary(i => (CharacterEquipmentSlot)i.Slot);
			toSend.Add(BuildBurstlessPlayerSpawn(client, worn, playfieldToken, await _database.GetMorphTargetRegistry(), liveMax));

			toSend.Add(snapshot);

			// If the player logs in mid-escort (an escort stage active), resume the escortee's follow now - the live
			// grant trigger only fires on the talk itself, so without this a relog leaves her at the cage. Driven off
			// the Escort goal + its escortee template id, not a hard-coded quest.
			//
			// Before the entry burst, and that ordering is load-bearing: the burst is where the escortee's spawn record
			// is built, and a freed escortee is described by a different body (ordinary idle, walk mode) chosen on
			// `Following`. Resumed after the burst, her record is built as still-captive and she comes back chained on
			// every relog no matter what is sent afterwards.
			foreach (var (_, qdef) in await _database.GetCharacterQuestsWithDefinitions(character.Id, QuestState.Active))
			{
				if (qdef.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort) is { TrailingRefId: not 0 } escort)
				{
					_npcs.SetNpcFollowByTemplate(mapId, escort.TrailingRefId, client.Session.Character.Id, true);
					break;
				}
			}

			// The in-range NPCs ride the entry burst (before the client's first move), matching retail's shape (fr64-75
			// in the Hoxxling beach login capture) rather than a post-load interest volley. BuildEntryNpcBurst seeds them
			// into the player's Known set, so the interest layer (BroadcastPlayerMove) then streams only the delta as the
			// player walks, and the initial set is never culled mid-load. The markers / captive pose for these NPCs are
			// separate IIRs and ride SendEntryNpcFollowups after the flush.
			timing.Mark("map-change+objects+player-spawn+quests");
			var (entryNpcRecords, entryBurstNpcs) = await _broadcaster.BuildEntryNpcBurst(client);
			timing.Mark("npc-burst");
			toSend.AddRange(entryNpcRecords);

			toSend.Add(GameWire.BuildVitals(client, _simulation.GetOrAddPlayerVitals(client)));
			toSend.Add(GameWire.BuildGameTime(client, DateTimeOffset.UtcNow, _clock.OffsetGameSeconds, _clock.Rate));

			foreach (var rpc in toSend)
				await client.SendAsync(rpc);
			timing.Mark("flush");

			// The client now has the world - MapChange first, then the objects/spawn/snapshot/NPC burst - so the tick's
			// interest sweep may start streaming to it. Until this point the sweep would have been the first sender to
			// reach the client, with a spawn for a playfield it had not loaded yet.
			client.Session.WorldEntryDelivered = true;

			await RestoreEntryBuffs(client);
			timing.Mark("restore-buffs");

			// The step breakdown rides the line: every real entry in faolan.log is its own profile.
			_logger.LogInformation("Burst-less world entry (map={Map}, '{Name}'): sent {Count} constructed packets ({Npcs} in-range NPCs, {Objects}/{ObjectTotal} in-range world objects) - {Timing}.", mapId,
				character.Name, toSend.Count, entryBurstNpcs.Count, worldObjectRecords.Count, worldObjectTotal, timing);

			// The per-NPC quest markers / captive pose for the burst NPCs (separate IIRs, after the spawn records).
			await _broadcaster.SendEntryNpcFollowups(client, entryBurstNpcs);
			timing.Mark("npc-followups");

			// Spawn this player to nearby other players (its NPC set already rode the burst above). The quest journal is
			// not re-sent here: retail ships both tabs in the login snapshot and sends no quest packet at all until the
			// first in-session accept, so a live "restore" burst would be a fabrication.
			await _broadcaster.BroadcastPlayerEntry(client);

			// Entering a started custom match's minigame map - tell the client the match clock. The Flash API's
			// GetTimeLeft/GetMinigameStatus compute off the match start this sets (i_nSecondsSinceStarted, so a member
			// zoning in late sees the running clock, not a fresh one). Client-recovered, uncaptured - like the whole
			// match lifecycle (PvpMatchService).
			if (pvpMatch is { } startedMatch)
			{
				await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchMinigameMatchStarted,
					s => s.WriteUInt32(startedMatch.SecondsSinceStarted)));
				_logger.LogInformation("World entry: '{Name}' entered PvP match {MatchId}'s minigame map (clock at {Seconds}s).", character.Name, startedMatch.MatchId,
					startedMatch.SecondsSinceStarted);
			}

			// On a create-world entry the snapshot carried no quests (empty region above), so deliver the starter quest
			// live exactly as retail does: QuestLogList (J-panel) then QuestFullUpdate (detail). It was persisted at
			// character creation, so it rides the snapshot on every later login, and nothing re-sends it live there.
			if (isCreateEntry)
			{
				// The starter quest is seed data every database carries; without it a new character enters the world with
				// no first objective and nothing to explain why, so a missing definition is corruption, not a case.
				var beachDef = await _database.GetQuestDefinition(QuestContent.BeachStarterQuestId)
					?? throw new InvalidOperationException($"The beach starter quest {QuestContent.BeachStarterQuestId} has no QuestDefinition - it is seed data, so this database is incomplete.");
				await QuestBroadcaster.SendQuestLogList(client, beachDef, [QuestContent.BeachStarterQuestId]);
				await _questBroadcaster.SendQuestFullUpdate(client, beachDef);
				_logger.LogInformation("Create-world entry: delivered starter quest {QuestId} live to '{Name}'.", QuestContent.BeachStarterQuestId, character.Name);
			}

			timing.Mark("broadcast-entry");

			return timing;
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
				// (the death/release screen) - at login the client consumes it silently to set the resurrection anchor. The
				// body is built from the character's actual resurrection point (the same closest-known/AlwaysKnown
				// resolution SendDeathScreen does), so the anchor is per character and per map and advertises the real
				// RespawnPoint.Id (the id RespawnMe echoes back). Falls back to the universal Tortage Beach default when the
				// map defines no point.
				var spot = await ResolveLoginResurrectionSpot(client.Session.Character);
				body = ValidResurrectionSpotLists.Known(client.Session.Character.Position, [spot]);
			}

			await client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceMethods.DispatchValidResurrectionSpots, ValidResurrectionSpotsCodec.Build(body)));
		}

		/// <summary>
		///     Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: the body is
		///     0x0000C350 + the polled entity handle + a status byte.
		/// </summary>
		private async Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			// The poll reply doubles as the NPC quest marker, computed from the player's quest state for the polled
			// handle: non-giver / not-in-view -> 0x00 (still confirms the spawn). Capture-matched: the poll reply is 0x02
			// before accept and 0x00 after (Ranochar frames 125 -> 698).
			var status = await _questBroadcaster.QuestMarkerStateForHandle(client, handle);

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteByte(status)));
		}

		/// <summary>
		///     The single existing-character world entry used by both login and the teleport GM command: the 0x201c
		///     channel handshake, the spell re-grant, then the character's current-map world entry built from the DB.
		///     The caller has already set Character.MapId/Position.
		/// </summary>
		internal async Task SendWorldEntry(INetworkClient client)
		{
			await Send0X201C(client);

			// The learned spells (DispatchLearnSpell 0x200A) are re-granted before the world snapshot, so the client
			// absorbs them as part of the initial load and stays silent - retail sends them pre-entry (NecroL80 login:
			// grants at frame 59, the 0x737A6DF9 snapshot at frame 152). Granted post-ready, the client treats each as a
			// fresh learn and toasts "You have learned a new spell" on every login. Abilities (0x2052) stay post-0x2024;
			// they do not toast.
			await GrantCharacterSpells(client);

			var mapId = client.Session.Character.MapId;

			await SendBurstlessWorldEntry(client, mapId);

			// Restore the character's Khitai faction standings. Sent after the world entry for the same reason abilities
			// are - they draw into a window rather than into the initial load, so nothing toasts - and only for the
			// factions the character has a row for: a faction at neutral has no row and needs no message, since zero is
			// what the client shows for one it has never been told about.
			await _factions.SendAll(client);
		}

		/// <summary>
		///     The complete burst-less player FA015807 spawn packet. Entry is a pure read of the scaffold: the rows are
		///     seeded at creation (CreateFlowService.CommitCharacterName), where the class and editor body they are
		///     generated from are first known, and a character without them throws in the builder. One character object
		///     throughout - the session copy carries the scaffold rows (WithSessionNavigations) - so identity, transform
		///     and scaffold cannot disagree.
		/// </summary>
		private static Rpc BuildBurstlessPlayerSpawn(INetworkClient client, IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance> worn, uint playfield,
			IReadOnlyDictionary<string, MorphTarget> morphRegistry,
			CharacterMaxStats maxVitals)
		{
			var character = client.Session.Character;
			// The CharacterIIR: the fixed render template with this character's name/identity/looks/worn gear applied.
			var message = PlayerCharacterIirs.ForCharacter(character, worn, maxVitals);

			// A near-zero stored quaternion (the degenerate W=0 default) cannot be realised by the avatar; fall back to
			// identity.
			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			return GameWire.BuildWorldEntrySpawn(client, PlayerSpawnBuilder.BuildFromScratch(character.Id, playfield, character.Position, rotation, message, character, morphRegistry));
		}

		internal static Task ReportDimensionId(INetworkClient client, uint realmId, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceMethods.DispatchReportDimesionID, s => s.WriteString(realmId.ToString())));
		}

		internal static Task ReportServerId(INetworkClient client, uint value, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			// Body: [u32 0x54 (fixed header)][u32 value]. The create-flow auth handshake reports value 1.
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceMethods.DispatchReportServerID, s => s.WriteUInt32(0x54u).WriteUInt32(value)));
		}

		private static async Task Send0X201C(INetworkClient client)
		{
			var receiver = client.Endpoint(RpcInterfaces.GameCharInterfaceType);
			var ticketBase = 0x00160000u + ((uint)Environment.TickCount & 0xFFFF);

			// Retail (Strie login) sends five channel registrations before the spawn burst: four with the 0x65 prefix
			// and state ids {5, 6, 0x10, 0x12}, then a final 0x68 prefix with state 1. Fewer than these leave the client
			// stuck before it ever starts polling spawns (0x2020).
			await Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000005, ticketBase);
			await Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000006, ticketBase + 1);
			await Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000010, ticketBase + 2);
			await Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000012, ticketBase + 3);
			await Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, receiver, 0x00000068, 0x00000001, ticketBase + 4);
		}

		/// <summary>
		///     The item at <paramref name="slot" /> has bound to its owner: re-sends that cell's Flags word (stat 0) with
		///     bit 26 set. Retail sends it as the last message of an equip that binds, after the two 0x17 slot
		///     registrations.
		/// </summary>
		internal static Task SendItemBound(INetworkClient client, CharacterEquipmentSlot slot, ItemInstance instance)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), SetItemStatCodec.Method,
				SetItemStatCodec.Build(SetItemStats.Bound(ContainerKind.Equipped, client.Session.Character.Id, (uint)slot, instance.Template.WireFlags))));
		}

		internal static Task Send0X201CEntry(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, uint prefix, uint state, uint ticket)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceMethods.DispatchSetItemStat,
				b => b.WriteUInt32(prefix).WriteUInt32(client.Session.Character.Id).WriteUInt32(state).WriteUInt32(0x00000017).WriteUInt32(ticket)));
		}

		/// <summary>
		///     The line rows + line quest catalogs the quest index needs for a set of active quests. The catalog is every
		///     quest of the line ordered by JournalSequence - static line data, not the character's journal: a freshly
		///     created character carries its line's whole list in the captures.
		/// </summary>
		private async Task<(Dictionary<uint, QuestLine> Lines, Dictionary<uint, IReadOnlyList<QuestDefinition>> Catalog)> LoadLineCatalog(IReadOnlyList<QuestDefinition> quests)
		{
			var lines = new Dictionary<uint, QuestLine>();
			var catalog = new Dictionary<uint, IReadOnlyList<QuestDefinition>>();
			if (quests.Count == 0)
				return (lines, catalog);

			var allLines = (await _database.GetQuestLines()).ToDictionary(a => a.QuestLineId);
			var lineIds = quests.Select(q => q.QuestLineId).Distinct().ToArray();
			// One read for every line's catalog rather than a query per line.
			var catalogs = await _database.GetQuestDefinitionsByLines(lineIds);
			foreach (var lineId in lineIds)
			{
				if (allLines.TryGetValue(lineId, out var line))
					lines[lineId] = line;

				catalog[lineId] = catalogs.GetValueOrDefault(lineId, []);
			}

			return (lines, catalog);
		}

		/// <summary>
		///     The character's completed-quest journal, grouped by quest line - the Completed tab's contents. Retail ships
		///     this in the login snapshot and sends no quest packets at all before the first in-session accept, so this is
		///     the only path that populates that tab.
		/// </summary>
		/// <remarks>
		///     The order is ours to choose: the wire order is per character in the captures, but the client re-sorts for
		///     display (retail renders alphabetically while the wire does not), so a stable lineId/questId ordering is
		///     enough. A quest whose line has no row, or that has no definition, is skipped rather than guessed at.
		/// </remarks>
		private async Task<List<(QuestLine Line, IReadOnlyList<QuestDefinition> Quests)>> LoadCompletedJournal(Character character)
		{
			var groups = new List<(QuestLine, IReadOnlyList<QuestDefinition>)>();

			var completed = (await _database.GetCharacterQuests(character.Id, QuestState.Completed)).ToList();
			if (completed.Count == 0)
				return groups;

			// One batched, lean read: the tab renders a title and a sub-line per quest under a line's three names, so
			// the read carries each definition's TextRefs and the lines those quests sit on - not the active journal's
			// whole graph (goals, regions, vertices, rewards), which for a character with hundreds of completed quests
			// costs seconds per login.
			var (definitionRows, lineRows) = await _database.GetCompletedJournal([.. completed.Select(q => q.QuestId)]);
			var lines = lineRows.ToDictionary(a => a.QuestLineId);
			var definitions = definitionRows.ToDictionary(d => d.QuestId);
			var byLine = new SortedDictionary<uint, List<QuestDefinition>>();

			foreach (var cq in completed)
			{
				if (!definitions.TryGetValue(cq.QuestId, out var def) || def.QuestLineId == 0 || !lines.ContainsKey(def.QuestLineId))
					continue;

				(byLine.TryGetValue(def.QuestLineId, out var list)
					? list
					: byLine[def.QuestLineId] = []).Add(def);
			}

			foreach (var (lineId, defs) in byLine)
				groups.Add((lines[lineId], defs.OrderBy(d => d.QuestId).ToList()));

			return groups;
		}

		/// <summary>
		///     The player's snapshot-eligible held quests + the primary (tracked) quest, plus the character's per-goal
		///     progress resolver.
		/// </summary>
		/// <remarks>
		///     A held quest joins only if its definition is snapshot-eligible (Quest_c scalars seeded + the 5 header text
		///     refs) - else the tail builder would throw. An ineligible held quest is simply absent from the journal:
		///     there is no live restore path, so widening <see cref="SnapshotTails.IsSnapshotEligible" /> is what makes
		///     such a quest appear.
		///     <para>
		///         The goal record's first reserved word is <c>QuestGoal_c::SolvedTimes</c> - the "3 of 7" the journal
		///         shows - and it is per-character state, so the snapshot sources it from the character rather than from
		///         the definition row. Progress lives in two places, as QuestProgressionService reads it: a quest's
		///         primary goal keeps the scalar on CharacterQuest, and only its secondary goals get CharacterQuestGoal
		///         rows.
		///     </para>
		/// </remarks>
		private async Task<(List<QuestDefinition> Quests, QuestDefinition? Primary, Func<QuestGoalDefinition, uint> SolvedTimesOf)> LoadSnapshotQuests(Character character)
		{
			var quests = new List<QuestDefinition>();

			// The journal is exactly the character's active, snapshot-eligible quests - no quest is forced in, and a
			// completed/abandoned quest never lingers as a phantom.
			var lineRows = (await _database.GetQuestLines()).ToDictionary(a => a.QuestLineId);
			// Batched like the completed journal. The journal order is the character's own CharacterQuests order - it
			// picks the primary (tracked) quest below - so the rows are walked in that order and only the definitions
			// behind them are fetched in one go.
			var active = await _database.GetCharacterQuests(character.Id, QuestState.Active);
			var definitions = (await _database.GetQuestDefinitions([.. active.Select(q => q.QuestId)])).ToDictionary(d => d.QuestId);
			// The gate also needs each line's length: the index block states the held quest's step number against it,
			// and a multi-step line no capture has ordered cannot state one. Open work tracked in Gitea #926.
			var lineSteps = await _database.GetQuestDefinitionsByLines([.. definitions.Values.Select(d => d.QuestLineId).Distinct()]);
			foreach (var cq in active)
			{
				if (definitions.TryGetValue(cq.QuestId, out var def) && lineRows.TryGetValue(def.QuestLineId, out var line)
				    && SnapshotTails.IsSnapshotEligible(def, line, lineSteps.TryGetValue(def.QuestLineId, out var steps) ? steps.Length : 0))
					quests.Add(def);
			}

			// The tracked (primary) quest owns the snapshot's objective region + index terminal: the beach starter when
			// held, else the first held quest (null only when the character holds none -> empty region).
			var primary = quests.FirstOrDefault(q => q.QuestId == QuestContent.BeachStarterQuestId) ?? quests.FirstOrDefault();

			// One query for every active quest's secondary-goal rows, and the primary-goal scalar off the rows already
			// loaded above - so the resolver below is pure lookup and the journal build stays synchronous.
			var secondary = (await _database.GetCharacterQuestGoals(character.Id)).ToDictionary(g => (g.QuestId, g.GoalId), g => g.Progress);
			var primaryProgress = active.ToDictionary(q => q.QuestId, q => q.GoalProgress);

			uint SolvedTimesOf(QuestGoalDefinition goal)
			{
				if (secondary.TryGetValue((goal.QuestDefinitionId, goal.GoalId), out var p))
					return p;

				// The quest's first goal is the one whose progress lives on CharacterQuest - the same primary/secondary
				// split QuestProgressionService.IsPrimaryGoal applies, and it has to agree with it or a quest's two
				// goals would read each other's counters.
				return definitions.TryGetValue(goal.QuestDefinitionId, out var d)
				       && QuestProgress.InPlayOrder(d.Goals).FirstOrDefault()?.GoalId == goal.GoalId
				       && primaryProgress.TryGetValue(goal.QuestDefinitionId, out var pp)
					? pp
					: 0;
			}

			return (quests, primary, SolvedTimesOf);
		}
	}
}
