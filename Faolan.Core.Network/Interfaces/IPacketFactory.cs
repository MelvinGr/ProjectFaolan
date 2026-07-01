using Faolan.Core.Network.Packets;

namespace Faolan.Core.Network.Interfaces
{
	public interface IPacketFactory<out TPacket> where TPacket : Packet
	{
		TPacket Create(byte[] bytes);
	}
}
