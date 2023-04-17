using System.Threading.Tasks;

namespace Faolan.Core.Network.AgentServer
{
	public interface IAgentServerProvider
	{
		Task CharacterSay(uint characterId, string message);
		Task SendSystemMessage(uint characterId, string message);
		Task SystemBroadcast(string message);
	}
}
