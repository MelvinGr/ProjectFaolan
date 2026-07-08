using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.AgentServer
{
	public interface IAgentServerDispatcher : IAgentServerProvider
	{
		void Register(IAgentServerProvider provider);
		void Unregister(IAgentServerProvider provider);
	}

	public class AgentServerDispatcher : IAgentServerDispatcher
	{
		private readonly Lock _lock = new Lock();
		private IAgentServerProvider? _provider;

		public void Register(IAgentServerProvider provider)
		{
			lock (_lock)
			{
				if (_provider != null && _provider != provider)
					throw new InvalidOperationException("Only one agent server provider can be registered at a time.");

				_provider = provider;
			}
		}

		public void Unregister(IAgentServerProvider provider)
		{
			lock (_lock)
			{
				if (_provider == provider)
					_provider = null;
			}
		}

		public async Task CharacterSay(uint characterId, string message, IReadOnlyCollection<uint> recipientCharacterIds = null)
		{
			if (_provider != null)
				await _provider.CharacterSay(characterId, message, recipientCharacterIds);
		}

		public IReadOnlyList<SessionInfo> GetSessions()
		{
			lock (_lock)
				return _provider?.GetSessions() ?? [];
		}

		public async Task<int> SendClaudeChannelMessage(string message)
		{
			if (_provider != null)
				return await _provider.SendClaudeChannelMessage(message);

			return 0;
		}

		public async Task<bool> SendClaudeWhisper(uint targetCharacterId, string message)
		{
			if (_provider != null)
				return await _provider.SendClaudeWhisper(targetCharacterId, message);

			return false;
		}

		public async Task SendSystemMessage(uint characterId, string message)
		{
			if (_provider != null)
				await _provider.SendSystemMessage(characterId, message);
		}

		public async Task SystemBroadcast(string message)
		{
			if (_provider != null)
				await _provider.SystemBroadcast(message);
		}
	}
}
