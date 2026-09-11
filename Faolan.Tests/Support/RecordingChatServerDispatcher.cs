using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;

namespace Faolan.Tests.Support
{
	// An IChatServerDispatcher that records the channel moves the CS services make (a member's ~Team / ~Guild
	// subscription following a roster change) and swallows everything else.
	public sealed class RecordingChatServerDispatcher : IChatServerDispatcher
	{
		public List<(uint Character, uint? Team)> TeamChannel { get; } = [];
		public List<(uint Character, uint? Guild)> GuildChannel { get; } = [];
		public List<(uint Character, ChatMarkup Message)> SystemMessages { get; } = [];

		public Task RefreshTeamChannel(uint characterId, uint? teamId)
		{
			TeamChannel.Add((characterId, teamId));
			return Task.CompletedTask;
		}

		public Task RefreshGuildChannel(uint characterId, uint? guildId)
		{
			GuildChannel.Add((characterId, guildId));
			return Task.CompletedTask;
		}

		public Task SendSystemMessage(uint characterId, ChatMarkup message)
		{
			SystemMessages.Add((characterId, message));
			return Task.CompletedTask;
		}

		public Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null) => Task.CompletedTask;
		public Task NpcBark(uint npcHandle, NpcChatterBark bark, IReadOnlyCollection<uint>? recipientCharacterIds = null) => Task.CompletedTask;
		public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
		public Task<int> SendClaudeChannelMessage(ChatMarkup message) => Task.FromResult(0);
		public Task RefreshPlayfieldChannel(uint characterId, uint mapId) => Task.CompletedTask;
		public Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message) => Task.FromResult(false);
		public Task SystemBroadcast(ChatMarkup message) => Task.CompletedTask;
		public void Register(IChatServerProvider provider) { }
		public void Unregister(IChatServerProvider provider) { }
	}
}
