using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.GameServer;

namespace Faolan.Tests.Support
{
	// An IGameServerDispatcher that records the world-tier ops the CS services drive (team membership pushes,
	// teleports, minigame end/stop) and answers "done" to them, so a team / raid / group-finder / PvP flow can run
	// without a world listener behind it. Everything else answers "nobody is there". A test that needs to see a new
	// op adds a list here rather than a copy of its own.
	public sealed class RecordingGameServerDispatcher : IGameServerDispatcher
	{
		public List<(uint Recipient, uint Member, uint Team, uint Leader, bool Joined)> Membership { get; } = [];
		public List<(uint CharacterId, uint MapId, Vector3? Position)> Teleports { get; } = [];
		public List<(uint Character, uint WinningSide, MinigameResult Result)> Ended { get; } = [];
		public List<(uint Character, string Message)> Stopped { get; } = [];

		public Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined)
		{
			Membership.Add((recipientId, memberId, teamId, leaderId, joined));
			return Task.FromResult(true);
		}

		public Task<bool> TeleportCharacter(uint characterId, uint mapId, Vector3? position = null)
		{
			Teleports.Add((characterId, mapId, position));
			return Task.FromResult(true);
		}

		public Task<bool> SendMinigameMatchEnded(uint characterId, uint winningSide, MinigameResult yourResult)
		{
			Ended.Add((characterId, winningSide, yourResult));
			return Task.FromResult(true);
		}

		public Task<bool> SendMinigameForceStop(uint characterId, string message)
		{
			Stopped.Add((characterId, message));
			return Task.FromResult(true);
		}

		public bool DisconnectCharacter(uint characterId) => false;
		public CharacterLiveState? GetCharacterLiveState(uint characterId) => null;
		public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId) => [];
		public Task<bool> RunGmCommand(uint characterId, string commandText) => Task.FromResult(false);
		public Task<bool> SendGuildTag(uint characterId, string guildName) => Task.FromResult(false);
		public Task<bool> CreditCharacterCash(uint characterId, ulong amountTin) => Task.FromResult(false);
		public void Register(IGameServerProvider provider) { }
		public void Unregister(IGameServerProvider provider) { }
	}
}
