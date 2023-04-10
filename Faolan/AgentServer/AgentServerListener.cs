using System;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.AgentServer
{
	public partial class AgentServerListener : Server<AgentServerPacket>
	{
		// ReSharper disable once SuggestBaseTypeForParameter
		public AgentServerListener(ILogger<AgentServerListener> logger, IConfiguration configuration, IDatabaseRepository database)
			: base(configuration.AgentServerPort(), logger, configuration, database)
		{
		}

		protected override async Task ReceivedPacket(INetworkClient client, AgentServerPacket packet)
		{
			Logger.LogInformation($"Received opcode: {packet.Opcode} ({packet.Opcode.ToHex()})");

			switch (packet.Opcode)
			{
				case AgentServerOpcodes.Hello:
				{
					var unk0 = packet.Data.ReadUInt32();
					var clientInstance = packet.Data.ReadUInt32();
					var accountId = packet.Data.ReadUInt32();

					client.Account = await Database.GetAccount(accountId);
					if (client.Account == null)
						throw new Exception("client.Account == null");

					if (!await Database.UpdateClientInstance(client.Account, clientInstance))
						throw new Exception("!await Database.UpdateClientInstance");

					new ConanStream()
						.WriteUInt32(0x00050000)
						.Send(client);

					new ConanStream()
						.WriteUInt16(AgentServerRespondsOpcodes.Ox0014)
						.WriteArrayUInt16Length(new ConanStream()
							.WriteUInt32(client.Account.ClientInstance)
							.WriteUInt32(0x00000000)
							.WriteString(client.Character.Name.Length > 0
								? client.Character.Name
								: $"Character{client.Account.ClientInstance}"))
						.Send(client);

					// "<localized category=20000 token=\"welcome_message\">"
					SendSystemMessage(client, Configuration.WelcomeString());
					SendSystemMessage(client, Statics.BuildString.Replace("\n", "<br />"));

					new ConanStream()
						.WriteUInt16(AgentServerRespondsOpcodes.Ox003C)
						.WriteArrayUInt16Length(new ConanStream()
							.WriteByte(0x04)
							.WriteUInt32(0x0000232a)
							.WriteString("~Trial")
							.WriteUInt32(0x00008044)
							.WriteUInt16(0x0000))
						.Send(client);

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
