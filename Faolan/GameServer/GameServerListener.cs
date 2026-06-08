using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
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
		private static readonly HashSet<uint> PendingCreateWorldEntry = new HashSet<uint>();
		private readonly IAgentServerDispatcher _agentServerDispatcher;

		public GameServerListener(ILogger logger, IConfiguration configuration, IDatabaseRepository database, ushort port, IAgentServerDispatcher agentServerDispatcher) : base(port, logger, configuration, database,
			new ConanOutgoingPacketLogger(logger, "GameServer", typeof(GameServerRespondOpcodes), typeof(GameServer0X2000RespondsOpcodes)))
		{
			_agentServerDispatcher = agentServerDispatcher;

			InitGmCommands();
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

						await ReportDimensionId(client, client.Session.Character.RealmId ?? throw new Exception(), authSender, authReceiver); // 1
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
					else if (await Database.GetMapWorldBurst(client.Session.Character.MapId ?? 0) is { } worldBurst)
					{
						// Existing-character login on a map that has a captured world burst in the DB (the beach plus
						// the player's other maps): replay it, re-addressed to this character, so the client builds the
						// playfield and renders the NPCs. The 0x201c handshake still comes from us; the player's own
						// position/name are rebuilt from the DB. This generalizes (and retired) the embedded beach blob.
						Logger.LogInformation("World-burst login (map={Map}, '{Name}'): replaying {Count} packets from the DB.", client.Session.Character.MapId, client.Session.Character.Name, worldBurst.Packets.Count);
						await Send0X201C(client);
						await ReplayMapWorldBurst(client, worldBurst);
					}
					else
					{
						// No captured world burst for this map: the hand-built fallback (no NPCs/world objects).
						await Send0X201C(client);
						await MapChange(client);
						await SpawnPlayer(client);
						await Send0x2000_0x737A6DF9_Test(client);

						await SetTimeOfDay(client);

						///////////////////////////////////////

						// Old Tarantia Docks notification / chat message
						/*var sender638167354509850010_0 = new byte[] { 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x54 };
						var receiver638167354509850010_0 = new byte[] { 0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };
						var packetData638167354509850010_0 = new byte[]
						{
						    0x00, 0x00, 0x01, 0x50, 0x8D, 0x5A, 0x8B, 0x6B, 0x00, 0x00, 0xC3, 0x50
						};
						//0x08, 0x02, 0xE5, 0xD4
						var packetData638167354509850010_1 = new byte[]
						{
						    0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x31, 0x3C, 0x72, 0x65, 0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x74, 0x6F, 0x6B, 0x65, 0x6E, 0x3D, 0x22, 0x73, 0x68, 0x6F, 0x77, 0x66, 0x69, 0x66, 0x6F, 0x6D, 0x65, 0x73, 0x73, 0x61, 0x67, 0x65, 0x66, 0x72, 0x6F, 0x6D, 0x73, 0x65, 0x72, 0x76, 0x65, 0x72, 0x28, 0x38, 0x29, 0x7C, 0x6D, 0x61, 0x69, 0x6E, 0x28, 0x31, 0x29, 0x7C, 0x47, 0x65, 0x6E, 0x65, 0x72, 0x69, 0x63, 0x44, 0x69, 0x73, 0x70, 0x6C, 0x61, 0x79, 0x41, 0x72, 0x65, 0x61, 0x4E, 0x61, 0x6D, 0x65, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6F, 0x72, 0x79, 0x3D, 0x22, 0x35, 0x30, 0x30, 0x31, 0x38, 0x22, 0x20, 0x6B, 0x65, 0x79, 0x3D, 0x22, 0x70, 0x63, 0x41, 0x64, 0x49, 0x57, 0x59, 0x68, 0x53, 0x58, 0x66, 0x54, 0x46, 0x44, 0x65, 0x35, 0x49, 0x31, 0x67, 0x37, 0x22, 0x20, 0x6B, 0x6E, 0x75, 0x62, 0x6F, 0x74, 0x3D, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3E, 0x3C, 0x70, 0x61, 0x72, 0x61, 0x6D, 0x65, 0x74, 0x65, 0x72, 0x20, 0x76, 0x61, 0x6C, 0x75, 0x65, 0x3D, 0x22, 0x37, 0x3B, 0x26, 0x6C, 0x74, 0x3B, 0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x69, 0x7A, 0x65, 0x64, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6F, 0x72, 0x79, 0x3D, 0x26, 0x23, 0x33, 0x39, 0x3B, 0x35, 0x30, 0x30, 0x32, 0x35, 0x26, 0x23, 0x33, 0x39, 0x3B, 0x20, 0x74, 0x6F, 0x6B, 0x65, 0x6E, 0x3D, 0x26, 0x23, 0x33, 0x39, 0x3B, 0x44, 0x69, 0x73, 0x70, 0x6C, 0x61, 0x79, 0x65, 0x64, 0x54, 0x65, 0x78, 0x74, 0x28, 0x33, 0x29, 0x7C, 0x47, 0x65, 0x6E, 0x65, 0x72, 0x69, 0x63, 0x44, 0x69, 0x73, 0x70, 0x6C, 0x61, 0x79, 0x41, 0x72, 0x65, 0x61, 0x4E, 0x61, 0x6D, 0x65, 0x7C, 0x33, 0x33, 0x37, 0x33, 0x35, 0x31, 0x36, 0x26, 0x23, 0x33, 0x39, 0x3B, 0x26, 0x67, 0x74, 0x3B, 0x22, 0x20, 0x6E, 0x61, 0x6D, 0x65, 0x3D, 0x22, 0x44, 0x69, 0x73, 0x70, 0x6C, 0x61, 0x79, 0x65, 0x64, 0x54, 0x65, 0x78, 0x74, 0x22, 0x20, 0x2F, 0x3E, 0x3C, 0x2F, 0x72, 0x65, 0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x3E, 0x00, 0x00, 0x00, 0x01
						};

						new PacketStream()
						    .WriteHeader(sender638167354509850010_0, receiver638167354509850010_0, null, 0x00002000, true)
						    .WriteArray(packetData638167354509850010_0)
							.WriteUInt32(client.Session.Character.Id)
						    .WriteArray(packetData638167354509850010_1)
						.Send(client);*/

						///////////////////////////////////////

						//
					}

					break;
				}

				case GameServerOpcodes.Ox2000:
				{
					/*if (packet.SenderBytes[1] == 0x48)
					{
					    var sId1 = packet.Data.ReadUInt32();
					    var sId2 = packet.Data.ReadUInt32();
					    var sId3 = packet.Data.ReadUInt32();
					    var sId4 = packet.Data.ReadUInt32();

					    var aBuffer = new PacketStream();
					    aBuffer.WriteHeader(Sender3, Receiver3, null, 0x2000);
					    aBuffer.WriteUInt32(sId3);
					    aBuffer.WriteUInt32(sId4);
					    aBuffer.WriteUInt32(sId4 != 0x000027f9 ? 0x00000002 : 0x00000000);
					    aBuffer.Send(client);
					}
					else*/
					await Handle0X2000(client, packet);

					break;
				}

				case GameServerOpcodes.Ox207E:
				{
					if (packet.Body != null)
					{
						LogPacketBodyDebug("GameServer 0x207E body", packet.Body.ToArray());
						if (packet.Body.Length > 0)
							Logger.LogInformation("0x207E decoded: value0={Value0:X2}", packet.Body.ToArray()[0]);
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

					if (client.Session.Character?.RealmId != null)
						await ReportDimensionId(client, client.Session.Character.RealmId.Value);

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

				case GameServerOpcodes.LatencyCheck: // latency check
				{
					var counter = packet.Body.ReadUInt32();

					await new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.LatencyCheckResponse, [0x01]).WriteUInt32(counter).WriteUInt32(0x0000003C)
						.WriteUInt32(0x65F17240).SendAsync(client);

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

				case GameServerOpcodes.Ping:
				{
					await new PacketStream().WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerRespondOpcodes.Pong).WriteUInt32(0x42B32A07) // old = 0x42c80000, new = 0x42B32A07
						.WriteUInt32(0).WriteUInt32(0).SendAsync(client);

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

				case GameServerOpcodes.GcPing:
				{
					//Logger.LogInformation("GcPing: " + packet.Data.ToArray().ToHex());

					var counter = packet.Body.ReadUInt32();
					var time = (uint)DateTimeOffset.Now.ToUnixTimeSeconds();

					await new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.GcPing).WriteUInt32(counter).WriteUInt32(0x0000004E)
						// counter  retvalue
						// 1        0x2B, 0x95, 0x70, 0x58 -> p.167
						// 2        0x2C, 0x70, 0x01, 0x78 -> p.448
						// 3        0x2c, 0x7e, 0xc6, 0xb8 -> p.540
						// 4        0x2c, 0x7e, 0xc6, 0xb8 -> p.541
						// 5        0x2c, 0xa6, 0xb1, 0x78 -> p.564
						.WriteUInt32(time) // not the correct value? But still working
						.SendAsync(client);

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
						await _agentServerDispatcher.CharacterSay(client.Session.Character.Id, text);

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
					// TODO: THIS IS FALSE 0x206E is just MoveItemToBar, this is also needed for new characters to put there starting spells etc on the bar
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

		// The create-editor 0x2000 receiver: a fixed conversation-family prefix followed by the session's
		// client instance encoded as a varint. Replaces the old hardcoded receiver that baked in 0x021662C8.
		private static byte[] CreateEditorReceiver(INetworkClient client)
		{
			return ReceiverWithInstance([0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10], client.Session.Account.ClientInstance);
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

		// Builds a conversation id from a family prefix and the client instance encoded as a varint tail.
		private static byte[] ReceiverWithInstance(byte[] prefix, uint instance)
		{
			var instanceVarint = EncodeVarint(instance);
			var receiver = new byte[prefix.Length + instanceVarint.Length];
			Array.Copy(prefix, receiver, prefix.Length);
			Array.Copy(instanceVarint, 0, receiver, prefix.Length, instanceVarint.Length);
			return receiver;
		}

		private static byte[] WithEmbeddedCharacterId(byte[] payload, int offset, uint characterId)
		{
			BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, sizeof(uint)), characterId);
			return payload;
		}
	}
}
