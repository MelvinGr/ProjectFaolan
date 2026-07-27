using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.UniverseAgent
{
	public partial class UniverseAgentListener : RpcServer<ConanMessageFramer>
	{
		public UniverseAgentListener(ILogger<UniverseAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.UniverseAgentPort, logger, configuration,
			database, new ConanPacketTracer(logger, "UniverseAgent", typeof(UniverseInterfaceOpcodes)))
		{
		}

		protected override async Task ReceivedPacket(INetworkClient client, Rpc packet)
		{
			var opcode = (UniverseAgentOpcodes)packet.Opcode;
			LogPacketReceived(packet);

			var body = packet.BodyReader();
			switch (opcode)
			{
				case UniverseAgentOpcodes.InitiateAuthentication:
				{
					var accountName = body.ReadString();
					var unk0 = body.ReadUInt32();

					client.Session.Account = (await Database.GetAccount(accountName))!;
					if (client.Session.Account == null)
					{
						Logger.LogWarning("UniverseAgent login: unknown account '{Account}' - rejecting (invalid login).", accountName);
						await SendInvalidLogin(client, packet.Sender, InvalidLoginReasons.InvalidLogin);
					}
					else
						await InitAuth(client);

					break;
				}

				case UniverseAgentOpcodes.AnswerChallenge:
				{
					var loginKey = body.ReadString();
					var account = client.Session.Account;

					bool authenticated;
					if (LoginEncryption.Decrypt(loginKey) is { } credentials)
					{
						authenticated = credentials.Username == account.UserName && credentials.Challenge == account.AuthChallenge && PasswordHashing.Verify(credentials.Password, account.Password);

						if (authenticated)
							Logger.LogInformation("UniverseAgent login: password validated for {UserName} (patched client).", account.UserName);
						else
							Logger.LogWarning("UniverseAgent login: WRONG PASSWORD for {UserName} (patched client).", account.UserName);
					}
					else if (Configuration.LoginAllowUnpatchedClients)
					{
						authenticated = true;

						Logger.LogDebug("UniverseAgent login: block not decryptable (unpatched client); accepting {UserName} without password check.", account.UserName);
					}
					else
					{
						authenticated = false;

						Logger.LogWarning("UniverseAgent login: REFUSED {UserName} - unpatched client and Login:AllowUnpatchedClients is off (patch it with Faolan.ClientLauncher --patch-login).", account.UserName);
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
