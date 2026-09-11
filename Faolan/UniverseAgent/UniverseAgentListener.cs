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
		public UniverseAgentListener(ILogger<UniverseAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.ServerUniverseAgentPort, logger, configuration,
			database, new ConanPacketTracer(logger, "UniverseAgent", typeof(UniverseInterfaceMethods)), new ConnectionRegistry())
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
					// client's own signature (docs/re/aoc-client-rpc-methods.json). nGameID is the product id the login
					// is for; the retail client hardcodes 1 (all 116 captured logins across the corpus, three different
					// accounts). Read and ignored, because this server serves one game.
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

					// Three outcomes, and Server:PrivateKey alone decides which two are reachable: with a key, the block
					// either decrypted (validate the password) or it did not (refuse - the client is unpatched, and a
					// server that holds a key holds it to make the password required); with no key there is nothing to
					// decrypt with, so every client takes the accept-any path on its account name alone.
					//
					// "Could not decrypt" is not the same as "no key is held" - it is also every unpatched client on a
					// server that does hold one, which is the common case. The key is therefore read first, so the
					// decrypt failure below it can only mean an unpatched client.
					bool authenticated;
					if (Configuration.ServerPrivateKey is { } privateKey)
					{
						if (ClientLoginEncryption.Decrypt(loginKey, privateKey) is { } credentials)
						{
							authenticated = credentials.Username == account.UserName &&
							                credentials.Challenge == account.AuthChallenge.ToString("N") &&
							                PasswordHashing.Verify(credentials.Password, account.Password);

							if (authenticated)
								Logger.LogInformation("UniverseAgent login: password validated for {UserName} (patched client).", account.UserName);
							else
								Logger.LogWarning("UniverseAgent login: WRONG PASSWORD for {UserName} (patched client).", account.UserName);
						}
						else
						{
							authenticated = false;

							Logger.LogWarning("UniverseAgent login: REFUSED {UserName} - unpatched client and this server has a Server:PrivateKey (patch it with Faolan.ClientLauncher --patch-login).", account.UserName);
						}
					}
					else
					{
						authenticated = true;

						Logger.LogDebug("UniverseAgent login: no Server:PrivateKey; accepting {UserName} without password check.", account.UserName);
					}

					if (!authenticated)
					{
						await SendInvalidLogin(client, packet.Sender, InvalidLoginReasons.InvalidLogin);
						break;
					}

					// A timed ban lifts itself here rather than on a background timer: the expiry is a column, so a
					// restart inside the window still holds the ban and one that spanned the whole window releases it.
					// This is the one place that transition happens, which is why it sits before the state check rather
					// than beside it.
					if (await Database.ClearExpiredBan(account.Id, DateTimeOffset.UtcNow))
					{
						Logger.LogInformation("UniverseAgent login: timed ban on '{UserName}' had expired - lifted.", account.UserName);
						account.State = AccountState.Active;
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
