using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
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
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Faolan.Core.Database.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Faolan.GameServer;

namespace Faolan.GameServer.Session
{
	/// <summary>
	///     Authenticate2 (0x2003), the world listener's hello: binds the account and character to the session, refuses
	///     a duplicate login, decides the entry mode (login / teleport re-entry / create-editor / create-world) and
	///     starts that mode's entry sequence. Returns false when the connection was dropped.
	/// </summary>
	public sealed class LoginHandshakeService(ILogger logger, IDatabaseRepository database, IConfiguration configuration, ConnectionRegistry connections,
		TeleportService teleport, CreateFlowService createFlow, WorldEntryService worldEntry)
	{
		private readonly ILogger _logger = logger;
		private readonly IDatabaseRepository _database = database;
		private readonly IConfiguration _configuration = configuration;
		private readonly ConnectionRegistry _connections = connections;
		private readonly TeleportService _teleport = teleport;
		private readonly CreateFlowService _createFlow = createFlow;
		private readonly WorldEntryService _worldEntry = worldEntry;

		public async Task<bool> HandleAuthenticate2(INetworkClient client, FaolanStream body)
		{
		// 0x2003 = GameAgent.Authenticate2 (the connection Hello: clientInstance + accountId + version).
		// The GameCharAgent.SetTutorialStats collision on this opcode is resolved by the class-first route above, so
		// here the body is always the >= 12-byte Authenticate2. Guarded anyway: a short body would over-read and drop
		// the client.
		if (body.Length - body.Position < 12)
		{
			_logger.LogWarning("GameAgent.Authenticate2 (0x2003) with a short {Bytes}-byte body; ignoring.", body.Length - body.Position);
			return true;
		}

		var clientInstance = body.ReadUInt32(); // 0x0802e5d4 0x1101b4f5
		var accountId = body.ReadUInt32(); // 0x310cec57 0x2b68ea25=728295973
		var clientVersion = body.ReadString();

		if (await _database.GetAccount(accountId) is not {} account)
			throw new Exception("Account == null");

		client.Session.Account = account;
		client.Session.Account.ClientInstance = clientInstance;

		// No `!` here on purpose: GetCharacterForAccount returns null for a character this account does not own,
		// and forgiving that makes the check look like dead code someone will helpfully delete.
		client.Session.Character = await _database.GetCharacterForAccount(accountId, clientInstance)
			?? throw new Exception($"Character not found for account {accountId:X} and client instance {clientInstance:X}");

		_logger.LogInformation("GameServer hello: clientInstance={ClientInstance:X8}, clientVersion={ClientVersion}", clientInstance, clientVersion);

		// The build this protocol was decoded against, from config (default v5.01.0@602248 - every capture in the
		// corpus is that one). A mismatch is not a refusal: the protocol is largely shared across builds, and rejecting
		// on a string would lock out a working client over a trivial difference. It warns loudly because otherwise
		// "the client behaves oddly" and "the client is a different build" look identical in the logs.
		if (clientVersion != _configuration.ContentClientVersion)
		{
			_logger.LogWarning("GameServer hello: client reports build {Reported}, but the protocol was decoded against {Expected}. Unexpected behaviour past this point may be a build difference.",
				clientVersion, _configuration.ContentClientVersion);
		}

		// Character creation has two phases on this GameServer connection:
		//  1) Creator: the client connects on the temp instance 0x021662C8 and receives the verbatim editor burst
		//     (RetailCreateEditorInitialBurst) so the creator UI appears. Auth replies use the editor conversation
		//     family so the client associates the burst.
		//  2) Enter-world: after the player customises and names the character, the world entry is sent from the
		//     post-0x205F / ReadyForPlayScreen path rather than here.
		// Existing-character login uses the legacy conversation family + the world entry.
		var isNewCharacter = client.Session!.Character.Name == null;

		// Post-creation world connection: the client reconnects with the same (now named) character
		// instance, but this is the second GameServer connection - the one opened after the 0x2002
		// enter-world handoff. It is recognised by the character id being in PendingCreateWorldEntry
		// (added when the name was committed on the editor connection). Consume it here so a later
		// normal login of the same character does not re-trigger the intro. This connection needs the
		// full retail creation world entry (the verbatim burst with its own 0x200B/0x200C/0x2001
		// auth + the intro/cutscene), so skip the helper auth for it.
		var isCreateWorldEntry = !isNewCharacter && _createFlow.TryConsumePendingCreateWorldEntry(clientInstance);
		// One live connection per character. A second login of a character already in the world gives two sessions
		// writing one character's state - two world entities, two persistence writers, and a wallet and inventory that
		// depend on which session saved last. The account may hold as many connections as it likes (that is how a
		// second client on the same account works); the character is what must be exclusive.
		//
		// The two legitimate re-connections are not duplicates and must pass: the post-creation world handoff and a
		// changemap/teleport re-entry both open a fresh connection for a character whose old one may not have been
		// reaped yet. Both are recognised by their pending set - CreateWorld is consumed just above, and the teleport
		// re-entry mark is peeked here, with the login branch consuming it.
		var reconnectHandoff = isCreateWorldEntry || _teleport.IsReentryPending(clientInstance);
		if (!reconnectHandoff && _connections.ForCharacter(client.Session.Character.Id) is { } existingClient && !ReferenceEquals(existingClient, client))
		{
			_logger.LogWarning("Duplicate login for character {CharacterId:X8} '{Name}' (account {AccountId:X8}): already in the world on another connection - dropping the new one.",
				client.Session.Character.Id, client.Session.Character.Name, accountId);
			client.Stop();
			return false;
		}

		// The connection's world-entry role, decided once here and the single source of truth for routing every later
		// handshake signal (docs/subsystems/world-entry-flow.md). CreateWorld and CreateEditor are known now; the
		// Login-vs-TeleportReentry split is finalised in the existing-login branch below, which consumes the teleport
		// re-entry mark next to the follow-up arming that depends on it.
		client.Session.EntryMode = isCreateWorldEntry
			? WorldEntryMode.CreateWorld
			: isNewCharacter
				? WorldEntryMode.CreateEditor
				: WorldEntryMode.Login;

		// Retail sends this auth trio (DimensionId 0x200b / ServerId 0x200c / AckAuthenticate 0x2001) byte-identically
		// in both the create-editor and the existing-login flow (Barbarian-create vs -login), so it is not the
		// create-vs-login differentiator - that is purely the following 0x2000 burst content. Only isCreateWorldEntry,
		// the post-creation world connection, skips it, because it carries its own auth in the verbatim burst.
		if (!isCreateWorldEntry)
		{
			var authSender = isNewCharacter
				? Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59)
				: GameEndpoints.WorldSenderEndpoint;
			var authReceiver = isNewCharacter
				? CreateFlowWire.Receiver(client)
				: client.Endpoint(RpcInterfaces.GameInterfaceType);

			await WorldEntryService.ReportDimensionId(client, client.Session.Character.RealmId, authSender, authReceiver); // 1
			await WorldEntryService.ReportServerId(client, 1, authSender, authReceiver); // 0x00000006
			await client.SendAsync(new Rpc(authSender, authReceiver, GameInterfaceMethods.DispatchAckAuthenticate, s => s.WriteUInt32(1)));
		}

		client.Session.HasPendingPost205FFollowUp = false;
		client.Session.MorphAckCounter = 0;
		client.Session.MorphActivitySequence = 0;
		client.Session.PostMorphBurstSent = false;
		client.Session.ResetEnterWorldHandoff();
		client.Session.CreateWorldChunkIndex = 0;
		// EntryMode is set above and is what every later handshake signal routes on.

		// The world entry each role gets. Written as an exhaustive switch over the mode set above rather than a chain
		// of booleans - the modes are the routing, so a new one is a compile-time question here instead of silently
		// inheriting the login path.
		switch (client.Session.EntryMode)
		{
		case WorldEntryMode.CreateWorld: // Post-creation enter-world -> the intro/cutscene world entry
		{
			// Force the Tortage intro spawn onto the character (covers resumed characters that
			// never went through CreateCharacter), so the live SpawnPlayer uses the right position.
			// SetCreationSpawn is an ExecuteUpdate: it writes the row and cannot touch the object in hand, so the
			// session character must be moved too. Otherwise the world entry below builds from the stale in-memory
			// transform (the editor's staging spot, 260/4.65/290) instead of the beach wash-ashore point, which puts
			// the player ~440 units from every beach NPC - no in-range NPCs or world objects, and an intro cutscene
			// with no Kalanthes to bind its actor to, which the client reports as `Unable to find NPC 66` from its own
			// controlcamera script.
			var spawnRows = await _database.SetCreationSpawn(client.Session.Character.Id);
			client.Session.Character.MapId = Map.NewPlayerSpawnSpawnPoint.MapId;
			client.Session.Character.Position = Map.NewPlayerSpawnSpawnPoint.Position;
			_logger.LogInformation(
				"Post-creation world entry (clientInstance={ClientInstance:X8}, character={CharacterId:X8} '{Name}'): set creation spawn (rows={Rows}, pos=({X},{Y},{Z}), map={Map}); sending the creation world SCENE burst.",
				clientInstance, client.Session.Character.Id, client.Session.Character.Name, spawnRows, client.Session.Character.PositionX, client.Session.Character.PositionY,
				client.Session.Character.PositionZ, client.Session.Character.MapId);
			// The verbatim SendCreateWorldSceneBurst delivers the captured session's world, i.e. the wrong character,
			// after which the client drops the connection and the reconnect hangs. So this sends the burst-less
			// live-character world plus the destiny cutscene's Scry-script overlay (SendCreateWorldWithCutscene): the
			// created character lands on the beach as itself and the cutscene drives to IntroDone. Cutscene actors are
			// still invisible; SendCreateWorldSceneBurst stays as the golden reference.
			await _createFlow.SendCreateWorldWithCutscene(client);
			break;
		}

		case WorldEntryMode.CreateEditor: // New character -> show the creator
		{
			_logger.LogInformation("New-character create connection (clientInstance={ClientInstance:X8}): sending the character-creator (editor) burst.", clientInstance);
			await CreateEditorBursts.SendRetailCreateEditorInitialBurst(client, _database, logger: _logger);
			break;
		}

		case WorldEntryMode.Login:
		case WorldEntryMode.TeleportReentry:
		{
			// Existing-character login: the database-driven, burst-less world entry - the character's current-map world
			// content is constructed from the WorldObject and NpcSpawn tables and addressed to this character, so the
			// client builds the playfield and renders the NPCs. The 0x201c handshake is server-sent, and the player's
			// own position, name and inventory are rebuilt from the database too. See SendWorldEntry.
			await _worldEntry.SendWorldEntry(client);
			// Abilities (0x2052) are granted after 0x2024 ReadyForPlayScreen, in FlushPendingPost205FFollowUp, matching
			// the retail login order (handshake -> 0x2024 -> 0x2052). Granting them here, before the client has polled,
			// diverges from the captured login.

			// Teleport/changemap re-entry: a full login arms the post-0x205F follow-up off the client's 0x205F, but a
			// direct GameServer-to-GameServer changemap reconnect sends no 0x205F, as retail confirms. It is armed here
			// so the next 0x2020 poll flushes 0x2024 ReadyForPlayScreen and the abilities and the loading screen
			// dismisses; otherwise the burst lands and the client loads for ever.
			if (_teleport.ConsumeReentry(clientInstance))
			{
				client.Session.EntryMode = WorldEntryMode.TeleportReentry;
				client.Session.HasPendingPost205FFollowUp = true;
				_logger.LogInformation(
					"Teleport re-entry (clientInstance={ClientInstance:X8}): armed the post-0x205F follow-up so 0x2024 ReadyForPlayScreen flushes on the next 0x2020 (no client 0x205F on a changemap reconnect).",
					clientInstance);
			}

			break;
		}

		default:
		{
			_logger.LogError("Authenticate2 on an unhandled entry mode {Mode} (clientInstance={ClientInstance:X8}): no world entry was sent.", client.Session.EntryMode, clientInstance);
			break;
		}
		}


			return true;
		}
	}
}
