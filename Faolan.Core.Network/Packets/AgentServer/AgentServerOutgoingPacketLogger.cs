using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.AgentServer
{
	public sealed class AgentServerOutgoingPacketLogger : IPacketLogger
	{
		private readonly ILogger _logger;

		public AgentServerOutgoingPacketLogger(ILogger logger)
		{
			_logger = logger;
		}

		public void Log(byte[] bytes)
		{
			try
			{
				var packet = new AgentServerPacket(bytes);
				var opcode = (ushort)packet.Opcode;
				var opcodeName = Enum.IsDefined(typeof(AgentServerRespondsOpcodes), opcode)
					? $"{packet.Opcode} ({opcode.ToHex()})"
					: opcode.ToHex();

				_logger.LogInformation("<= [AgentServer {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}", opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length);

				if (_logger.IsEnabled(LogLevel.Debug) && packet.Body?.Length > 0)
					_logger.LogDebug("<= [AgentServer {OpcodeName}] body ({BodyLength} bytes): {Body}", opcodeName, packet.Body.Length, packet.Body.ToArray().ToHex());
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to decode outgoing AgentServer packet for logging.");
			}
		}
	}
}
