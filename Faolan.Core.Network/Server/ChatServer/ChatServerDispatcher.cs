using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.ChatServer
{
	public interface IChatServerDispatcher : IChatServerProvider
	{
		void Register(IChatServerProvider provider);
		void Unregister(IChatServerProvider provider);
	}

	/// <summary>
	///     The <see cref="ProviderDispatcher{TProvider}" /> slot for the chat listener: every op forwards to the
	///     one registered ChatServer listener, or answers empty when none is running.
	/// </summary>
	public sealed class ChatServerDispatcher : ProviderDispatcher<IChatServerProvider>, IChatServerDispatcher
	{
		public async Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null)
		{
			var provider = Provider;
			if (provider != null)
				await provider.CharacterSay(characterId, message, recipientCharacterIds);
		}

		public IEnumerable<INetworkClientSession> GetWorldSessions()
		{
			return Provider?.GetWorldSessions() ?? [];
		}

		public async Task<int> SendClaudeChannelMessage(ChatMarkup message)
		{
			var provider = Provider;
			return provider != null
				? await provider.SendClaudeChannelMessage(message)
				: 0;
		}

		public async Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message)
		{
			var provider = Provider;
			return provider != null && await provider.SendClaudeWhisper(targetCharacterId, message);
		}

		public async Task SendSystemMessage(uint characterId, ChatMarkup message)
		{
			var provider = Provider;
			if (provider != null)
				await provider.SendSystemMessage(characterId, message);
		}

		public async Task SystemBroadcast(ChatMarkup message)
		{
			var provider = Provider;
			if (provider != null)
				await provider.SystemBroadcast(message);
		}
	}
}
