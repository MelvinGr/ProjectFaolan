using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Interfaces;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Packets.ChatServer
{
	public sealed class ChatServerOutgoingPacketLogger : IPacketLogger
	{
		private readonly ILogger _logger;

		public ChatServerOutgoingPacketLogger(ILogger logger)
		{
			_logger = logger;
		}

		public void Log(byte[] bytes)
		{
			try
			{
				var packet = new ChatServerPacket(bytes);
				var opcode = (ushort)packet.Opcode;
				var opcodeName = Enum.IsDefined(typeof(ChatServerInterfaceOpcodes), opcode)
					? $"{packet.Opcode} ({opcode.ToHex()})"
					: opcode.ToHex();

				var bytesSuffix = PacketLogOptions.BytesSuffix(_logger, ("body", packet.Body?.ToArray()));
				_logger.LogInformation("<= [ChatServer {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length,
					bytesSuffix);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to decode outgoing ChatServer packet for logging.");
			}
		}
	}
}
