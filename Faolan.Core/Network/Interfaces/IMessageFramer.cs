using System.Collections.Generic;

namespace Faolan.Core.Network.Interfaces
{
	public interface IMessageFramer
	{
		IEnumerable<byte[]> PushBytes(byte[] bytes, int length);
	}
}
