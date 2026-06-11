using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener : Server<ConanPacket, ConanPacketFactory, ConanMessageFramer>
	{
		// Character ids that have just been named in the create-editor and are about to open their FIRST world
		// connection (the second GameServer connection, after the 0x2002 enter-world handoff). The id is added
		// when the name is committed and removed (consumed) on the world Hello, which is how that connection is
		// recognised as the create-world entry (and given the intro/cutscene) rather than a normal login. Static
		// because the editor and world connections are different sessions but hit the same listener instance.
		private static readonly HashSet<uint> PendingCreateWorldEntry = [];
		private readonly IAgentServerDispatcher _agentServerDispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// The live in-memory world (docs/server-architecture-roadmap.md). Players are registered on world entry,
		// their position updated on Move, and removed on disconnect. Currently bookkeeping + interest queries; the
		// interest-managed cross-player broadcast (so two players see each other) is the next step.
		private readonly World.WorldState _world = new World.WorldState();
		private readonly System.Collections.Concurrent.ConcurrentDictionary<INetworkClient, World.WorldEntity> _playerEntities =
			new System.Collections.Concurrent.ConcurrentDictionary<INetworkClient, World.WorldEntity>();

		// Maps whose NPCs have been loaded into the world model (once per map, on first entry).
		private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _npcLoadedZones =
			new System.Collections.Concurrent.ConcurrentDictionary<uint, byte>();

		// How far a player perceives other players (world-entity interest radius; wider than say range).
		private const float PlayerInterestRange = 150f;

		// Periodic world-state autosave cadence (crash safety; logout/teleport/.save are the event-driven saves).
		private const int AutoSaveIntervalMinutes = 5;

		// Game-loop cadence. 1 Hz is plenty for the current behaviour (vitals regen); raise it (with dt-scaling and
		// per-behaviour accumulators) when NPC AI/movement lands, which needs ~10-20 Hz.
		private const double GameTickSeconds = 1.0;

		// Background loops (game tick + autosave) and their lifetime token. Held (not fire-and-forget) so StopAsync can
		// signal them and await a clean shutdown, and so any unobserved fault surfaces instead of being swallowed.
		private CancellationTokenSource _loopCts;
		private Task _gameTickTask = Task.CompletedTask;
		private Task _autoSaveTask = Task.CompletedTask;

		public GameServerListener(ILogger logger, IConfiguration configuration, IDatabaseRepository database, ushort port, IAgentServerDispatcher agentServerDispatcher, IGameServerDispatcher gameServerDispatcher) : base(port, logger, configuration, database,
			new ConanOutgoingPacketLogger(logger, "GameServer", typeof(GameServerRespondOpcodes), typeof(GameServer0X2000RespondsOpcodes)))
		{
			_agentServerDispatcher = agentServerDispatcher;
			_gameServerDispatcher = gameServerDispatcher;

			InitGmCommands();
		}

		// Local-say range in world units. Built-NPC tests showed a +4-unit offset renders "beside" the player, so
		// units are ~metres; ~50 is a generous "nearby" for /say. Tunable; same-map is the hard filter either way.
		private const float SayRangeUnits = 50f;

		// The player's live world entity for this connection (the authoritative transform), or null if not yet in the
		// world. Registered on world entry; the live transform lives here, not on the persisted Character.
		private World.WorldEntity PlayerWorldEntity(INetworkClient client)
		{
			return _playerEntities.TryGetValue(client, out var entity) ? entity : null;
		}

		// The character ids that should hear a proximity "say" from `sender`: same-map players within SayRangeUnits of
		// the sender, plus the sender (you always hear yourself). Resolved against the live world model (the
		// authoritative positions); the in-range ids are handed to the AgentServer for delivery.
		private IReadOnlyCollection<uint> SayRecipients(INetworkClient sender)
		{
			var ids = new HashSet<uint>();
			var entity = PlayerWorldEntity(sender);
			if (entity == null)
			{
				// Not in the world model yet - fall back to just the sender so they still see their own message.
				if (sender.Session?.Character != null)
					ids.Add(sender.Session.Character.Id);
				return ids;
			}

			ids.Add(entity.Handle);
			foreach (var near in _world.ZoneFor(entity.MapId).PlayersInRange(entity.Position, SayRangeUnits, entity.Handle))
				ids.Add(near.Handle);

			return ids;
		}

		// Registers (or relocates) the player in the live world after world entry, SEEDING the authoritative WorldEntity
		// transform from the persisted Character (the loaded spawn point / teleport target). After this the WorldEntity
		// is the source of truth for live position; the Character is only re-read on a fresh entry/teleport. Idempotent.
		private void RegisterPlayerInWorld(INetworkClient client)
		{
			var character = client.Session?.Character;
			if (character == null)
				return;

			var handle = client.Session.Account.ClientInstance;
			var entity = _playerEntities.GetOrAdd(client, _ => new World.WorldEntity(handle, character.MapId, true, character.Name ?? $"Character{handle}", client));
			var zone = _world.Place(entity, character.MapId, character.Position, character.Rotation);
			var nearby = zone.PlayersInRange(character.Position, PlayerInterestRange, handle).Count();
			Logger.LogInformation("World: player {Name} (0x{Handle:X8}) entered map {Map} at {Pos}; {Nearby} other player(s) in range.", entity.Name, handle, character.MapId, character.Position, nearby);
		}

		protected override void ClientDisconnected(INetworkClient client)
		{
			if (_playerEntities.TryRemove(client, out var entity))
			{
				// Tell everyone who could see this player that it left (n3ToClientQuit), then drop it from the zone.
				// Fire-and-forget: the connection is going away so we can't await. Despawn first so ResolveRemove still
				// sees the leaver's viewers.
				_ = BroadcastPlayerDespawn(entity);
				_world.Remove(entity);
				// Persist the player's final live state - movement isn't otherwise saved, so without this a logout
				// loses where they were. Fire-and-forget: the connection is going away so we can't await; logged.
				_ = SavePlayerState(client, entity);
				Logger.LogInformation("World: player {Name} (0x{Handle:X8}) left map {Map}.", entity.Name, entity.Handle, entity.MapId);
			}

			base.ClientDisconnected(client);
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			_gameServerDispatcher.Register(this);
			// Own lifetime token (linked to startup) so StopAsync can cancel the loops independently of any single
			// connection. Tasks are stored so they can be awaited on shutdown.
			_loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_autoSaveTask = AutoSaveLoop(_loopCts.Token); // periodic crash-safety flush of the live world state
			_gameTickTask = GameTickLoop(_loopCts.Token); // the simulation tick (regen now; NPC AI/movement later)
			return base.StartAsync(cancellationToken);
		}

		// The server game loop: a fixed-cadence tick that advances the world model and runs time-based behaviour
		// (docs/server-architecture-roadmap.md). Today it advances the (player-only) world model - a no-op - and
		// regenerates connected players' vitals; NPC AI/movement/respawn join it as those land. Cancelled on shutdown.
		private async Task GameTickLoop(CancellationToken cancellationToken)
		{
			using var timer = new System.Threading.PeriodicTimer(System.TimeSpan.FromSeconds(GameTickSeconds));
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					foreach (var respawned in _world.Tick(GameTickSeconds))
						await BroadcastNpcRespawn(respawned);
					await RegenerateConnectedPlayers();
				}
			}
			catch (System.OperationCanceledException)
			{
				// server shutting down
			}
			catch (System.Exception ex)
			{
				Logger.LogError(ex, "Game tick loop failed.");
			}
		}

		// Regenerates each connected player's vitals one tick's worth and pushes a 0x96B8DC59 update only when a pool
		// actually rose (a player at full HP/stamina/mana costs nothing). Rates are provisional fractions of max.
		private async Task RegenerateConnectedPlayers()
		{
			foreach (var pair in _playerEntities)
			{
				var client = pair.Key;
				if (client.Session?.Character == null)
					continue;

				var vitals = GetOrAddPlayerVitals(client);
				var changed = vitals.Regenerate(
					RegenAmount(vitals.MaxHealth, 0.05), // ~5% hp / sec (provisional)
					RegenAmount(vitals.MaxStamina, 0.10), // ~10% stamina / sec
					RegenAmount(vitals.MaxMana, 0.05)); // ~5% mana / sec
				if (changed)
					await SendStatUpdate(client, vitals.ToVitalsBlock(), client.Session.Account.ClientInstance);
			}
		}

		private static uint RegenAmount(uint max, double fraction)
		{
			var amount = (uint)(max * fraction);
			return amount > 0
				? amount
				: 1;
		}

		// Periodically flushes the live world state to the DB so a crash loses at most one interval. Persistence is
		// otherwise event-driven (disconnect, teleport, .save) and needs no game loop; this is just a cheap timer
		// (docs/server-architecture-roadmap.md). The simulation tick, when it lands, can absorb this.
		private async Task AutoSaveLoop(CancellationToken cancellationToken)
		{
			using var timer = new System.Threading.PeriodicTimer(System.TimeSpan.FromMinutes(AutoSaveIntervalMinutes));
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					var saved = await FlushWorldState();
					if (saved > 0)
						Logger.LogInformation("Autosave: flushed {Count} player(s)' live world state.", saved);
				}
			}
			catch (System.OperationCanceledException)
			{
				// server shutting down
			}
			catch (System.Exception ex)
			{
				Logger.LogError(ex, "Autosave loop failed.");
			}
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			_gameServerDispatcher.Unregister(this);

			// Signal the background loops and await their (clean) exit so shutdown doesn't race a mid-tick save. The
			// loops swallow their own OperationCanceledException, so WhenAll completes normally; bounded by the host's
			// shutdown token either way.
			if (_loopCts != null)
				await _loopCts.CancelAsync();
			await Task.WhenAll(_gameTickTask, _autoSaveTask);
			_loopCts?.Dispose();

			await base.StopAsync(cancellationToken);
		}

		protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			var opcode = (GameServerOpcodes)packet.Opcode;
			client.Session.GameServerInboundPacketSequence++;

			if (opcode is not (GameServerOpcodes.Ping or GameServerOpcodes.LatencyCheck))
				LogPacketReceived(packet, opcode);

			switch (opcode)
			{
				case GameServerOpcodes.Hello:
				{
					// 0x2003 is overloaded: the connection Hello carries clientInstance + accountId + version
					// (>= 12 bytes), but in-world the client also sends a 4-byte 0x2003 (a uint32 entity handle)
					// as it streams entities into view. Retail sends no per-packet reply, so just note it and
					// move on - parsing it as a Hello would read past the end of the body and drop the client.
					if (packet.Body.Length - packet.Body.Position < 12)
					{
						var entityHandle = packet.Body.Length - packet.Body.Position >= sizeof(uint)
							? packet.Body.ReadUInt32()
							: 0;
						Logger.LogDebug("In-world 0x2003 entity notification (handle=0x{Handle:X8}); no reply.", entityHandle);
						break;
					}

					var clientInstance = packet.Body.ReadUInt32(); // 0x0802e5d4 0x1101b4f5
					var accountId = packet.Body.ReadUInt32(); // 0x310cec57 0x2b68ea25=728295973
					var clientVersion = packet.Body.ReadString();

					client.Session.Account = await Database.GetAccount(accountId);
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					var resolvedCharacter = await Database.GetCharacterForAccount(accountId, clientInstance);
					client.Session.Account.ClientInstance = clientInstance;
					client.Session.Account.Character = resolvedCharacter?.Id == clientInstance
						? resolvedCharacter
						: null;
					client.Session.Character = resolvedCharacter;
					if (resolvedCharacter?.Id != clientInstance)
					{
						Logger.LogInformation(
							"GameServer Hello: resolved temporary client instance {ClientInstance:X8} to pending character {CharacterId:X8} for account {AccountId:X8} while preserving the retail temporary instance for create-flow traffic.",
							clientInstance, resolvedCharacter?.Id ?? 0, accountId);
					}

					Logger.LogInformation("GameServer hello: clientInstance={ClientInstance:X8}, clientVersion={ClientVersion}", clientInstance, clientVersion);

					/*if (clientVersion != "v4.00.NoTS@369764")
					{
					    Logger.Info("Incompatible client connects to the Server");
					    //break;
					}*/

					// Character creation has two phases on this GameServer connection:
					//  1) CREATOR: the client connects on the temp instance 0x021662C8 and we send the
					//     verbatim editor burst (RetailCreateEditorInitialBurst) so the creator UI appears.
					//     Auth replies use the editor conversation family so the client associates the burst.
					//  2) ENTER-WORLD: after the player customises + names, the world entry is sent (handled
					//     out of the post-0x205F / ReadyForPlayScreen path), not here.
					// Existing-character login uses the legacy conversation family + the world entry.
					var isNewCharacter = client.Session.Character.Name == null;

					// Post-creation WORLD connection: the client reconnects with the same (now named) character
					// instance, but this is the SECOND GameServer connection - the one opened after the 0x2002
					// enter-world handoff. It is recognised by the character id being in PendingCreateWorldEntry
					// (added when the name was committed on the editor connection). Consume it here so a later
					// normal login of the same character does not re-trigger the intro. This connection needs the
					// full retail creation world entry (the verbatim burst with its own 0x200B/0x200C/0x2001
					// auth + the intro/cutscene), so skip the helper auth for it.
					var isCreateWorldEntry = !isNewCharacter && PendingCreateWorldEntry.Remove(clientInstance);
					client.Session.IsCreateWorldSession = isCreateWorldEntry;
					client.Session.IsCreateEditorConnection = isNewCharacter;

					if (!isCreateWorldEntry)
					{
						// New (unnamed) characters take the create-editor path; existing characters log in normally.
						var authSender = isNewCharacter
							? RetailCreatePost205FSender
							: null;
						var authReceiver = isNewCharacter
							? CreateEditorReceiver(client)
							: null;

						await ReportDimensionId(client, client.Session.Character.RealmId, authSender, authReceiver); // 1
						await ReportServerId(client, 1, authSender, authReceiver); // 0x00000006
						await AckAuthentication(client, 1, authSender, authReceiver);
					}

					client.Session.LastGameServer2020RequestId = null;
					client.Session.HasPendingPost205FFollowUp = false;
					client.Session.HasPendingCreateEditorReadyFollowUp = false;
					client.Session.HasFlushedCreateWorldEntryBurst = false;
					client.Session.Post205F2020Count = 0;
					client.Session.Post205FF98E10B3Count = 0;
					client.Session.Post205F02165EFFCount = 0;
					client.Session.CreateEditorTempInstanceCount = 0;
					client.Session.CreateEditorObservedRequestIds.Clear();
					client.Session.CreateEditorReadyRequestIds.Clear();
					client.Session.MorphAckCounter = 0;
					client.Session.CharacterEditorInteractive = false;
					client.Session.MorphActivitySequence = 0;
					client.Session.PostMorphBurstSent = false;
					client.Session.AwaitingEnterWorld = false;
					client.Session.CreateWorldChunkIndex = 0;
					// IsCreateEditorConnection / IsCreateWorldSession were set above from this connection's role.

					if (isCreateWorldEntry) // Post-creation enter-world -> full retail creation world entry (-> scene)
					{
						// Force the Tortage intro spawn onto the character (covers resumed characters that
						// never went through CreateCharacter), so the live SpawnPlayer uses the right position.
						var spawnRows = await Database.SetCreationSpawn(client.Session.Character);
						Logger.LogInformation(
							"Post-creation world entry (clientInstance={ClientInstance:X8}, character={CharacterId:X8} '{Name}'): set creation spawn (rows={Rows}, pos=({X},{Y},{Z}), map={Map}); sending the creation world SCENE burst.",
							clientInstance, client.Session.Character.Id, client.Session.Character.Name, spawnRows, client.Session.Character.PositionX, client.Session.Character.PositionY, client.Session.Character.PositionZ,
							client.Session.Character.MapId);
						await SendCreateWorldSceneBurst(client);
					}
					else if (isNewCharacter) // New character -> show the creator
					{
						Logger.LogInformation("New-character create connection (clientInstance={ClientInstance:X8}): sending the character-creator (editor) burst.", clientInstance);
						await SendRetailCreateEditorInitialBurst(client);
					}
					else
					{
						// Existing-character login: the DB-driven world entry - replay the character's current-map
						// world burst (or the beach burst as a universal fallback), re-addressed to this character, so
						// the client builds the playfield and renders the NPCs. The 0x201c handshake comes from us; the
						// player's own position/name/inventory are rebuilt from the DB. (The old hand-built fallback -
						// MapChange/SpawnPlayer + a hardcoded-path inventory .bin - was removed; see SendWorldEntry.)
						await SendWorldEntry(client);
						// Abilities (0x2052) are granted AFTER 0x2024 ReadyForPlayScreen (in FlushPendingPost205FFollowUp),
						// matching the retail login order (handshake -> 0x2024 -> 0x2052). Granting them eagerly here -
						// before the client polled/was ready - is a divergence from the Strie login dump.
					}

					break;
				}

				case GameServerOpcodes.Ox2000:
				{
					await Handle0X2000(client, packet);

					break;
				}

				case GameServerOpcodes.Ox207E:
				{
					if (packet.Body != null)
					{
						LogPacketBodyDebug("GameServer 0x207E body", packet.Body.ToArray());
						if (packet.Body.Length > 0)
							Logger.LogInformation("0x207E decoded: value0={Value0:X2}", packet
								.Body
								.ToArray()[0]);
					}

					break;
				}

				case GameServerOpcodes.Ox205F:
				{
					var unk0 = packet.Body.ReadUInt32(); // 0xc350
					var characterId = packet.Body.ReadUInt32();

					if (IsRetailCreateEditorFlow(client))
					{
						if (client.Session.HasFlushedCreateWorldEntryBurst)
						{
							Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Retail create-editor post-0x205F burst was already flushed; ignoring later 0x205F.", unk0, characterId);
							break;
						}

						client.Session.HasPendingPost205FFollowUp = true;
						client.Session.CreateEditorTempInstanceCount = 0;
						client.Session.Post205F2020Count = 0;
						client.Session.Post205FF98E10B3Count = 0;
						client.Session.Post205F02165EFFCount = 0;
						Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Armed the retail create-editor post-0x205F follow-up.", unk0, characterId);
						break;
					}

					// The new-character create flow only performs the one-shot world-entry download
					// (AccessWindow + SetDayTime + 0xF98E10B3 + SpawnCheck burst) on the FIRST 0x205F. In the
					// retail create capture the client sends a second 0x205F after ReadyForPlayScreen, but
					// retail sends NO download in response to it - it just continues receiving the client's
					// normal in-world 0x02165EFF stream. Re-arming the handoff here re-replayed the 8KB
					// world-entry burst mid-world, which broke the client. So only arm the create handoff once.
					if (client.Session.HasFlushedCreateWorldEntryBurst)
					{
						Logger.LogInformation(
							"0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Create world-entry burst was already flushed; not re-arming the post-0x205F handoff (retail sends no download on the second 0x205F).", unk0,
							characterId);
						break;
					}

					client.Session.HasPendingPost205FFollowUp = true;
					client.Session.Post205F2020Count = 0;
					client.Session.Post205FF98E10B3Count = 0;
					client.Session.Post205F02165EFFCount = 0;
					Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}, expectedCharacterId={ExpectedCharacterId:X8}. Marked post-0x205F follow-up as pending.", unk0, characterId, characterId);

					break;
				}

				case GameServerOpcodes.Ox200C: // request change map(?)
				{
					if (packet.Body != null)
						LogPacketBodyDebug("GameServer 0x200C body", packet.Body.ToArray());

					if (client.Session.Character != null)
						await ReportDimensionId(client, client.Session.Character.RealmId);

					break;
				}

				case GameServerOpcodes.Ox2020:
				{
					if (packet.Body?.Length >= sizeof(uint) * 4)
					{
						var ack0 = packet.Body.ReadUInt32();
						var requestId = packet.Body.ReadUInt32();
						var ack1 = packet.Body.ReadUInt32();
						var characterId = packet.Body.ReadUInt32();
						client.Session.LastGameServer2020RequestId = requestId;

						// 0x2020 is the client polling a spawned entity (requestId = the entity handle). Retail
						// answers each with a 0x2008 SpawnCheck echoing that handle (verified: dikketest world conn
						// polls Kalanthes 0x535A0 and gets a SpawnCheck for 0x535A0). This is what completes/renders
						// each entity. Both existing-character login (world-burst replay) AND post-creation world
						// entry need it: the create-world flow's old hand-built batch answered the wrong, hardcoded
						// capture handles (0x818xx) on Strie's instance, so its entities (incl. Kalanthes) were never
						// confirmed and the intro cinematic ("invalid camera path Kalanthes") failed.
						if (client.Session.UsedWorldBurstReplay || client.Session.IsCreateWorldSession)
							await SendSpawnCheckResponse(client, requestId);

						if (client.Session.Character?.Name == null && client.Session.HasPendingPost205FFollowUp)
							client.Session.Post205F2020Count++;
						Logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}", ack0, requestId, ack1, characterId);
						Logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}, newCharacter2020Count={NewCharacter2020Count}",
							ack0, requestId, ack1, characterId, client.Session.HasPendingPost205FFollowUp, client.Session.Post205F2020Count);
						await FlushPendingPost205FFollowUp(client);
						packet.Body.Position = 0;
					}
					else
						LogPacketBodyDebug("GameServer 0x2020 short body", packet.Body?.ToArray());

					break;
				}

				case GameServerOpcodes.Ox205C:
				{
					if (packet.Body != null)
						LogPacketBodyDebug("GameServer 0x205C body", packet.Body.ToArray());

					break;
				}

				case GameServerOpcodes.Ox205E:
				{
					if (packet.Body != null)
						LogPacketBodyDebug("GameServer 0x205E body", packet.Body.ToArray());

					break;
				}

				case GameServerOpcodes.Ox206D:
				{
					Logger.LogInformation("Received opcode 0x206D.");

					break;
				}

				case GameServerOpcodes.LatencyCheck: // latency check (client 0x2085 -> server 0x2082)
				{
					var counter = packet.Body.ReadUInt32();

					// Retail reply body = [counter echoed][0x00000012][server tick]. The tick is a monotonically
					// increasing microsecond timer (gameplay dumps: ~5,000,000 per ~5s latency interval, ms
					// precision). Sent on the GameCharAgent conversation sub-id 0x08 with headerData 0x01.
					var tick = unchecked((uint)(Environment.TickCount64 * 1000L));

					await new PacketStream()
						.WriteHeader(LatencyResponseSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.LatencyCheckResponse, [0x01])
						.WriteUInt32(counter)
						.WriteUInt32(0x00000012)
						.WriteUInt32(tick)
						.SendAsync(client);

					break;
				}

				case GameServerOpcodes.Ox206A: // request change map(?)
				{
					/*byte[] sender = { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14 };
					        byte[] receiver = { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08 };

					        PacketBuffer aBuffer = new PacketBuffer();
					        aBuffer.WriteHeader(sender, receiver, null, 0x2009, true);
					        aBuffer.WriteUInt32(0x00000067);
					        aBuffer.WriteUInt32(0x5a32f0d7);
					        aBuffer.WriteUInt32(0x0000c350);
					        aBuffer.WriteUInt32(client.client.Account.nClientInst);
					        aBuffer.WriteByte(0);
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt32(0x3f800000);
					        aBuffer.WriteByte(0x62);
					        aBuffer.WriteUInt32(0x0000c79c);
					        aBuffer.WriteUInt32(0x00000faa); //map
					        aBuffer.WriteUInt32(client.client.Account.nClientInst);
					        aBuffer.WriteUInt32(0x0000000a);
					        aBuffer.WriteUInt32(0x00009c50);
					        aBuffer.WriteUInt32(0x00030bde); //instance
					        aBuffer.WriteUInt64(0);
					        aBuffer.WriteUInt32(0x00000014);
					        aBuffer.WriteUInt32(0x00018704);
					        aBuffer.WriteUInt32(0x43a14000);
					        aBuffer.WriteUInt32(0x43160000);
					        aBuffer.WriteUInt32(0x4430399a);
					        aBuffer.WriteUInt32(0x00000032);
					        aBuffer.WriteByte(0);
					        //Log.Warning("Send Packet:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
					        aBuffer.Send(client);

					        aBuffer = new PacketBuffer();
					        aBuffer.WriteHeader(sender, receiver, null, 0x2002, true);
					        //aBuffer.WriteUInt32(htonl(inet_addr(realm->worldServerIPAddress)));
					        //aBuffer.WriteUInt16(realm->worldServerPort);
					        aBuffer.WriteUInt32(Native.Network.htonl(Native.Network.inet_addr("127.0.0.1")));
					        aBuffer.WriteUInt16(Program.GameServerPort);
					        aBuffer.Send(client);*/

					//var packetData = packet.Data?.ToArray().ToHexString();
					//Logger.LogInformation("0x206A: " + (packet.Data?.ToArray()).ToHex());

					break;
				}

				case GameServerOpcodes.Ox207B: // When the 'Select instance' button is clicked
				{
					LogPacketBodyDebug("GameServer 0x207B body", packet.Body?.ToArray());

					//Logger.Info("Receive opcode 0x207b. Maybe hide object(helmet,...)");

					break;
				}

				case GameServerOpcodes.Ox205A:
				{
					var counter = packet.Body.ReadUInt32();

					Logger.LogInformation("0x205A counter={Counter:X4}", counter);

					break;
				}

				case GameServerOpcodes.Ping: // client 0x2005 (empty) -> server Pong 0x2009
				{
					// Reply on the GameAgent conversation sub-id 0x08. Body = [float][0][0]; retail's leading
					// float is a varying server-load/fps metric (~80-83 in dumps) - we have no equivalent, so a
					// fixed plausible value (0x42B32A07 = ~89.6) is sent. The client just treats it as a readout.
					await new PacketStream()
						.WriteHeader(PingResponseSender, LoginWorldReceiver(client), GameServerRespondOpcodes.Pong)
						.WriteUInt32(0x42B32A07)
						.WriteUInt32(0)
						.WriteUInt32(0)
						.SendAsync(client);

					break;
				}

				case GameServerOpcodes.SpawnCheck: // p.160&161
				{
					LogPacketBodyDebug("GameServer SpawnCheck body", packet.Body?.ToArray());

					/*var part1 = packet.Data.ReadUInt32();
					var spawnId = packet.Data.ReadUInt32();
					var unk0 = packet.Data.ReadUInt32();
					var nClientInst = packet.Data.ReadUInt32();

					new PacketStream()
					    .WriteHeader(Sender9, Receiver9, null, GameServerRespondOpcodes.SpawnCheck)
					    .WriteUInt32(part1)
					    .WriteUInt32(spawnId)
					    .WriteByte(0)
					    .Send(client);*/

					break;
				}

				case GameServerOpcodes.SplitStack:
				{
					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var sourceSlot = packet.Body.ReadUInt32();
					var targetStackSize = packet.Body.ReadUInt32();
					var targetSlot = packet.Body.ReadUInt32();

					// 0x00, 0x00, 0x00, 0x68,
					// 0x11, 0x01, 0xB4, 0xF5,
					// 0x00, 0x00, 0x00, 0x09, // source slot
					// 0x00, 0x00, 0x00, 0x29, // 41 (target stack size)
					// 0x00, 0x00, 0x00, 0x13  // target Slot

					// 0x00, 0x00, 0x00, 0x68,
					// 0x11, 0x01, 0xB4, 0xF5,
					// 0x00, 0x00, 0x00, 0x03, // source slot
					// 0x00, 0x00, 0x00, 0x1C, // 28 (target stack size)
					// 0x00, 0x00, 0x00, 0x04  // target Slot

					break;
				}

				case GameServerOpcodes.ChatCommand:
				{
					LogPacketBodyDebug("GameServer ChatCommand body", packet.Body?.ToArray());

					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var unk2 = packet.Body.ReadUInt32();
					var unk3 = packet.Body.ReadUInt32();
					var text = packet.Body.ReadString();

					if (text.Length > 2 && text[0] == '.' && text[1] != '.' && client.Session.Account.Type >= AccountType.GameMaster)
						await HandleGmCommand(client, text);
					else
						// "say" is proximity chat - only same-map players within range hear it (the sender always does).
							// The GameServer owns the live positions, so it picks the recipients here.
							await _agentServerDispatcher.CharacterSay(client.Session.Character.Id, text, SayRecipients(client));

					break;
				}

				case GameServerOpcodes.VanityToggle:
				{
					LogPacketBodyDebug("GameServer VanityToggle body", packet.Body?.ToArray());

					break;
				}

				case GameServerOpcodes.Ox2071:
				{
					var counter = packet.Body.ReadUInt32();

					Logger.LogInformation("0x2071 counter={Counter:X4}", counter);

					break;
				}

				case GameServerOpcodes.MoveItemToBar:
				{
					// 0x206E collides: in-game it's MoveItemToBar, but on the character-creator connection it's
					// the "enter world" signal. The client sends it several times; answer the first with the
					// 0x2002 world handoff, ignore the rest.
					// TODO: THIS IS FALSE?? 0x206E is just MoveItemToBar, this is also needed for new characters to put there starting spells etc on the bar
					if (client.Session.IsCreateEditorConnection)
					{
						if (client.Session.AwaitingEnterWorld)
						{
							client.Session.AwaitingEnterWorld = false;
							await SendWorldHandoff(client);
						}

						break;
					}

					var type = packet.Body.ReadUInt32(); // 1 = item, 2 = spell/ability
					var slotId = packet.Body.ReadUInt32(); // 1 = 0x64, 2 = 0x65, 3 = 0x66, etc
					var sourceId = packet.Body.ReadUInt32(); // ability id or item slot
					var unk = packet.Body.ReadUInt32();

					await LogToClient($"MoveItemToBar: type: {type:X8}, slotId: {slotId:X8}, sourceId: {sourceId:X8}, unk: {unk:X8}");

					break;
				}

				default:
				{
					LogUnknownPacket(packet, opcode);
					break;
				}
			}

			return;

			async Task LogToClient(string message)
			{
				Logger.LogInformation(message);
				await _agentServerDispatcher.SendSystemMessage(client.Session.Account.ClientInstance, message);
			}
		}

		// The create-editor 0x2000 receiver: the player object (GamePlayerType) addressed to this session's
		// client instance. Built as a typed ProtoEndpoint so the type+instance can't drift (the old hardcoded
		// receiver baked in 0x021662C8; see ConanEndpoints / ProtoEndpointGoldenTests).
		private static byte[] CreateEditorReceiver(INetworkClient client)
		{
			return ConanEndpoints.EndpointBytes(ConanEndpoints.GamePlayerType, client.Session.Account.ClientInstance);
		}

		// The post-creation WORLD connection: the second (world) connection opened after the 0x2002 enter-world
		// handoff, flagged in the Hello handler. Distinguishes it from the editor connection and from a normal
		// existing-character login so the intro/cutscene bursts only fire there.
		private static bool IsCreateWorldConnection(INetworkClient client)
		{
			return client.Session.IsCreateWorldSession && client.Session.Character?.Name != null;
		}

		// The character-creator (editor) phase: a not-yet-named character is connected. The client instance is
		// the character's real id now, so the unnamed-character state alone identifies the editor flow.
		private static bool IsRetailCreateEditorFlow(INetworkClient client)
		{
			return client.Session.Character?.Name == null;
		}

	}
}
