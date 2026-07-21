using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	// A captured static world object on a map - the decor/props (66AEDD50), interactive containers (F096251E),
	// zone/city labels (33A56FB0), destructibles (6BD56736) and climbing dynels (1FD6412B) that build the world
	// alongside the NPC spawns ([[NpcSpawn]]'s sibling). All share the object transform grammar (no pre-IIR block).
	// Keyed for dedup by (MapId, Position) - the stable spawn point; the handle is session-scoped. Imported from
	// captures by Faolan.Importer (WorldObjectExtractor); built into a wire body by WorldObjectBuilder.
	//
	// The whole object body is CONSTRUCTED from typed columns (#187): the SubOpcode (object CLASS), the transform
	// (position + rotation), the object message's id + name/DisplayName, and the geometry tail (KdTreeTail). These
	// share the FA015807 transform grammar but have NO pre-IIR block - the object message's length prefix follows the
	// transform directly. The captured raw `Data` packet has been RETIRED (like the NPC/pet blobs) - the importer
	// lifts these columns from the dump body at import time and never persists it.
	// Transform (PositionX/Y/Z + the RotationX/Y/Z/W quaternion + the Position/Rotation accessors) is inherited
	// from Base3DEntity, shared with the player Character and the NPC spawns.
	public class WorldObject : Base3DEntity
	{
		// The teleport destination (map + arrival position), populated for Teleport/ProxyTeleport. Extracted from the
		// N3Teleport PlayfieldProxy (dest map) + spawn-position blob in the capture where the door was used; the
		// arrival matches the destination map's reciprocal zone-line. Null when the object isn't a teleporter or its
		// destination hasn't been captured. Nullable floats mirror Base3DEntity's transform columns.
		public uint? DestinationMapId { get; set; }
		public uint MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; } = null!;

		public uint ObjectId { get; set; }
		public uint? TemplateItemId { get; set; } // proto field 8 (present on ~25%)

		public bool IsClosed { get; set; } // proto field 12

		// The bytes AFTER the identity message: the kd-tree / collision / bounds geometry (FUN_0080c620), per-object
		// but session-INDEPENDENT (static decor geometry, 406 distinct blobs). Lifted out of the captured packet into
		// its own column so the world-entry build constructs the whole object from the DB - identity (columns above) +
		// this tail (#187). This is the object's geometry TRUTH (not re-derivable), the counterpart of the NPC stat
		// columns; the captured raw `Data` packet has been RETIRED. The transform (pos/rot) is rebuilt from
		// Base3DEntity's columns and the live playfield token comes from the burst.
		public byte[]? KdTreeTail { get; set; }
		public float? DestinationX { get; set; }
		public float? DestinationY { get; set; }
		public float? DestinationZ { get; set; }

		// What using this object DOES ([[InteractAction]]). Default None = no column-driven interaction (decor, or a
		// door handled by the subop-based open/loot path). Teleport/ProxyTeleport carry a Destination* below; the
		// world-teleports importer sets these from captured UseObject->N3Teleport sequences. A zone-line marked
		// Teleport/ProxyTeleport with a NULL DestinationMapId is a teleporter we haven't sourced a destination for yet
		// (the server surfaces a message rather than dropping the player) - distinct from a None object that just
		// isn't a teleporter.
		public InteractAction InteractAction { get; set; }

		[MaxLength(128)]
		public string? DisplayName { get; set; } // proto field 3

		[MaxLength(128)]
		public string? Name { get; set; }

		public uint CapturedHandle { get; set; }
		public uint CarsGroup { get; set; } // proto field 5

		// SimpleDynelFullUpdateIIR (0x66AEDD50) identity, lifted from the opaque Data slice into typed columns so the
		// dynel's ProtoSimpleDynelIIR message is CONSTRUCTED from the DB rather than replayed (#187/#57; reader decoded
		// in docs/re/iir-subclass-readers.md, codec SimpleDynelMessageCodec). Name (above) is proto field 2. The two
		// nullable columns are absent on most dynels (3 captured field-shapes), so null = "field not present" - which
		// keeps the rebuilt message byte-exact to the capture. Non-dynel object classes leave these null/default and
		// keep using the Data slice. (Kdtree is always 0 on world dynels, emitted by the builder.)
		public uint Kdtree { get; set; } // proto field 1 (nonzero on ~32% - a kd-tree node ref)
		public uint SubOpcode { get; set; }
		public uint? QuestItemEffect { get; set; } // proto field 6 (present on ~23%)
		public uint? WaypointType { get; set; } // proto field 7 (present on zone-label objects)

		// #262: quest GATE - when set, this object is delivered ONLY to a player who has this quest ACTIVE (the
		// world-object analogue of an NPC's GateQuestIds / IsVisibleTo). null = ungated (delivered to everyone, the
		// default). A BUSINESS column, NOT a wire field, so it never affects the byte-exact rebuild. Nothing is gated
		// yet - the mechanism is in place for quest-conditional decor/pickups (e.g. gating the quarry Stone Bricks on
		// 12371) once needed. Currently only filtered at world entry; mid-session appear/disappear on quest state
		// change (like RefreshQuestVisibility for NPCs) is a follow-up.
		public uint? GateQuestId { get; set; }

		// #262: an AFTER-QUEST EFFECT. When the viewing player has EffectQuestId ACTIVE or COMPLETED, this object is
		// shown with the SimpleDynel visual-change state EffectDynelState (instead of its default), pushed both at world
		// entry and the instant the player reaches that quest state. Generalises the Casilda beach "wall drop" (the chain
		// decor's collision, dropped when the escort begins - EffectQuestId 12407, state 5): data now, not a hard-coded
		// lookup. null = no effect (the default). Business columns, NOT wire fields.
		public uint? EffectQuestId { get; set; }
		public byte EffectDynelState { get; set; }

		// Whether this object SPAWNS for a player holding `activeQuestIds` - i.e. whether it is sent at world entry. A
		// gated object the player doesn't qualify for simply isn't delivered (it never spawns; it is NOT spawned-and-
		// hidden). Ungated objects always spawn.
		public bool SpawnsFor(IReadOnlySet<uint> activeQuestIds)
		{
			return GateQuestId is not { } gate || (activeQuestIds != null && activeQuestIds.Contains(gate));
		}

		[NotMapped]
		public Vector3? DestinationPosition => DestinationMapId == null
			? null
			: new Vector3(DestinationX!.Value, DestinationY!.Value, DestinationZ!.Value); // Just throw if any are null, the DB should never have a partial position.
	}
}
