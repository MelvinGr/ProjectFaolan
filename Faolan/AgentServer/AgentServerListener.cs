using System;
using System.Collections.Generic;
using System.Linq;
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

		public IReadOnlyList<SessionInfo> GetSessions()
		{
			return NetworkClients
				.Select(nc =>
				{
					var account = nc.Session?.Account;
					var character = nc.Session?.Character;
					return new SessionInfo(
						nc.IpAddress,
						account?.Id,
						account?.UserName,
						account?.ClientInstance ?? 0,
						character?.Id,
						character?.Name,
						character?.Level);
				})
				.ToArray();
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

					// Retail sends the full chat-channel subscription list FIRST (one 0x003C per channel), then the
					// 0x0005 ready marker, the 0x0014 character info, and finally the welcome message. Verified from
					// the raw pcap. Channel ids/types are fixed per channel,
					// except ~Playfield whose id is the character's current map. ~Trial carries 0x00008244, the rest
					// 0x00008044 (a per-channel flag).
					await SendChannelSubscription(client, 0x04, 0x0000232A, "~Trial", 0x00008244);
					await SendChannelSubscription(client, 0x04, 0x00002328, "~Global", 0x00008044);
					await SendChannelSubscription(client, 0x04, 0x00002329, "~LFG", 0x00008044);
					await SendChannelSubscription(client, 0x11, 0x00002335, "~GlobalEnglish", 0x00008044);

					if (client.Session.Character?.MapId > 0)
						await SendChannelSubscription(client, 0x90, client.Session.Character.MapId, "~Playfield", 0x00008044);

					await SendChannelSubscription(client, 0x87, 0x001E8480, "~NewbieHelp", 0x00008044);

					await new FaolanStream()
						.WriteUInt32(0x00050000)
						.SendAsync(client);

					await new FaolanStream()
						.WriteUInt16(AgentServerRespondsOpcodes.Ox0014)
						.WriteArrayUInt16Length(s => s
							.WriteUInt32(client.Session.Account.ClientInstance)
							.WriteUInt32(0x00000000)
							.WriteString(client.Session.Character?.Name?.Length > 0
								? client.Session.Character.Name
								: $"Character{client.Session.Account.ClientInstance}"))
						.SendAsync(client);

					await SendSystemMessage(client, Configuration.WelcomeString); //.Replace("$BUILDSTRING", Statics.BuildString));
					await SendSystemMessage(client, Statics.BuildString.Replace("\n", "<br />"));

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
