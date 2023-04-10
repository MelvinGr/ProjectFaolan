using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.UniverseAgent
{
	public partial class UniverseAgentListener : Server<ConanPacket>
	{
		// ReSharper disable once SuggestBaseTypeForParameter
		public UniverseAgentListener(ILogger<UniverseAgentListener> logger, IConfiguration configuration, IDatabaseRepository database)
			: base(configuration.UniverseAgentPort(), logger, configuration, database)
		{
		}

		protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			Logger.LogInformation($"Received opcode: {(UniverseAgentOpcodes)packet.Opcode} ({packet.Opcode.ToHex()})");

			switch ((UniverseAgentOpcodes)packet.Opcode)
			{
				case UniverseAgentOpcodes.InitiateAuthentication:
				{
					var accountName = packet.Data.ReadString();
					var unk0 = packet.Data.ReadUInt32();

					client.Account = await Database.GetAccount(accountName);
					if (client.Account == null)
						throw new Exception("client.Account == null");

					InitAuth(client);
					break;
				}

				case UniverseAgentOpcodes.AnswerChallenge:
				{
					var cAnswerChallenge = packet.Data.ReadString();

					// Decrypt data here
					/*var decryptedData = client.Username + "|" + client.AuthChallenge + "|PASSWORD";
					var decryptedDataVector = decryptedData.Split('|');

					if (decryptedDataVector.Length != 3) // wrong decryption
					{
					    Logger.Info("Wrong decryption of username and password");
					    Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
					    break;
					}

					var username = decryptedDataVector[0];
					var nChallenge = decryptedDataVector[1];
					var password = decryptedDataVector[2];

					if (nChallenge != client.AuthChallenge) // wrong auth
					{
					    Logger.Info("Wrong Auth");
					    Packets.AckAuthenticate(client, 0xffffff, 0, 0x04);
					    break;
					}
					*/
					if (!await Database.CheckLogin(client.Account.UserName, "")) // wrong login
					{
						Logger.LogInformation("Wrong Login");
						//Packets.AckAuthenticate(client, 0xffffff, 0, 0x0e);
						break;
					}

					if (client.Account.State != AccountState.Active)
					{
						Logger.LogInformation($"{client.Account.State} player tried to login");
						//Packets.AckAuthenticate(client, 0xffffff, 0, 0x17);
						break;
					}

					Logger.LogInformation($"User {client.Account.UserName} ({client.Account.Id.ToHex()}) is logging on");
					await Database.UpdateLastInfo(client.Account, client);

					SetRegionState(client);
					SendPlayerAgentRealm(client);

					break;
				}

				default:
				{
					Logger.LogWarning($"Unknown packet: {packet}");
					break;
				}
			}
		}
	}
}
