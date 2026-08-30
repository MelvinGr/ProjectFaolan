using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Network.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.GameServer;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Quest
{
	/// <summary>
	///     The wire + world side of a quest SHARE (#101), off the listener (#826).
	/// </summary>
	/// <remarks>
	///     This is the first of the listener's OWN-wire ports to move, and it moved only once
	///     <see cref="ConnectionRegistry" /> existed. Unlike <c>IWorldOutbound</c> - a facade over services - every op
	///     here is real wire code whose one listener-scoped need is "give me the client for this character id". With the
	///     registry injectable that need is satisfiable without the listener, so the code can live where it belongs
	///     instead of behind a pile of Func seams.
	/// </remarks>
	public sealed class QuestShareOutbound : IQuestShareOutbound
	{
		private readonly ConnectionRegistry _connections;
		private readonly WorldSimulation _simulation;

		public QuestShareOutbound(ConnectionRegistry connections, WorldSimulation simulation)
		{
			_connections = connections;
			_simulation = simulation;
		}

		public async Task<bool> SendQuestShareRequest(uint recipientId, uint sharerId, uint questId, string questName)
		{
			if (_connections.ForCharacter(recipientId) is not { } client)
				return false;

			await SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchQuestShareRequest, QuestShareRequestCodec.Build(sharerId, questId, questName));
			return true;
		}

		public Task SendQuestShareGlobalFeedback(uint recipientId, QuestShareFeedback type, uint value = 0)
		{
			return _connections.ForCharacter(recipientId) is { } client
				? SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchQuestShareGlobalFeedback, QuestShareGlobalFeedbackCodec.Build(type, value))
				: Task.CompletedTask;
		}

		public Task SendQuestShareIndividualFeedback(uint recipientId, QuestShareFeedback type, string playerName)
		{
			return _connections.ForCharacter(recipientId) is { } client
				? SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchQuestShareIndividualFeedback, QuestShareIndividualFeedbackCodec.Build(type, playerName))
				: Task.CompletedTask;
		}

		public INetworkClient? ClientFor(uint characterId)
		{
			return _connections.ForCharacter(characterId);
		}

		// The live view of a character, and now the ONE implementation of it: the listener's IGameServerProvider
		// member (the MCP session tool) forwards here rather than keeping a second copy of the same walk.
		public CharacterLiveState? LiveStateOf(uint characterId)
		{
			var client = _connections.ForCharacter(characterId);
			var entity = client == null
				? null
				: _simulation.PlayerWorldEntity(client);
			if (entity == null)
				return null;

			var buffs = entity.ActiveBuffs.Keys.OrderBy(id => id).Select(id => $"0x{id:X8}").ToArray();
			return new CharacterLiveState(characterId, entity.Name, entity.Handle, entity.MapId, _simulation.World.ZoneForEntity(entity).PlayfieldToken, entity.Position.X,
				entity.Position.Y, entity.Position.Z, entity.Vitals?.Health, entity.Vitals?.MaxHealth, buffs);
		}

		// The share dispatches ride the same GameCharAgent -> GameCharInterface pair the trade ones do, from the same
		// sender instance. ⚠️ Un-gated: no capture performs a share, so the endpoint is the trade set's by analogy, not
		// by observation - and a wrong sender is exactly the failure that cost the team invite window (#101's team
		// third), which looks like a wrong body and is not one. [V0]
		//
		// Internal because the inbound DispatchCancelTeamQuestList reply uses the same pair from the listener.
		internal static Task SendQuestShareRpc(INetworkClient client, GameCharInterfaceMethods method, byte[] body)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType), method, body));
		}
	}
}
