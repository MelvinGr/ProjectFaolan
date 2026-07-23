using Faolan.Core.Enums;
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
			var opcodeName = PacketLogFormatter.OpcodeName(typeof(ChatServerInterfaceOpcodes), (ushort)packet.Opcode);

			Logger.LogPacket(PacketLogFormatter.Transmitted, ChannelName, opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length, ("body", packet.Body?.ToArray()));
		}
	}
}
