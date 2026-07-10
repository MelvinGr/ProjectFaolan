using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Packets.ChatServer
{
	public sealed class ChatServerPacketFactory : IPacketFactory<ChatServerPacket>
	{
		public ChatServerPacket Create(byte[] bytes)
		{
			return new ChatServerPacket(bytes);
		}
	}
}
