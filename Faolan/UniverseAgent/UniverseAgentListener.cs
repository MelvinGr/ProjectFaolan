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
					var cAnswerChallenge = packet.Body.ReadString();

					/*var decryptedData = client.Username + "|" + client.AuthChallenge + "|PASSWORD";
					var decryptedDataVector = decryptedData.Split('|');

					if (decryptedDataVector.Length != 3) // wrong decryption
					{
					    SendInvalidLogin(client, InvalidLoginReasons.InternalSystemError);
					    break;
					}

					var username = decryptedDataVector[0];
					var nChallenge = decryptedDataVector[1];
					var password = decryptedDataVector[2];

					if (nChallenge != client.AuthChallenge) // wrong auth
					{
					    SendInvalidLogin(client, InvalidLoginReasons.InternalSystemError);
					    break;
					}*/

					if (!await Database.CheckLogin(client.Session.Account.UserName, "-"))
					{
						await SendInvalidLogin(client, InvalidLoginReasons.InvalidLogin);
						break;
					}

					if (client.Session.Account.State != AccountState.Active)
					{
						await SendInvalidLogin(client, InvalidLoginReasons.AccountFrozen);
						break;
					}

					Logger.LogInformation("UniverseAgent login accepted for user {UserName} ({AccountId})", client.Session.Account.UserName, client.Session.Account.Id.ToHex());
					await Database.UpdateLastInfo(client.Session.Account, client);

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
