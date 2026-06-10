using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.UniverseAgent
{
	public partial class UniverseAgentListener : Server<ConanPacket, ConanPacketFactory, ConanMessageFramer>
	{
		public UniverseAgentListener(ILogger<UniverseAgentListener> logger, IConfiguration configuration, IDatabaseRepository database) : base(configuration.UniverseAgentPort, logger, configuration, database,
			new ConanOutgoingPacketLogger(logger, "UniverseAgent", typeof(UniverseAgentResponseOpcodes)))
		{
		}

		protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			var opcode = (UniverseAgentOpcodes)packet.Opcode;
			LogPacketReceived(packet, opcode);

			// Remember the client's conversation id (the sender field's value bytes; SenderBytes.ToArray() is
			// [0x0a][len][value], so skip the 2-byte tag+len) so responses echo it back as the receiver,
			// matching retail instead of a hardcoded capture id.
			var senderField = packet.SenderBytes.ToArray();
			if (senderField.Length > 2)
				client.Session.ClientConversation = senderField[2..];

			switch (opcode)
			{
				case UniverseAgentOpcodes.InitiateAuthentication:
				{
					var accountName = packet.Body.ReadString();
					var unk0 = packet.Body.ReadUInt32();

					client.Session.Account = await Database.GetAccount(accountName);
					if (client.Session.Account == null)
						throw new Exception("client.Account == null");

					await InitAuth(client);
					break;
				}

				case UniverseAgentOpcodes.AnswerChallenge:
				{
					var loginKey = packet.Body.ReadString();
					var account = client.Session.Account;

					// Decrypt the DH+TEA login block. This only succeeds for a PATCHED client (one using our DH
					// public key - see tools/patch_aoc_login_key.py). When it decrypts and the challenge round-trips
					// we enforce the real password; an unpatched client (block undecryptable to us, or a challenge
					// mismatch) keeps the legacy accept-any behaviour, so turning this on never breaks login.
					var credentials = LoginEncryption.Decrypt(loginKey);
					bool authenticated;
					if (credentials is not null && credentials.Challenge == account.AuthChallenge)
					{
						// Patched client: enforce the password, unless none is configured (the "-" sentinel).
						authenticated = string.IsNullOrEmpty(account.Password) || account.Password == "-" || (credentials.Username == account.UserName && credentials.Password == account.Password);

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
						await SendInvalidLogin(client, InvalidLoginReasons.InvalidLogin);
						break;
					}

					if (account.State != AccountState.Active)
					{
						await SendInvalidLogin(client, InvalidLoginReasons.AccountFrozen);
						break;
					}

					Logger.LogInformation("UniverseAgent login accepted for user {UserName} ({AccountId})", account.UserName, account.Id.ToHex());
					await Database.UpdateLastInfo(account, client);

					await SetRegionState(client);
					await SendPlayerAgentRealm(client);

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
