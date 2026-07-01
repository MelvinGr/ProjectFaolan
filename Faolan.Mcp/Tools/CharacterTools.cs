using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Network.GameServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Deep per-character inspection: the persisted DB state (identity, wallet, inventory, quests, tags)
	///     merged with the live world state (position/vitals/buffs via <see cref="IGameServerDispatcher" />)
	///     when the character is connected.
	/// </summary>
	[McpServerToolType]
	public sealed class CharacterTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		private readonly IDatabaseRepository _db;
		private readonly IGameServerDispatcher _gameServer;

		public CharacterTools(IDatabaseRepository db, IGameServerDispatcher gameServer)
		{
			_db = db;
			_gameServer = gameServer;
		}

		[McpServerTool(Name = "get_character"),
		 Description(
			 "Inspect one character: persisted DB state (level, wallet, inventory rows, quests, tags) plus live world state (position, vitals, buffs) when connected. Character ids: see list_world_sessions or list_sessions. Read-only.")]
		public async Task<string> GetCharacter([Description("Numeric character id")] uint characterId)
		{
			var character = await _db.GetCharacter(characterId);
			if (character == null)
				return $"No character with id {characterId}.";

			var inventory = await _db.GetCharacterInventory(characterId);
			var quests = await _db.GetCharacterQuests(characterId);
			var tags = await _db.GetCharacterTags(characterId);

			return JsonSerializer.Serialize(new
				{
					character.Id,
					character.Name,
					character.Level,
					character.Experience,
					MoneyCopper = character.Money,
					character.MapId,
					character.AccountId,
					character.GuildId,
					Live = _gameServer.GetCharacterLiveState(characterId), // null = not connected to a world listener
					Inventory = inventory
					.OrderBy(r => r.Container)
					.ThenBy(r => r.Slot)
					.Select(r => new
					{
						r.Container,
						r.Slot,
						r.ItemId,
						ItemName = r.Item?.Name,
						r.Count
					}),
					Quests = quests.Select(q => new
					{
						q.QuestId,
						State = q.State.ToString(),
						q.GoalProgress,
						q.GoalRequired,
						q.GoalTargetName
					}),
					Tags = tags
				},
				JsonOptions);
		}
	}
}
