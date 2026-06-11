using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.GameServer;

namespace Faolan.GameServer
{
	// The IGameServerProvider face of the listener: lets out-of-band tooling (the in-process MCP server)
	// observe connected world sessions and drive the server through the already-validated GM command paths.
	public partial class GameServerListener : IGameServerProvider
	{
		public IReadOnlyList<SessionInfo> GetSessions()
		{
			return NetworkClients
				.Select(nc =>
				{
					var account = nc.Session?.Account;
					var character = nc.Session?.Character;
					return new SessionInfo(nc.IpAddress, account?.Id, account?.UserName, account?.ClientInstance ?? 0, character?.Id, character?.Name, character?.Level);
				})
				.ToArray();
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			var client = ClientForCharacter(characterId);
			if (client is null)
				return false;

			// HandleGmCommand strips a single leading prefix char (".teleport" -> "teleport"), so ensure one is present.
			if (commandText.Length == 0 || char.IsLetterOrDigit(commandText[0]))
				commandText = "." + commandText;

			await HandleGmCommand(client, commandText);
			return true;
		}
	}
}
