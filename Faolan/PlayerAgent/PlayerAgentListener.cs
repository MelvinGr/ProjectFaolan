using System;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.PlayerAgent
{
	public partial class PlayerAgentListener : RpcServer<ConanMessageFramer>
	{
		public PlayerAgentListener(ILogger<PlayerAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.PlayerAgentPort, logger, configuration,
			database, new ConanPacketTracer(logger, "PlayerAgent", typeof(PlayerInterfaceOpcodes)))
		{
			//
		}

		protected override async Task ReceivedPacket(INetworkClient client, Rpc packet)
		{
			var opcode = (PlayerAgentOpcodes)packet.Opcode;
			LogPacketReceived(packet);

			var body = packet.BodyReader();
			switch (opcode)
			{
				case PlayerAgentOpcodes.Authenticate:
				{
					var accountId = body.ReadUInt32();
					var cookie = body.ReadUInt32();

					client.Session.Account = (await Database.GetAccount(accountId))!;
					if (client.Session.Account == null)
						throw new Exception("client.Account == null");

					client.Session.Account.AuthStatus = body.ReadUInt32();
					await Database.SaveAccount(client.Session.Account);

					await client.SendAsync(PlayerRpc(Sender, packet.Sender, PlayerInterfaceOpcodes.InitAuth, b => b.WriteUInt32(client.Session.Account.AuthStatus)));

					break;
				}

				case PlayerAgentOpcodes.GetStartupData:
				{
					// PlayerAgent.GetStartupData(const String& md5) - a single wire string (u16 length + LATIN1), NOT a
					// bare u16: the client's checksum over its CACHED startup data, so the server can skip resending
					// what the client already has. Empty on a first login / after a character create (`0000` in the
					// corpus), a 32-char lowercase hex md5 once a cache exists. We always resend the full list, so it
					// is read for the log only - but it must be read as a STRING or the 32 bytes stay in the body.
					var startupDataMd5 = body.ReadString();

					Logger.LogDebug("GetStartupData: client startup-data md5 '{Md5}' (we always resend the full list).", startupDataMd5);

					await SendSmallCharList(client, packet.Sender);

					break;
				}

				case PlayerAgentOpcodes.FinishGetStartupData:
				{
					// PlayerAgent.FinishGetStartupData(const CharacterCheckSumMap& characterMap) - the client reporting
					// the md5 it has cached PER CHARACTER, the entry-level twin of GetStartupData's whole-list md5.
					// Wire: [ListFrame count][ u32 characterId, string md5 ]*. Same "we always resend" story, so the
					// entries only get logged.
					//
					// The client's own count-framed list (ListFrame): a malformed word means we would loop on garbage,
					// so reject it rather than trusting inbound arithmetic.
					var word = body.ReadUInt32();
					if (!ListFrame.TryCountOf(word, out var charCount))
					{
						Logger.LogWarning("FinishGetStartupData: 0x{Word:X} is not a count-framed list word; ignoring the entries.", word);
						charCount = 0;
					}

					for (var i = 0; i < charCount; i++)
					{
						var characterId = body.ReadUInt32();
						var characterDataMd5 = body.ReadString();

						Logger.LogDebug("FinishGetStartupData entry: characterId={CharacterId:X8}, cached md5={Md5}", characterId, characterDataMd5);
					}

					await SendCharacterList(client, packet.Sender);
					await SendRealmList(client, packet.Sender);

					await client.SendAsync(PlayerRpc(Sender, packet.Sender, PlayerInterfaceOpcodes.SendCharacterRealmData, b => b.WriteUInt32(1)));

					break;
				}

				case PlayerAgentOpcodes.LoadFacebookToken:
				{
					// headerdata was { 0xE0, 0xC1, 0x9C, 0x0D };
					await client.SendAsync(PlayerRpc(Sender, packet.Sender, PlayerInterfaceOpcodes.Ox209C, b => b.WriteUInt16(0).WriteByte(0)));

					break;
				}

				case PlayerAgentOpcodes.LoginCharacter:
				{
					// PlayerAgent.LoginCharacter(ID32::InstanceType nCharInstance, const StrStrMap& dataMap,
					//   const std::string& cClientLanguage, const uint16 regionCode, const uint16 ageCode,
					//   const uint16 genderCode, const String& igrID, const uint32 userType, const uint32 serviceType)
					// The client's own signature (docs/re/aoc-client-rpc-methods.json). The old u32/u8 run read the
					// right characterId and then walked the rest misaligned - the tail below is the real grammar, and
					// it is byte-identical in all 88 captured logins:
					//   [charId][ListFrame 0][string "en"][0][0][0][string "_"][0][0]
					// i.e. everything after the id is a constant the retail client always sends: an EMPTY dataMap, the
					// UI language, zeroed region/age/gender (the age-gate fields the launcher never fills), the "_"
					// placeholder IGR (internet-gaming-room / cafe) id, and account userType/serviceType 0.
					var characterId = body.ReadUInt32();

					var dataMapWord = body.ReadUInt32();
					if (!ListFrame.TryCountOf(dataMapWord, out var dataMapCount))
					{
						Logger.LogWarning("LoginCharacter: 0x{Word:X} is not a count-framed list word; ignoring dataMap.", dataMapWord);
						dataMapCount = 0;
					}

					for (var i = 0; i < dataMapCount; i++)
						Logger.LogDebug("LoginCharacter dataMap[{Index}]: {Key}={Value}", i, body.ReadString(), body.ReadString());

					var clientLanguage = body.ReadString();
					var regionCode = body.ReadUInt16();
					var ageCode = body.ReadUInt16();
					var genderCode = body.ReadUInt16();
					var igrId = body.ReadString();
					var userType = body.ReadUInt32();
					var serviceType = body.ReadUInt32();

					Logger.LogDebug("LoginCharacter {CharacterId:X8}: language={Language}, region={Region}, age={Age}, gender={Gender}, igr={Igr}, userType={UserType}, serviceType={ServiceType}", characterId,
						clientLanguage, regionCode, ageCode, genderCode, igrId, userType, serviceType);

					client.Session.Account.ClientInstance = characterId;
					client.Session.Character = (await Database.GetCharacter(characterId))!;
					await Database.UpdateLastInfo(client.Session.Character!.Id, client.IpAddress!);
					await Database.SaveAccount(client.Session.Account);

					await SendOx20B9(client, packet.Sender);

					break;
				}

				case PlayerAgentOpcodes.VerifyLanguageSetting:
				{
					var languageId = body.ReadUInt32();

					Logger.LogInformation("PlayerAgent language report: languageId={LanguageId}", languageId);

					break;
				}

				case PlayerAgentOpcodes.ReceivedCharacterSettings: // after 0x20B9
				{
					// Retail order (login dump): CsPlayerAgent, then ChatServer, then GameServer, then gametweaks.
					await SendCsPlayerAgent(client, packet.Sender);
					await SendChatServer(client, packet.Sender);
					await SendGameServer(client, packet.Sender);

					// OverrideGameTweaks (PlayerInterface 0x1A07): the 19 dynamic game tweaks the client applies on
					// login. Body is built inline from the named-tweak list below; the wire format is locked byte-for-byte by GameTweaksTests.cs.
					using var gameTweaks = new FaolanStream();

					// The PlayerInterface OverrideGameTweaks (0x1A07) payload: the server's "dynamic game tweaks" the client
					// applies on login (ApplyDynamicGameTweaks: "Applying N gametweaks"). Reverse-engineered from the retail
					// capture (logs-test/retail pkt#3) — see docs/protocol/02-rpc.md.
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
						("AAPointsPerLevel", 1.0), ("AlternateAdvancement_GenericTokensAwarded", 1.0), ("AlternateAdvancement_SpecificTokensAwarded", 3.0), ("Anniversary2020", 0.0),
						("Anniversary2020_CanGetArmor", 0.0), ("Anniversary_5_SpawnBosses", 0.0), ("CurrentSeason", 3.0), ("DoubleRaidFinderTokens", 0.0), ("EventAnniversary_2014", 0.0),
						("EventDoubleXP", 0.0), ("EventHalloween", 1.0), ("ExpandInventories_MaxBankSlots", 300.0), ("IsPvPServer", 0.0), ("PvEXPFromPvPMultiplier", 0.1),
						("RaidFinderEventChest", 0.0), ("WorldBoss_SpawnDay_Boss_11", 0.0), ("WorldBoss_SpawnDay_Boss_4", 0.0), ("WorldBoss_SpawnWeek_Boss_11", 0.0),
						("WorldBoss_SpawnWeek_Boss_3", 3.0)
					];

					// Opaque 4-byte prefix replayed verbatim from the retail capture (a tweak-table id/version; not the
					// entry count, which the client infers by reading to end-of-stream).
					gameTweaks.WriteUInt32(0x00004ED4);

					foreach (var (name, value) in Defaults)
						gameTweaks.WriteString(name).WriteDouble(value);

					await client.SendAsync(new Rpc(ServiceConnectionSender, packet.Sender, PlayerInterfaceOpcodes.OverrideGameTweaks,
						(byte[])[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE3, 0xEE, 0x9F, 0xE2, 0x07], gameTweaks.ToArray()));

					break;
				}

				case PlayerAgentOpcodes.CreateCharacter:
				{
					var realmId = body.ReadUInt32();
					var newCharacter = await Database.CreateCharacter(client.Session.Account.Id, realmId);

					client.Session.Character = newCharacter;
					// The character's real DB id IS the client instance for the whole create flow: the client
					// opens both the creator and the world GameServer connections with it, and the editor/world
					// bursts are constructed with it at send time (see the burst builders in CreateFlowService).
					// This mirrors retail, where the create session uses one generated instance throughout
					// (there is no temporary const). ClientInstance is no longer a Character FK, so handing out
					// the real id is safe.
					client.Session.Account.ClientInstance = newCharacter.Id;
					Logger.LogInformation("CreateCharacter: created character {CharacterId:X8}; using it as the create-flow client instance.", newCharacter.Id);
					await Database.SaveAccount(client.Session.Account);

					await SendOx20B9(client, packet.Sender);

					break;
				}

				case PlayerAgentOpcodes.DeleteCharacter:
				{
					var characterId = body.ReadUInt32();
					Logger.LogInformation("DeleteCharacter request: characterId={CharacterId:X8}.", characterId);

					var deleted = await Database.DeleteCharacter(characterId);
					Logger.LogInformation("DeleteCharacter: characterId={CharacterId:X8} deleted={Deleted}.", characterId, deleted);

					// The refreshed list re-queries the DB (GetCharactersByAccount), so it omits the deleted character.
					await SendSmallCharList(client, packet.Sender);

					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}
	}
}
