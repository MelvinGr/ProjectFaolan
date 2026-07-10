using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Read-only tools over faolan.db, served through the same <see cref="IDatabaseRepository" /> the live
	///     server uses. One tool per action while the surface is small; promote to a search+execute pair if it grows.
	/// </summary>
	[McpServerToolType]
	public sealed class DatabaseTools
	{
		// EF entities carry navigation properties (populated via Include; the context does not enable lazy-loading
		// proxies), so guard against reference cycles and cap depth. Tools return detached/no-tracking results.
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			ReferenceHandler = ReferenceHandler.IgnoreCycles,
			MaxDepth = 8,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		private readonly IDatabaseRepository _db;

		public DatabaseTools(IDatabaseRepository db)
		{
			_db = db;
		}

		[McpServerTool(Name = "get_item"), Description("Look up a single item from faolan.db by its numeric id. Returns the item record as JSON.")]
		public async Task<string> GetItem([Description("Numeric item id")] uint id)
		{
			var item = await _db.GetItem(id);
			return item == null
				? $"No item with id {id}."
				: Serialize(item);
		}

		[McpServerTool(Name = "get_map_npc_spawns"), Description("List the configured NPC spawn entries for a given map id.")]
		public async Task<string> GetMapNpcSpawns([Description("Numeric map id")] uint mapId)
		{
			var spawns = await _db.GetNpcSpawns(mapId);
			return Serialize(spawns);
		}

		[McpServerTool(Name = "get_map_world_objects"), Description("List the world objects (props, doors, interactables) configured for a given map id.")]
		public async Task<string> GetMapWorldObjects([Description("Numeric map id")] uint mapId)
		{
			var objects = await _db.GetWorldObjects(mapId);
			return Serialize(objects);
		}

		[McpServerTool(Name = "get_spell"), Description("Look up a single spell from faolan.db by its numeric id. Returns the spell record as JSON.")]
		public async Task<string> GetSpell([Description("Numeric spell id")] uint id)
		{
			var spell = await _db.GetSpell(id);
			return spell == null
				? $"No spell with id {id}."
				: Serialize(spell);
		}

		[McpServerTool(Name = "list_maps"), Description("List every map known to the server (id + name + metadata). Use this to discover map ids for other tools.")]
		public async Task<string> ListMaps()
		{
			var maps = await _db.GetMaps();
			return Serialize(maps);
		}

		[McpServerTool(Name = "search_items"),
		 Description(
			 "Search items by name substring (case-insensitive). Returns id/name/level/type/archetype (type = Normal/Quest/Resource/Equipped classification) - use get_item for the full record, add_item to grant one.")]
		public async Task<string> SearchItems([Description("Name substring to match, e.g. \"potion\"")] string name, [Description("Max results (default 25)")] int limit = 25)
		{
			var items = await _db.SearchItems(name, limit);
			return items.Length == 0
				? $"No items matching '{name}'."
				: Serialize(items.Select(i => new
				{
					i.Id,
					i.Name,
					i.Level,
					Type = i.Type.ToString(),
					i.Hash
				}));
		}

		[McpServerTool(Name = "search_npc_spawns"),
		 Description("Search captured NPC spawn rows by name substring (case-insensitive), across all maps. Spawnable=true rows can be spawned at a player with spawn_npc.")]
		public async Task<string> SearchNpcSpawns([Description("Name substring to match, e.g. \"croc\"")] string name, [Description("Max results (default 50)")] int limit = 50)
		{
			var spawns = await _db.SearchNpcSpawns(name, limit);
			return spawns.Length == 0
				? $"No NPC spawns matching '{name}'."
				: Serialize(spawns.Select(n => new
				{
					n.Id,
					n.Npc?.Name, // #244: name on the Npc type, community on its StatCore
					n.MapId,
					n.Level,
					n.Health,
					Community = n.Npc?.StatCore?.Community ?? 0,
					Position = new
					{
						X = n.PositionX,
						Y = n.PositionY,
						Z = n.PositionZ
					},
					Spawnable = n.Npc?.StatCore?.IirHeader != null // builds from columns via TryBuildLive
				}));
		}

		private static string Serialize(object value)
		{
			return JsonSerializer.Serialize(value, JsonOptions);
		}
	}
}
