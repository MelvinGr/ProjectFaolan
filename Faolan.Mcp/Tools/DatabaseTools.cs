using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Faolan.Core.Database;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	/// Read-only tools over faolan.db, served through the same <see cref="IDatabaseRepository"/> the live
	/// server uses. One tool per action while the surface is small; promote to a search+execute pair if it grows.
	/// </summary>
	[McpServerToolType]
	public sealed class DatabaseTools
	{
		// EF entities carry navigation properties (the Core context uses lazy-loading proxies), so guard against
		// reference cycles and cap depth. Tools return detached/no-tracking results, so no lazy load fires here.
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			ReferenceHandler = ReferenceHandler.IgnoreCycles,
			MaxDepth = 8,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		private readonly IDatabaseRepository _db;

		public DatabaseTools(IDatabaseRepository db) => _db = db;

		[McpServerTool(Name = "get_item")]
		[Description("Look up a single item from faolan.db by its numeric id. Returns the item record as JSON.")]
		public async Task<string> GetItem([Description("Numeric item id")] uint id)
		{
			var item = await _db.GetItem(id);
			return item is null ? $"No item with id {id}." : Serialize(item);
		}

		[McpServerTool(Name = "list_maps")]
		[Description("List every map known to the server (id + name + metadata). Use this to discover map ids for other tools.")]
		public async Task<string> ListMaps()
		{
			var maps = await _db.GetMaps();
			return Serialize(maps);
		}

		[McpServerTool(Name = "get_map_npc_spawns")]
		[Description("List the configured NPC spawn entries for a given map id.")]
		public async Task<string> GetMapNpcSpawns([Description("Numeric map id")] uint mapId)
		{
			var spawns = await _db.GetNpcSpawns(mapId);
			return Serialize(spawns);
		}

		[McpServerTool(Name = "get_map_world_objects")]
		[Description("List the world objects (props, doors, interactables) configured for a given map id.")]
		public async Task<string> GetMapWorldObjects([Description("Numeric map id")] uint mapId)
		{
			var objects = await _db.GetWorldObjects(mapId);
			return Serialize(objects);
		}

		[McpServerTool(Name = "get_spell")]
		[Description("Look up a single spell from faolan.db by its numeric id. Returns the spell record as JSON.")]
		public async Task<string> GetSpell([Description("Numeric spell id")] uint id)
		{
			var spell = await _db.GetSpell(id);
			return spell is null ? $"No spell with id {id}." : Serialize(spell);
		}

		private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
	}
}
