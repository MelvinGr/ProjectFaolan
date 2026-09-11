using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.Apprentice
{
	/// <summary>
	///     The apprentice (mentoring) wire. Every op is addressed by character id - mentoring always spans two
	///     connections - so the class resolves clients through <see cref="ConnectionRegistry" /> rather than a session.
	/// </summary>
	internal sealed class ApprenticeOutbound : IApprenticeOutbound
	{
		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly ConnectionRegistry _connections;

		public ApprenticeOutbound(ConnectionRegistry connections, IChatServerDispatcher agentServerDispatcher)
		{
			_connections = connections;
			_agentServerDispatcher = agentServerDispatcher;
		}

		// No capture performs an apprenticeship, so these sends are not golden-gated: the endpoint pair is taken from
		// the neighbouring GameCharInterfaceProject dispatches that are observed (SyncCanSprint / UpdateSprintBar), and
		// the bodies are the client's own signatures. [V0]
		public async Task<bool> SendApprenticeRequest(uint studentId, uint teacherId)
		{
			if (_connections.ForCharacter(studentId) is not { } client)
				return false;

			await SendApprenticeRpc(client, GameCharInterfaceProjectMethods.DispatchRequestApprentice, RequestApprenticeCodec.Build(teacherId));
			return true;
		}
		public Task SendApprenticeEntry(uint recipientId, uint teacherId, uint studentId, bool added)
		{
			return _connections.ForCharacter(recipientId) is { } client
				? SendApprenticeRpc(client, added
					? GameCharInterfaceProjectMethods.DispatchAddApprenticeEntry
					: GameCharInterfaceProjectMethods.DispatchRemoveApprenticeEntry, ApprenticeEntryCodec.Build(teacherId, studentId))
				: Task.CompletedTask;
		}
		public Task SendApprenticeMode(uint recipientId, bool on)
		{
			// The mode flag is on GameCharInterface, not GameCharInterfaceProject - a different interface, so a
			// different endpoint pair, which is exactly the distinction `WireOpcode` cannot express.
			return _connections.ForCharacter(recipientId) is { } client
				? client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchApprenticeModeOnOff, ApprenticeModeCodec.Build(on)))
				: Task.CompletedTask;
		}
		public async Task SendApprenticeLevel(uint characterId, uint apprenticeLevel)
		{
			if (_connections.ForCharacter(characterId) is not { } client)
				return;

			// ApprenticeLevel alone - not `SetLevel`, which also writes Level so the nameplate tracks a GM `.level`.
			// Here the real level is unchanged and only the mitigation term moves.
			await GameWire.SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.ApprenticeLevel, apprenticeLevel), characterId);
		}
		public Task SendApprenticeFeedback(uint characterId, string message)
		{
			return _agentServerDispatcher.SendSystemMessage(characterId, message);
		}
		public uint? LevelOf(uint characterId)
		{
			return _connections.ForCharacter(characterId)?.Session.Character.Level;
		}
		public string? NameOf(uint characterId)
		{
			return _connections.ForCharacter(characterId)?.Session.Character.Name;
		}
		private static Task SendApprenticeRpc(INetworkClient client, GameCharInterfaceProjectMethods method, byte[] body)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType), method, body));
		}
	}
}
