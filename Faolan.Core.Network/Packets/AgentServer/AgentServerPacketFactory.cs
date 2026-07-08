using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.AgentServer
{
	public sealed class AgentServerPacketFactory : IPacketFactory<AgentServerPacket>
	{
		public AgentServerPacket Create(byte[] bytes)
		{
			return new AgentServerPacket(bytes);
		}
	}
}
