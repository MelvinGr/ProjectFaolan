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
		public bool DisconnectCharacter(uint characterId)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
				return false;

			client.Stop();
			return true;
		}

		public CharacterLiveState GetCharacterLiveState(uint characterId)
		{
			var client = ClientForCharacter(characterId);
			var entity = client == null
				? null
				: _simulation.PlayerWorldEntity(client);
			if (entity == null)
				return null;

			var buffs = entity.ActiveBuffs.Keys.OrderBy(id => id).Select(id => $"0x{id:X8}").ToArray();
			return new CharacterLiveState(characterId, entity.Name, entity.Handle, entity.MapId, entity.Position.X, entity.Position.Y, entity.Position.Z, entity.Vitals?.Health, entity.Vitals?.MaxHealth, buffs);
		}

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

		public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId)
		{
			// Observer view: union every live instance (copy) of the map (#268), so the MCP tool sees all entities
			// regardless of which copy they're in.
			return _simulation
				.World
				.ZonesForMap(mapId)
				.SelectMany(z => z.Entities)
				.Select(e => new ZoneEntityInfo(e.Handle, e.Name, e.IsPlayer ? "player" : e.IsLoot ? "loot" : "npc", e.Level, e.Hostile, e.IsDead, e.Vitals?.Health, e.Vitals?.MaxHealth, e.Position.X, e.Position.Y, e.Position.Z))
				.OrderBy(e => e.Handle)
				.ToArray();
		}

		public async Task<bool> RunGmCommand(uint characterId, string commandText)
		{
			var client = ClientForCharacter(characterId);
			if (client == null)
				return false;

			// HandleGmCommand strips a single leading prefix char (".teleport" -> "teleport"), so ensure one is present.
			if (commandText.Length == 0 || char.IsLetterOrDigit(commandText[0]))
				commandText = "." + commandText;

			await HandleGmCommand(client, commandText);
			return true;
		}
	}
}
