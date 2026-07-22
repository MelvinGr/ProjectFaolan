using System.Threading.Tasks;

namespace Faolan.Core.Network.Packets
{
	public static class FaolanStreamSendExtensions
	{
		// Fluent send for the chat server, whose frames are plain FaolanStream buffers (its own framing carries no
		// Conan RPC header, so there is no Rpc model). The Conan tiers build an Rpc and send via client.SendAsync(Rpc).
		public static Task SendAsync(this FaolanStream stream, INetworkClient client)
		{
			return client.SendAsync(stream.ToArray());
		}
	}
}
