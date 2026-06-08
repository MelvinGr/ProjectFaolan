namespace Faolan.Core.Database.Tables
{
	// A captured NPC entity spawn (FA015807) on a map, stored raw for future DB-driven world entry: the server
	// will be able to replay these per map (re-addressing the session handle) the way the beach world-burst does
	// today. Keyed for dedup by (MapId, Position) - the stable spawn point; the entity handle is session-scoped and
	// the name is best-effort. Imported from captures by Faolan.Importer (NpcSpawnExtractor).
	public class NpcSpawn : BaseEntity
	{
		public byte[] Data { get; set; }
		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }
		public string Name { get; set; }
		public uint CapturedHandle { get; set; }
		public uint MapId { get; set; }
	}
}
