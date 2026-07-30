using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Progression;

namespace Faolan.GameServer.World
{
	// A static world object as it exists IN A LOADED ZONE: its DB row plus the wire handle this zone streamed it
	// under. The handle is live state, minted by Zone.AllocateHandle when the zone loads its objects - it is not a
	// column.
	//
	// It used to be one: WorldObjects.CapturedHandle, the handle whichever capture the importer last saw the object in
	// happened to give it. That made a session-volatile value a persistence key (the wire notes in CLAUDE.md rule this
	// out) and it did not even hold up as a key - 999 distinct values across 1053 rows, so the zone's handle map
	// silently dropped colliding objects, and a re-import renumbered handles under anything that had written one down
	// (it silently emptied the create-editor scene, 760d8688).
	//
	// A row is only inert DATA; the handle belongs to the live copy of the map, the same way an NPC's does.
	public sealed record LiveWorldObject(uint Handle, WorldObject Row)
	{
		public Vector3 Position => Row.Position;

		public uint SubOpcode => Row.SubOpcode;

		// A harvest node's remaining health (#494). LIVE state, not a column, for the same reason the handle is: it is
		// per-loaded-zone. Retail streams each node with whatever health the shared world has left it at, and nodes
		// regrow - we model neither the sharing nor a respawn timer, so a node starts full on every map load. That is a
		// deliberate simplification, not an oversight: no respawn cadence has been measured.
		//
		// Null for everything that is not a harvest node, so this doubles as "is this being harvested".
		public uint? Health { get; set; }

		// True while this node still has something to give. Health bottoms out at 1 in every capture, never 0, and the
		// node despawns there - so exhausted is <= ExhaustedHealth rather than == 0.
		public bool IsHarvestable => HarvestCatalog.IsHarvestable(Row.Name) && (Health ?? HarvestCatalog.NodeMaxHealth) > HarvestCatalog.ExhaustedHealth;

		// The quest gate travels with the row (#262): an object no player-quest reveals is absent entirely.
		public bool SpawnsFor(IReadOnlySet<uint> quests)
		{
			return Row.SpawnsFor(quests);
		}
	}
}
