using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.GameServer.CreateFlow;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
    public partial class GameServerListener
    {
        // GameAgent (connection-class) dispatch: auth / chat / ping / server-stats. Routed here by receiver class.
        private async Task HandleGameAgentPacket(INetworkClient client, ConanPacket packet)
        {
            var gaOp = (GameAgentOpcodes)packet.Opcode;
            if (gaOp is not GameAgentOpcodes.UpdateGameServerStats)
                LogPacketReceived(packet, gaOp);

            switch (gaOp)
            {
                case GameAgentOpcodes.Authenticate2:
                {
                    // 0x2003 = GameAgent.Authenticate2 (the connection Hello: clientInstance + accountId + version).
                    // The GameCharAgent.SetTutorialStats collision on this opcode is resolved by the class-first route
                    // above, so here the body is always the >= 12-byte Authenticate2. Guard defensively anyway: a short
                    // body would over-read and drop the client.
                    if (packet.Body.Length - packet.Body.Position < 12)
                    {
                        Logger.LogWarning("GameAgent.Authenticate2 (0x2003) with a short {Bytes}-byte body; ignoring.", packet.Body.Length - packet.Body.Position);
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
                    client.Session.Character = resolvedCharacter;
                    if (resolvedCharacter?.Id != clientInstance)
                    {
                        Logger.LogInformation(
                            "GameServer Hello: resolved temporary client instance {ClientInstance:X8} to pending character {CharacterId:X8} for account {AccountId:X8} while preserving the retail temporary instance for create-flow traffic.",
                            clientInstance,
                            resolvedCharacter?.Id ?? 0,
                            accountId);
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
                    var isCreateWorldEntry = !isNewCharacter && _createFlow.TryConsumePendingCreateWorldEntry(clientInstance);
                    client.Session.IsCreateWorldSession = isCreateWorldEntry;
                    client.Session.IsCreateEditorConnection = isNewCharacter;

                    if (!isCreateWorldEntry)
                    {
                        // New (unnamed) characters take the create-editor path; existing characters log in normally.
                        var authSender = isNewCharacter
                            ? RetailCreatePost205FSender
                            : null;
                        var authReceiver = isNewCharacter
                            ? CreateEditorFraming.Receiver(client)
                            : null;

                        await ReportDimensionId(client, client.Session.Character.RealmId, authSender, authReceiver); // 1
                        await ReportServerId(client, 1, authSender, authReceiver); // 0x00000006
                        await AckAuthenticate(client, 1, authSender, authReceiver);
                    }

                    client.Session.LastGameServer2020RequestId = null;
                    client.Session.HasPendingPost205FFollowUp = false;
                    client.Session.HasPendingCreateEditorReadyFollowUp = false;
                    client.Session.HasFlushedCreateWorldEntryBurst = false;
                    client.Session.HasFlushedCreateEditorPostReady = false;
                    client.Session.CreateEditorTempInstanceCount = 0;
                    client.Session.CreateEditorObservedRequestIds.Clear();
                    client.Session.CreateEditorReadyRequestIds.Clear();
                    client.Session.MorphAckCounter = 0;
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
                            clientInstance,
                            client.Session.Character.Id,
                            client.Session.Character.Name,
                            spawnRows,
                            client.Session.Character.PositionX,
                            client.Session.Character.PositionY,
                            client.Session.Character.PositionZ,
                            client.Session.Character.MapId);
                        // #234 reconstruct (docs/handoff-2026-06-22-create-flow-reconstruct.md): the verbatim
                        // SendCreateWorldSceneBurst delivers the captured `hoxxling` world (wrong char) -> after the
                        // cutscene the client drops the connection and the reconnect hangs. Send the PROVEN burst-less
                        // live-char world + the destiny cutscene's Scry-script overlay instead (SendCreateWorldWithCutscene):
                        // the created char lands on the beach as ITSELF and the cutscene drives to IntroDone. Cutscene
                        // actors stay invisible until #185; SendCreateWorldSceneBurst stays as the golden reference.
                        await _createFlow.SendCreateWorldWithCutscene(client);
                    }
                    else if (isNewCharacter) // New character -> show the creator
                    {
                        Logger.LogInformation("New-character create connection (clientInstance={ClientInstance:X8}): sending the character-creator (editor) burst.", clientInstance);
                        await RetailCreateEditorInitialBurst.SendRetailCreateEditorInitialBurst(client, Database);
                    }
                    else
                    {
                        // Existing-character login: the DB-driven, burst-less world entry - the character's current-map
                        // world content is CONSTRUCTED from the DB (WorldObject/NpcSpawn tables) and addressed to this
                        // character, so the client builds the playfield and renders the NPCs. The 0x201c handshake comes
                        // from us; the player's own position/name/inventory are rebuilt from the DB too. (See SendWorldEntry.)
                        await SendWorldEntry(client);
                        // Abilities (0x2052) are granted AFTER 0x2024 ReadyForPlayScreen (in FlushPendingPost205FFollowUp),
                        // matching the retail login order (handshake -> 0x2024 -> 0x2052). Granting them eagerly here -
                        // before the client polled/was ready - is a divergence from the Strie login dump.

                        // Teleport/changemap re-entry: a full login arms the post-0x205F follow-up off the client's 0x205F,
                        // but a direct GameServer->GameServer changemap reconnect sends no 0x205F (retail confirms). Arm it
                        // here so the next 0x2020 poll flushes 0x2024 ReadyForPlayScreen (+ abilities) and the loading screen
                        // dismisses; otherwise the burst lands but the client loads forever (#28 / totem recall).
                        if (PendingTeleportReentry.TryRemove(clientInstance, out _))
                        {
                            client.Session.HasPendingPost205FFollowUp = true;
                            Logger.LogInformation(
                                "Teleport re-entry (clientInstance={ClientInstance:X8}): armed the post-0x205F follow-up so 0x2024 ReadyForPlayScreen flushes on the next 0x2020 (no client 0x205F on a changemap reconnect).",
                                clientInstance);
                        }
                    }

                    break;
                }

                case GameAgentOpcodes.PassBlob:
                {
                    await Handle0X2000(client, packet);

                    break;
                }

                case GameAgentOpcodes.UpdateGameServerStats: // client 0x2005 (empty) -> server Pong 0x2009
                {
                    await _keepAlive.HandlePing(client);

                    break;
                }

                case GameAgentOpcodes.ChatVicinity:
                {
                    // 0x2002 = GameAgent.ChatVicinity (typed chat, carries GM commands). The GameCharAgent.RequestBindLocation
                    // collision on this opcode is resolved by the class-first route above (HandleRequestBindLocation).
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
                        await _agentServerDispatcher.CharacterSay(client.Session.Character.Id, text, _simulation.SayRecipients(client));

                    break;
                }

                default:
                {
                    // Resolve the method name from the (receiver class, opcode) pair (the GameServer multiplexes
                    // several RPC classes, so the opcode alone is ambiguous) - logs the name, not bare hex.
                    LogUnknownPacket(packet, packet.Opcode, GameServerMethodNames.Resolve(packet.Receiver.Type, packet.Opcode));
                    break;
                }
            }
        }
    }
}
