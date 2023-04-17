using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.AgentServer
{
	public partial class AgentServerListener : Server<AgentServerPacket, AgentServerPacketFactory, AgentServerMessageFramer>, IAgentServerProvider
	{
		private readonly IAgentServerDispatcher _dispatcher;

		public AgentServerListener(ILogger<AgentServerListener> logger, IConfiguration configuration, IDatabaseRepository database, IAgentServerDispatcher dispatcher) : base(configuration.AgentServerPort, logger, configuration,
			database, new AgentServerOutgoingPacketLogger(logger))
		{
			_dispatcher = dispatcher;
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			_dispatcher.Register(this);
			return base.StartAsync(cancellationToken);
		}

		public override Task StopAsync(CancellationToken cancellationToken)
		{
			_dispatcher.Unregister(this);
			return base.StopAsync(cancellationToken);
		}

		protected override async Task ReceivedPacket(INetworkClient client, AgentServerPacket packet)
		{
			LogPacketReceived(packet, packet.Opcode);

			switch (packet.Opcode)
			{
				case AgentServerOpcodes.Hello:
				{
					var unk0 = packet.Body.ReadUInt32();
					var clientInstance = packet.Body.ReadUInt32();
					var accountId = packet.Body.ReadUInt32();

					client.Session.Account = await Database.GetAccount(accountId);
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					if (client.Session.Account.ClientInstance != clientInstance)
					{
						Logger.LogWarning("AgentServer hello: account {AccountId:X8} currently tracks client instance {TrackedClientInstance:X8}, but received {ClientInstance:X8}.", accountId,
							client.Session.Account.ClientInstance, clientInstance);
					}

					var resolvedCharacter = await Database.GetCharacterForAccount(accountId, clientInstance);
					client.Session.Account.ClientInstance = clientInstance;
					client.Session.Account.Character = resolvedCharacter?.Id == clientInstance
						? resolvedCharacter
						: null;
					client.Session.Character = resolvedCharacter;

					Logger.LogInformation("AgentServer hello decoded: unk0={Unk0:X}, clientInstance={ClientInstance:X8}, accountId={AccountId:X8}", unk0, clientInstance, accountId);

					await new FaolanStream().WriteUInt32(0x00050000).SendAsync(client);

					await new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.Ox0014).WriteArrayUInt16Length(s => s.WriteUInt32(client.Session.Account.ClientInstance).WriteUInt32(0x00000000).WriteString(
						client.Session.Character?.Name?.Length > 0
							? client.Session.Character.Name
							: $"Character{client.Session.Account.ClientInstance}")).SendAsync(client);

					await SendSystemMessage(client, Configuration.WelcomeString); //.Replace("$BUILDSTRING", Statics.BuildString));
					await SendSystemMessage(client, Statics.BuildString.Replace("\n", "<br />"));

					await new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.Ox003C).WriteArrayUInt16Length(s => s.WriteByte(0x04).WriteUInt32(0x0000232A).WriteString("~Trial").WriteUInt32(0x00008044).WriteUInt16(0x0000))
						.SendAsync(client);

					break;
				}

				default:
				{
					LogUnknownPacket(packet, packet.Opcode);
					break;
				}
			}
		}
	}
}
