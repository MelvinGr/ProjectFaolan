using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.PlayerAgent
{
	public partial class PlayerAgentListener : Server<ConanPacket, ConanPacketFactory, ConanMessageFramer>
	{
		public PlayerAgentListener(ILogger<PlayerAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.PlayerAgentPort,
			logger,
			configuration,
			database,
			new ConanOutgoingPacketLogger(logger, "PlayerAgent", typeof(PlayerAgentResponseOpcodes)))
		{
			//
		}

		protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			var opcode = (PlayerAgentOpcodes)packet.Opcode;
			LogPacketReceived(packet, opcode);

			// Remember the client's conversation id (the sender field's value bytes; SenderBytes.ToArray() is
			// [0x0a][len][value], so skip the 2-byte tag+len) so responses echo it back as the receiver,
			// matching retail instead of a hardcoded capture id.
			var senderField = packet.SenderBytes.ToArray();
			if (senderField.Length > 2)
				client.Session.ClientConversation = senderField[2..];

			switch (opcode)
			{
				case PlayerAgentOpcodes.RequestAuth:
				{
					var accountId = packet.Body.ReadUInt32();
					var cookie = packet.Body.ReadUInt32();

					client.Session.Account = await Database.GetAccount(accountId);
					if (client.Session.Account == null)
						throw new Exception("client.Account == null");

					client.Session.Account.AuthStatus = packet.Body.ReadUInt32();
					await Database.SaveAccount(client.Session.Account);

					await new PacketStream().WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.InitAuth, [0x93, 0x86, 0xee, 0x05]).WriteUInt32(client.Session.Account.AuthStatus).SendAsync(client);

					break;
				}

				case PlayerAgentOpcodes.RequestSmallCharList:
				{
					var unk1 = packet.Body.ReadUInt16();

					await SendSmallCharList(client);

					break;
				}

				case PlayerAgentOpcodes.RequestCharRealmData:
				{
					var charCount = packet.Body.ReadUInt32() / 1009 - 1; // % 1009

					for (var i = 0; i < charCount; i++)
					{
						var unk1 = packet.Body.ReadUInt32();
						var unk2 = packet.Body.ReadString();

						Logger.LogDebug("RequestCharRealmData entry: characterId={CharacterId:X8}, authChallenge={AuthChallenge}", unk1, unk2);
					}

					await SendCharacterList(client);
					await SendRealmList(client);

					await new PacketStream().WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.SendCharacterRealmData, [0xcb, 0xc6, 0xfc, 0x04]).WriteUInt32(1).SendAsync(client);

					break;
				}

				case PlayerAgentOpcodes.Ox20A6:
				{
					await new PacketStream() // headerdata was { 0xE0, 0xC1, 0x9C, 0x0D };
						.WriteHeader(Sender, client.Session.ClientConversation, PlayerAgentResponseOpcodes.Ox209C, [0x99, 0x95, 0x92, 0x05])
						.WriteUInt16(0)
						.WriteByte(0)
						.SendAsync(client);

					break;
				}

				case PlayerAgentOpcodes.EnterWorld:
				{
					var characterId = packet.Body.ReadUInt32();
					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var unk2 = packet.Body.ReadUInt32();
					var unk3 = packet.Body.ReadByte();
					var unk4 = packet.Body.ReadUInt32();
					var unk5 = packet.Body.ReadUInt32();
					var unk6 = packet.Body.ReadUInt32();

					client.Session.Account.ClientInstance = characterId;
					client.Session.Character = await Database.GetCharacter(characterId);
					await Database.UpdateLastInfo(client.Session.Character, client.IpAddress);
					await Database.SaveAccount(client.Session.Account);

					await SendOx20B9(client);

					break;
				}

				case PlayerAgentOpcodes.Ox20D6:
				{
					var languageId = packet.Body.ReadUInt32();

					Logger.LogInformation("PlayerAgent language report: languageId={LanguageId}", languageId);

					break;
				}

				case PlayerAgentOpcodes.Ox20EC: // after 0x20B9
				{
					// Retail order (login dump): CsPlayerAgent, then AgentServer, then GameServer, then gametweaks.
					await SendCsPlayerAgent(client);
					await SendAgentServer(client);
					await SendGameServer(client);

					// OverrideGameTweaks (PlayerInterface 0x1A07): the 19 dynamic game tweaks the client applies on
					// login. Body is built from the named GameTweaks catalog; see GameTweaks.cs for the wire format.
					var gameTweaks = new PacketStream().WriteHeader(ServiceConnectionSender,
						client.Session.ClientConversation,
						PlayerAgentResponseOpcodes.Ox1A07,
						[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE3, 0xEE, 0x9F, 0xE2, 0x07]);

					// The PlayerInterface OverrideGameTweaks (0x1A07) payload: the server's "dynamic game tweaks" the client
					// applies on login (ApplyDynamicGameTweaks: "Applying N gametweaks"). Reverse-engineered from the retail
					// capture (logs-test/retail pkt#3) — see docs/archive/opcode-re-plan.md item #2.
					//
					// Wire format of the `overrides` collection (raw stream, NOT protobuf):
					//   [u32 header][ per tweak: [u16 nameLen][name ASCII][f64 value] ] *   — all multi-byte fields big-endian.
					//
					// The tweak NAMES are plaintext on the wire (length-prefixed), not name-hashes — they map 1:1 onto the
					// client's RDBGameTweaks DataTable keys. Every value here is serialized as an 8-byte double regardless of the
					// tweak's logical type (bool/int/float). This list is byte-for-byte identical to what retail sends; a single
					// stray byte makes the client log "stream bad after 'overrides'" and drop the whole PlayerInterface route
					// (the old bug — it applied 0 of 19 tweaks).

					(string Name, double Value)[] Defaults =
					[
						("AAPointsPerLevel", 1.0),
						("AlternateAdvancement_GenericTokensAwarded", 1.0),
						("AlternateAdvancement_SpecificTokensAwarded", 3.0),
						("Anniversary2020", 0.0),
						("Anniversary2020_CanGetArmor", 0.0),
						("Anniversary_5_SpawnBosses", 0.0),
						("CurrentSeason", 3.0),
						("DoubleRaidFinderTokens", 0.0),
						("EventAnniversary_2014", 0.0),
						("EventDoubleXP", 0.0),
						("EventHalloween", 1.0),
						("ExpandInventories_MaxBankSlots", 300.0),
						("IsPvPServer", 0.0),
						("PvEXPFromPvPMultiplier", 0.1),
						("RaidFinderEventChest", 0.0),
						("WorldBoss_SpawnDay_Boss_11", 0.0),
						("WorldBoss_SpawnDay_Boss_4", 0.0),
						("WorldBoss_SpawnWeek_Boss_11", 0.0),
						("WorldBoss_SpawnWeek_Boss_3", 3.0)
					];

					// Opaque 4-byte prefix replayed verbatim from the retail capture (a tweak-table id/version; not the
					// entry count, which the client infers by reading to end-of-stream).
					gameTweaks.WriteUInt32(0x00004ED4);

					foreach (var (name, value) in Defaults)
						gameTweaks.WriteString(name).WriteDoubleBigEndian(value);

					await gameTweaks.SendAsync(client);

					break;
				}

				case PlayerAgentOpcodes.CreateCharacter:
				{
					var realmId = packet.Body.ReadUInt32();
					var newCharacter = await Database.CreateCharacter(client.Session.Account.Id, realmId);

					client.Session.Character = newCharacter;
					// The character's real DB id IS the client instance for the whole create flow: the client
					// opens both the creator and the world GameServer connections with it, and the editor/world
					// bursts are constructed with it at send time (see Burst + SendBurst in PacketBurstHelper).
					// This mirrors retail, where the create session uses one generated instance throughout
					// (there is no temporary const). ClientInstance is no longer a Character FK, so handing out
					// the real id is safe.
					client.Session.Account.ClientInstance = newCharacter.Id;
					Logger.LogInformation("CreateCharacter: created character {CharacterId:X8}; using it as the create-flow client instance.", newCharacter.Id);
					await Database.SaveAccount(client.Session.Account);

					await SendOx20B9(client);

					break;
				}

				case PlayerAgentOpcodes.DeleteCharacter:
				{
					var characterId = packet.Body.ReadUInt32();
					Logger.LogInformation("DeleteCharacter request: characterId={CharacterId:X8}.", characterId);

					var deleted = await Database.DeleteCharacter(characterId);
					Logger.LogInformation("DeleteCharacter: characterId={CharacterId:X8} deleted={Deleted}.", characterId, deleted);

					// The refreshed list re-queries the DB (GetCharactersByAccount), so it omits the deleted character.
					await SendSmallCharList(client);

					break;
				}

				default:
				{
					LogUnknownPacket(packet, opcode);
					break;
				}
			}
		}
	}
}
