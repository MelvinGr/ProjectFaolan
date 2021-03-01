using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
    public class CsPlayerAgentListener : Server<ConanPacket>
    {
        // ReSharper disable once SuggestBaseTypeForParameter
        public CsPlayerAgentListener(ILogger<CsPlayerAgentListener> logger, IConfiguration configuration, IDatabaseRepository database)
            : base(configuration.CsPlayerAgentPort(), logger, configuration, database)
        {
        }

        protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
        {
            Logger.LogInformation($"Received opcode: {(CsPlayerAgentOpcodes) packet.Opcode} ({packet.Opcode.ToHex()})");

            switch ((CsPlayerAgentOpcodes) packet.Opcode)
            {
                case CsPlayerAgentOpcodes.Authenticate:
                {
                    var clientInstance = packet.Data.ReadUInt32(); // ID64::InstanceType 
                    var accountId = packet.Data.ReadUInt32();

                    client.Account = await Database.GetAccount(accountId);
                    if (client.Account == null)
                        throw new Exception("client.Account == null");

                    //client.Account.ClientInstance = clientInstance;

                    var unk0 = packet.Data.ReadByte();
                    var auth = packet.Data.ReadUInt32();

                    byte[] sender = {0x0D, 0x14, 0x56, 0xD5, 0x6D, 0x10, 0x44};
                    byte[] receiver = {0x0D, 0xC8, 0x60, 0xD5, 0xBB, 0x10, 0xD4, 0xCB, 0x8B, 0x40};

                    new PacketStream()
                        .WriteHeader(sender, receiver, null, 0x2000)
                        .WriteUInt32(auth)
                        .Send(client);

                    break;
                }

                case CsPlayerAgentOpcodes.Ox2019: // register?
                {
                    var unk0 = packet.Data.ReadUInt32(); // 0x00, 0x00, 0xC3, 0x50
                    var nClientInst = packet.Data.ReadUInt32(); // 0x08, 0x02, 0xE5, 0xD4

                    Logger.LogInformation("Opcodes.Ox2019");

                    break;
                }

                case CsPlayerAgentOpcodes.Ox2072:
                {
                    Logger.LogInformation("Opcodes.Ox2072");
                    break;
                }

                case CsPlayerAgentOpcodes.Ox202A:
                {
                    Logger.LogInformation("Opcodes.Ox202a");

                    break;
                }

                case CsPlayerAgentOpcodes.Ox203B:
                {
                    Logger.LogInformation("Opcodes.Ox203b");
                    break;
                }

                case CsPlayerAgentOpcodes.Ox203C:
                {
                    Logger.LogInformation("Opcodes.Ox203c");
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