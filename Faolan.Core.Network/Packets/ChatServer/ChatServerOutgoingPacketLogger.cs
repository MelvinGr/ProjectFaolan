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

				_logger.LogInformation("<= [ChatServer {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}", opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length);

				if (_logger.IsEnabled(LogLevel.Debug) && packet.Body?.Length > 0)
					_logger.LogDebug("<= [ChatServer {OpcodeName}] body ({BodyLength} bytes): {Body}", opcodeName, packet.Body.Length, packet.Body.ToArray().ToHex());
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to decode outgoing ChatServer packet for logging.");
			}
		}
	}
}
