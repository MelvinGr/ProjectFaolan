using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
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
		public PlayerAgentListener(ILogger<PlayerAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.ServerPlayerAgentPort, logger, configuration,
			// The TX naming fall-back is the method catalog: this tier has no opcodes, and the four login handoffs
			// address the ServiceConnection, which no receiver-keyed map covers.
			database, new ConanPacketTracer(logger, "PlayerAgent", typeof(PlayerInterfaceMethods)), new ConnectionRegistry())
		{
		}

		protected override async Task ReceivedPacket(INetworkClient client, Rpc packet)
		{
			// Dispatch is on the method, never the opcode. On this interface pair `Rpc.Opcode` is only the method
			// varint's first group (`0x2080 | (constant & 0x7F)`), so two methods can share one - `0x20EC` is both
			// ReceivedCharacterSettings and AuthenticateLoggedIn, `0x20D6` both VerifyLanguageSetting and
			// UpdateCharacterPreview, and retail sends all four. The whole varint is what tells them apart; an
			// opcode-keyed switch would read an 8-byte AuthenticateLoggedIn as a no-arg ack.
			var method = (PlayerAgentMethods)packet.Method;
			LogPacketReceived(packet);

			var body = packet.BodyReader();
			switch (method)
			{
				// PlayerAgent.Authenticate(nAccount, nCookie, territoryVersion): the cookie is the one the UniverseAgent
				// issued this login, and the only thing that ties this connection to it. An account id alone proves
				// nothing - it is on the wire in every login reply.
				case PlayerAgentMethods.Authenticate:
				{
					var accountId = body.ReadUInt32();
					var cookie = body.ReadUInt32();

					if (await Database.GetAccountForLogin(accountId, cookie) is not {} account)
					{
						RefuseLogin(client, nameof(PlayerAgentMethods.Authenticate), accountId, cookie);
						break;
					}

					client.Session.Account = account;
					client.Session.Account.AuthStatus = body.ReadUInt32();
					await Database.SetAccountAuthStatus(account.Id, account.AuthStatus);

					await client.SendAsync(new Rpc(Sender, packet.Sender, PlayerInterfaceMethods.AuthenticateAck, b => b.WriteUInt32(client.Session.Account.AuthStatus)));

					break;
				}

				// The second, short-lived PlayerAgent socket the client opens mid-session - while in world, ~80 s after
				// the login connection closed - to push settings it has changed. It bootstraps with the same
				// (account, cookie) pair the login connection used and wants the same AuthenticateAck back; the uploads
				// that follow arrive on it without any further handshake.
				case PlayerAgentMethods.AuthenticateLoggedIn:
				{
					var resume = AuthenticateLoggedInCodec.Parse(packet.Body.Span);

					if (await Database.GetAccountForLogin(resume.AccountId, resume.Cookie) is not {} resumed)
					{
						RefuseLogin(client, nameof(PlayerAgentMethods.AuthenticateLoggedIn), resume.AccountId, resume.Cookie);
						break;
					}

					client.Session.Account = resumed;

					Logger.LogDebug("AuthenticateLoggedIn: account {AccountId:X8} re-attaching a settings connection.", resume.AccountId);

					await client.SendAsync(new Rpc(Sender, packet.Sender, PlayerInterfaceMethods.AuthenticateAck, b => b.WriteUInt32(client.Session.Account.AuthStatus)));

					break;
				}

				// The three uploads that ride that socket. Each is stored compressed and byte-for-byte as sent: the
				// checksum the client compares against is an md5 over exactly those bytes, so re-encoding them would
				// make every cached client copy read as stale (see ClientSettingsBufferCodec).
				case PlayerAgentMethods.UpdateSubscriptionSettings:
				{
					var upload = UpdateSubscriptionSettingsCodec.Parse(packet.Body.Span);
					WarnOnCheckSumMismatch(nameof(PlayerAgentMethods.UpdateSubscriptionSettings), upload.Buffer, upload.CheckSum);

					await Database.SetAccountSubscriptionSettings(client.Session.Account.Id, upload.Buffer.Payload);

					Logger.LogInformation("Stored account settings for {AccountId:X8}: {Files} ({Bytes} bytes).", client.Session.Account.Id, string.Join(", ", upload.Buffer.Files.Select(f => f.Name)),
						upload.Buffer.Payload.Length);

					break;
				}

				case PlayerAgentMethods.UpdateCharacterSettings:
				{
					var upload = UpdateCharacterSettingsCodec.Parse(packet.Body.Span);
					WarnOnCheckSumMismatch(nameof(PlayerAgentMethods.UpdateCharacterSettings), upload.Buffer, upload.CheckSum);

					if (await OwnedCharacter(client, upload.CharacterId, nameof(PlayerAgentMethods.UpdateCharacterSettings)) == null)
						break;

					await Database.SaveCharacterClientSettings(upload.CharacterId, upload.Buffer.Payload, null);

					Logger.LogInformation("Stored character settings for {CharacterId:X8}: {Files} ({Bytes} bytes).", upload.CharacterId, string.Join(", ", upload.Buffer.Files.Select(f => f.Name)),
						upload.Buffer.Payload.Length);

					break;
				}

				case PlayerAgentMethods.UpdateCharacterPreview:
				{
					var upload = UpdateCharacterPreviewCodec.Parse(packet.Body.Span);

					if (await OwnedCharacter(client, upload.CharacterId, nameof(PlayerAgentMethods.UpdateCharacterPreview)) == null)
						break;

					await Database.SaveCharacterClientSettings(upload.CharacterId, null, upload.Buffer.Payload);

					Logger.LogInformation("Stored character preview for {CharacterId:X8} ({Bytes} bytes).", upload.CharacterId, upload.Buffer.Payload.Length);

					break;
				}

				case PlayerAgentMethods.GetStartupData:
				{
					// PlayerAgent.GetStartupData(const String& md5) - a single wire string (u16 length + Latin-1), not a
					// bare u16: the client's checksum over its cached startup data, so the server can skip resending
					// what the client already has. Empty on a first login or after a character create (`0000` in the
					// corpus), a 32-character lowercase hex md5 once a cache exists. The full list is always resent, so
					// the value is read for the log only - but it must be read as a string or the 32 bytes stay in the
					// body.
					var startupDataMd5 = body.ReadString();

					Logger.LogDebug("GetStartupData: client startup-data md5 '{Md5}' (we always resend the full list).", startupDataMd5);

					await SendSmallCharList(client, packet.Sender);

					break;
				}

				case PlayerAgentMethods.FinishGetStartupData:
				{
					// PlayerAgent.FinishGetStartupData(const CharacterCheckSumMap& characterMap) - the client reporting
					// the md5 it has cached per character, the entry-level twin of GetStartupData's whole-list md5.
					// Wire: [ListFrame count][ u32 characterId, string md5 ]*. The full list is always resent, so the
					// entries are only logged.
					//
					// The client's own count-framed list (ListFrame): a malformed word would loop on garbage, so it is
					// rejected rather than trusting inbound arithmetic.
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

					await client.SendAsync(new Rpc(Sender, packet.Sender, PlayerInterfaceMethods.PlayerSetupComplete, b => b.WriteUInt32(1)));

					break;
				}

				case PlayerAgentMethods.LoadFacebookToken:
				{
					await client.SendAsync(new Rpc(Sender, packet.Sender, PlayerInterfaceMethods.OnFacebookTokenResult, b => b.WriteUInt16(0).WriteByte(0)));

					break;
				}

				case PlayerAgentMethods.LoginCharacter:
				{
					// PlayerAgent.LoginCharacter(ID32::InstanceType nCharInstance, const StrStrMap& dataMap,
					//   const std::string& cClientLanguage, const uint16 regionCode, const uint16 ageCode,
					//   const uint16 genderCode, const String& igrID, const uint32 userType, const uint32 serviceType)
					// The client's own signature (docs/re/aoc-client-rpc-methods.json). The tail is byte-identical in
					// all 88 captured logins:
					//   [charId][ListFrame 0][string "en"][0][0][0][string "_"][0][0]
					// i.e. everything after the id is a constant the retail client always sends: an empty dataMap, the
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

					if (await OwnedCharacter(client, characterId, nameof(PlayerAgentMethods.LoginCharacter)) is not {} selected)
						break;

					client.Session.Account.ClientInstance = characterId;
					client.Session.Character = selected;
					await Database.UpdateLastInfo(selected.Id, client.IpAddress!);
					await Database.SetAccountClientInstance(client.Session.Account.Id, characterId);

					await SendCharacterSettings(client, packet.Sender);

					break;
				}

				case PlayerAgentMethods.VerifyLanguageSetting:
				{
					var languageId = body.ReadUInt32();

					Logger.LogInformation("PlayerAgent language report: languageId={LanguageId}", languageId);

					break;
				}

				case PlayerAgentMethods.ReceivedCharacterSettings: // after 0x20B9
				{
					// This step normally follows LoginCharacter, which selects the character - but a client that resumes
					// the sequence after its connection was replaced sends this one without that one, and every handoff
					// below needs the selected character. Recover it from the account's last-played id; if there is
					// nothing to recover the client is out of sequence, which is not something to throw over.
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

					// OverrideGameTweaks: the 19 dynamic game tweaks the client applies on login. The named-tweak set and
					// its wire format are GameTweaksCodec's. Like the three handoffs above it addresses the
					// ServiceConnection and restates PlayerInterface#1 as the routing context.
					await client.SendAsync(new Rpc(ServiceConnectionSender, packet.Sender, PlayerInterfaceMethods.OverrideGameTweaks,
						GameTweaksCodec.Build(GameTweakSets.Defaults())) { RoutingContext = PlayerInterfaceRoutingContext });

					break;
				}

				// PlayerAgent.CreateCharacter(uint i_nDimID, const std::string& cRDBDSN, const uint16 regionCode,
				// const uint16 ageCode, const uint16 genderCode, const String& igrID, const uint32 userType,
				// const uint32 serviceType). `i_nDimID` is the dimension = the realm the character is created on (1 or
				// 2 across the captures, matching the realm the player picked). Everything after it is the same
				// constant tail LoginCharacter carries - an empty count-framed field where `cRDBDSN` sits, zeroed
				// region/age/gender, the "_" placeholder IGR id, and userType/serviceType 0 - so it is read to keep the
				// body fully consumed, not because any of it varies.
				case PlayerAgentMethods.CreateCharacterWithData:
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
					// This mirrors retail, where the create session uses one generated instance throughout, and
					// there is no temporary placeholder. ClientInstance is not a Character foreign key, so
					// handing out the real id is safe.
					client.Session.Account.ClientInstance = newCharacter.Id;
					Logger.LogInformation("CreateCharacter: created character {CharacterId:X8}; using it as the create-flow client instance.", newCharacter.Id);
					await Database.SetAccountClientInstance(client.Session.Account.Id, newCharacter.Id);

					await SendCharacterSettings(client, packet.Sender);

					break;
				}

				case PlayerAgentMethods.RequestDeleteCharacter:
				{
					var characterId = body.ReadUInt32();
					Logger.LogInformation("DeleteCharacter request: characterId={CharacterId:X8}.", characterId);

					if (await OwnedCharacter(client, characterId, nameof(PlayerAgentMethods.RequestDeleteCharacter)) == null)
						break;

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

		// A handshake whose cookie is not the account's current login: a client that skipped the UniverseAgent, or one
		// whose login a later login on the same account replaced. No answer to it is captured, so it gets none.
		private void RefuseLogin(INetworkClient client, string method, uint accountId, uint cookie)
		{
			Logger.LogWarning("PlayerAgent {Method} refused: cookie {Cookie:X8} is not account {AccountId:X8}'s current login - dropping the connection.", method, cookie, accountId);
			client.Stop();
		}

		/// <summary>
		///     The character <paramref name="characterId" /> names when it is one of this connection's account's;
		///     otherwise null, with the connection dropped. Every character id this tier is sent comes from the client, and
		///     nothing else stops it naming another account's character.
		/// </summary>
		private async Task<Character?> OwnedCharacter(INetworkClient client, uint characterId, string method)
		{
			var account = client.Session.Account;
			if (await Database.GetCharacter(characterId) is {} character && character.AccountId == account.Id)
				return character;

			Logger.LogWarning("PlayerAgent {Method} refused: character {CharacterId:X8} is not one of account {AccountId:X8}'s - dropping the connection.", method, characterId, account.Id);
			client.Stop();
			return null;
		}
	}
}
