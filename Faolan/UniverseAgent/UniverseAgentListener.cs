using System;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.UniverseAgent
{
	public partial class UniverseAgentListener : RpcServer
	{
		public UniverseAgentListener(ILogger<UniverseAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.UniverseAgentPort, logger, configuration,
			database, new ConanPacketTracer(logger, "UniverseAgent", typeof(UniverseInterfaceMethods)))
		{
		}

		protected override async Task ReceivedPacket(INetworkClient client, Rpc packet)
		{
			var method = (UniverseAgentMethods)packet.Method;
			LogPacketReceived(packet);

			var body = packet.BodyReader();
			switch (method)
			{
				case UniverseAgentMethods.InitiateAuthentication:
				{
					// UniverseAgent.InitiateAuthentication(const std::string& username, const uint32 nGameID) - the
					// client's own signature (docs/re/aoc-client-rpc-methods.json). nGameID is the product/title id
					// the login is FOR; the retail client hardcodes 1 (all 116 captured logins across the corpus,
					// three different accounts). Read and ignored - we serve exactly one game.
					var accountName = body.ReadString();
					var gameId = body.ReadUInt32();
					if (gameId != 1)
						Logger.LogWarning("UniverseAgent login: unexpected nGameID {GameId} for '{Account}' (retail always sends 1).", gameId, accountName);

					if (await Database.GetAccount(accountName) is {} account)
					{
						client.Session.Account = account;
						client.Session.Account.AuthChallenge = Guid.NewGuid();

						await Database.SetAccountAuthChallenge(client.Session.Account.Id, client.Session.Account.AuthChallenge);
						await InitAuth(client);
					}
					else
					{
						Logger.LogWarning("UniverseAgent login: unknown account '{Account}' - rejecting (invalid login).", accountName);
						await SendInvalidLogin(client, packet.Sender, InvalidLoginReasons.InvalidLogin);
					}

					break;
				}

				case UniverseAgentMethods.AnswerChallenge:
				{
					var loginKey = body.ReadString();
					var account = client.Session.Account;

					bool authenticated;
					switch (Configuration.LoginPrivateKey)
					{
						case {} privateKey when ClientLoginEncryption.Decrypt(loginKey, privateKey) is { } credentials:
						{
							authenticated = credentials.Username == account.UserName &&
							                credentials.Challenge == account.AuthChallenge.ToString("N") &&
							                PasswordHashing.Verify(credentials.Password, account.Password);

							if (authenticated)
								Logger.LogInformation("UniverseAgent login: password validated for {UserName} (patched client).", account.UserName);
							else
								Logger.LogWarning("UniverseAgent login: WRONG PASSWORD for {UserName} (patched client).", account.UserName);

							break;
						}

						case null when Configuration.LoginAllowUnpatchedClients:
						{
							authenticated = true;

							Logger.LogDebug("UniverseAgent login: block not decryptable (unpatched client); accepting {UserName} without password check.", account.UserName);
							break;
						}

						default:
						{
							authenticated = false;

							Logger.LogWarning("UniverseAgent login: REFUSED {UserName} - unpatched client and Login:AllowUnpatchedClients is off (patch it with Faolan.ClientLauncher --patch-login).", account.UserName);
							break;
						}
					}

					if (!authenticated)
					{
						await SendInvalidLogin(client, packet.Sender, InvalidLoginReasons.InvalidLogin);
						break;
					}

					if (account.State != AccountState.Active)
					{
						await SendInvalidLogin(client, packet.Sender, InvalidLoginReasons.AccountFrozen);
						break;
					}

					await Database.SetAccountLastInfo(account.Id, client.IpAddress!);

					await SetRegionState(client, packet.Sender);
					await SendPlayerAgentRealm(client, packet.Sender);

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
