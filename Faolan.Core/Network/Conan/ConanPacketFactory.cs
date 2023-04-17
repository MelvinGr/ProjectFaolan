using Faolan.Core.Network.Interfaces;

namespace Faolan.Core.Network.Conan
{
	public sealed class ConanPacketFactory : IPacketFactory<ConanPacket>
	{
		public ConanPacket Create(byte[] bytes)
		{
			return new ConanPacket(bytes);
		}
	}
}
