using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	public sealed class ChatServerOutgoingPacketLogger : OutgoingPacketLogger
	{
		public ChatServerOutgoingPacketLogger(ILogger logger) : base(logger, "ChatServer")
		{
		}

		protected override void LogPacket(byte[] bytes)
		{
			var packet = new ChatServerPacket(bytes);
			var opcode = (ushort)packet.Opcode;
			var opcodeName = Enum.IsDefined(typeof(ChatServerInterfaceOpcodes), opcode)
				? $"{packet.Opcode} ({opcode.ToHex()})"
				: opcode.ToHex();

			Logger.LogPacket(PacketLogFormatter.Downlink, ChannelName, opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length, ("body", packet.Body?.ToArray()));
		}
	}
}
