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
			database, new ConanOutgoingPacketLogger(logger, "UniverseAgent", typeof(UniverseInterfaceOpcodes)))
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

					client.Session.Account = await Database.GetAccount(accountName);
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

					// Decrypt the DH+TEA login block. This only succeeds for a PATCHED client (one using our DH
					// public key - see Faolan.ClientLauncher/LoginKeyPatcher.cs). When it decrypts and the challenge round-trips
					// we enforce the real password; an unpatched client (block undecryptable to us, or a challenge
					// mismatch) keeps the legacy accept-any behaviour, so turning this on never breaks login.
					var credentials = LoginEncryption.Decrypt(loginKey);
					bool authenticated;
					if (credentials != null && credentials.Challenge == account.AuthChallenge)
					{
						// Patched client: enforce the password, unless none is configured (the "-" sentinel) - same as
						// before. PasswordHashing.Matches verifies a stored PBKDF2 hash OR a legacy plaintext value.
						if (string.IsNullOrEmpty(account.Password) || account.Password == "-")
							authenticated = true;
						else
						{
							authenticated = credentials.Username == account.UserName && PasswordHashing.Matches(account.Password, credentials.Password);

							// Upgrade a legacy plaintext password to a hash on first successful login.
							if (authenticated && PasswordHashing.NeedsRehash(account.Password))
							{
								account.Password = PasswordHashing.Hash(credentials.Password);
								await Database.SetAccountPassword(account.Id, account.Password);
							}
						}

						if (authenticated)
							Logger.LogInformation("UniverseAgent login: password validated for {UserName} (patched client).", account.UserName);
						else
							Logger.LogWarning("UniverseAgent login: WRONG PASSWORD for {UserName} (patched client).", account.UserName);
					}
					else
					{
						Logger.LogDebug("UniverseAgent login: block not decryptable (unpatched client); accepting {UserName} without password check.", account.UserName);
						authenticated = true;
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

					Logger.LogInformation("UniverseAgent login accepted for user {UserName} ({AccountId})", account.UserName, account.Id.ToHex());
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
