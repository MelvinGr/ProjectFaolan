using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.GameServer;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{

	public partial class GameServerListener
	{
		// Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C
		// record stream (the retail money updates ride that conversation).
		private async Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			var ps = new PacketStream();
			ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchUpdateCash);
			UpdateCashBody.WriteUpdateCashBody(ps, balanceTin, reason);
			await ps.SendAsync(client);

			Logger.LogInformation("UpdateCash: {Balance} tin ({Reason}) -> {Character}.", balanceTin, reason, client.Session.Character?.Name);
		}

	}
}
