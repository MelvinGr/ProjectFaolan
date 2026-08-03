using System;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.PlayerAgent;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.PlayerAgent
{
	public partial class PlayerAgentListener : RpcServer
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

					await client.SendAsync(PlayerRpc(Sender, packet.Sender, PlayerInterfaceOpcodes.AuthenticateAck, b => b.WriteUInt32(client.Session.Account.AuthStatus)));

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

					await client.SendAsync(PlayerRpc(Sender, packet.Sender, PlayerInterfaceOpcodes.PlayerSetupComplete, b => b.WriteUInt32(1)));

					break;
				}

				case PlayerAgentOpcodes.LoadFacebookToken:
				{
					// headerdata was { 0xE0, 0xC1, 0x9C, 0x0D };
					await client.SendAsync(PlayerRpc(Sender, packet.Sender, PlayerInterfaceOpcodes.OnFacebookTokenResult, b => b.WriteUInt16(0).WriteByte(0)));

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

					await SendCharacterSettings(client, packet.Sender);

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
					// #573: this step normally follows LoginCharacter, which selects the character - but a client that
					// resumes the sequence after its connection was replaced sends this one WITHOUT that one, and every
					// handoff below needs the selected character. Recover it from the account's last-played id; if there
					// is nothing to recover the client is out of sequence, which is not something to throw over.
					if (!await EnsureSessionCharacter(client))
					{
						Logger.LogWarning("ReceivedCharacterSettings with no character selected and no last-played id on account {AccountId:X8} - skipping the handoff block.",
							client.Session.Account?.Id ?? 0);
						break;
					}

					// Retail order (login dump): CsPlayerAgent, then ChatServer, then GameServer, then gametweaks.
					await SendCsPlayerAgent(client, packet.Sender);
					await SendChatServer(client, packet.Sender);
					await SendGameServer(client, packet.Sender);

					// OverrideGameTweaks (PlayerInterface 0x1A07): the 19 dynamic game tweaks the client applies on login.
					// The named-tweak set and its wire format are GameTweaksCodec's (#530 wave 7); the 13-byte session
					// header-extra this method rides with stays here, because it is framing.
					await client.SendAsync(new Rpc(ServiceConnectionSender, packet.Sender, PlayerInterfaceOpcodes.OverrideGameTweaks,
						(byte[])[0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE3, 0xEE, 0x9F, 0xE2, 0x07], GameTweaksCodec.Build(GameTweakSets.Defaults())));

					break;
				}

				// PlayerAgent.CreateCharacter(uint i_nDimID, const std::string& cRDBDSN, const uint16 regionCode,
				// const uint16 ageCode, const uint16 genderCode, const String& igrID, const uint32 userType,
				// const uint32 serviceType). `i_nDimID` is the DIMENSION = the realm the character is created on (1 or
				// 2 across the captures, matching the realm the player picked). Everything after it is the same
				// constant tail LoginCharacter carries - an empty count-framed field where `cRDBDSN` sits, zeroed
				// region/age/gender, the "_" placeholder IGR id, and userType/serviceType 0 - so it is read to keep the
				// body fully consumed, not because any of it varies.
				case PlayerAgentOpcodes.CreateCharacterWithData:
				{
					var realmId = body.ReadUInt32();

					var rdbWord = body.ReadUInt32();
					if (!ListFrame.TryCountOf(rdbWord, out var rdbCount) || rdbCount != 0)
						Logger.LogWarning("CreateCharacter: expected an empty count-framed cRDBDSN field, got 0x{Word:X}.", rdbWord);

					var regionCode = body.ReadUInt16();
					var ageCode = body.ReadUInt16();
					var genderCode = body.ReadUInt16();
					var igrId = body.ReadString();
					var userType = body.ReadUInt32();
					var serviceType = body.ReadUInt32();

					Logger.LogDebug("CreateCharacter on realm {RealmId}: region={Region}, age={Age}, gender={Gender}, igr={Igr}, userType={UserType}, serviceType={ServiceType}", realmId, regionCode, ageCode, genderCode,
						igrId, userType, serviceType);

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

					await SendCharacterSettings(client, packet.Sender);

					break;
				}

				case PlayerAgentOpcodes.RequestDeleteCharacter:
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
