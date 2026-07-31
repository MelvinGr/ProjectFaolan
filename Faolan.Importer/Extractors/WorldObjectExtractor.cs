using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Extractors
{
	public sealed record WorldObjectEntry(uint MapId, uint SubOpcode, uint Handle, float X, float Y, float Z, byte[] Data)
	{
		public uint ObjectId { get; init; }
		public float RotW { get; init; }
		public float RotX { get; init; }
		public float RotY { get; init; }
		public float RotZ { get; init; }

		// The display_name identity (localized id) that disambiguates two props sharing an exact position (#222).
		public string DisplayName { get; init; } = string.Empty;
		public string Name { get; init; } = string.Empty;
	}

	// Extracts the static world objects per map from a capture: tracks the current map (MapChange 5D85BFC7), then
	// for each world-object packet reads the handle + transform (same offsets as the entity spawns) and the object
	// message's id (f1) + name (f2). The object classes are the FullUpdate families that share the object transform
	// grammar (0x2C group, no pre-IIR block): 66AEDD50 decor/props, F096251E interactive containers, 33A56FB0 zone
	// labels, 6BD56736 destructibles, 1FD6412B climbing dynels. Sibling of NpcSpawnExtractor; dedup key is (map, pos).
	public static class WorldObjectExtractor
	{
		public static List<WorldObjectEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<WorldObjectEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == PlayfieldFCodec.Opcode)
				{
					if (p.TryParse(PlayfieldFCodec.Parse, out var change))
						currentMap = change.MapId;
					continue;
				}

				if (!WorldObjectClasses.Placed.Contains(p.SubOpcode) || currentMap == 0)
					continue;

				if (!p.TryParse(WorldObjectCodec.Parse, out var obj))
					continue; // a capture truncated mid-record

				// EVERY class in the family carries the same identity message in the same place - kdtree in field 1,
				// name in field 2 - so one codec read replaces both the hand-rolled protobuf walker this file used to
				// carry and the separate DisplayName read next to it.
				if (!WorldObjectColumns.TryReadIdentity(p.Record, out var identity))
					continue;

				var handle = obj.Handle;
				var objectId = identity.Kdtree;
				var name = identity.Name ?? string.Empty;
				var displayName = identity.DisplayName ?? string.Empty;
				var transform = obj.Transform.FromProtobuf<ProtoGameCodeDynelIIR>();
				var position = transform.Position;
				var rotation = transform.Rotation;

				// LootBagFullUpdateIIR (F096251E) is shared by TWO things: transient loot-container DROPS and static
				// world-builder-PLACED containers. A drop - a plain "Bag of loot" OR a boss/raid CHEST ("Large Chest",
				// "Chest") - spawns dynamically on a kill and fades after ~120s (WorldBroadcaster.LootBagExpirySeconds);
				// it must NOT be baked as a permanent WorldObject (that was how a stray "Large Chest" ended up standing
				// on the Tortage beach - #363). Placed containers are constructed the SAME way (bigger container is just
				// a bigger LootBagTemplate.Variant), so the NAME can't tell a drop from a placement; the signal is the
				// world-builder [script-tag] in the name ([N7FU]/[QRON]/...), which only a PLACED object carries.
				if (p.SubOpcode == LootContainerTemplate.ContainerClass && IsTransientLootContainer(name))
					continue;

				entries.Add(new WorldObjectEntry(currentMap, p.SubOpcode, handle, position.X, position.Y, position.Z, p.Bytes)
				{
					RotX = rotation.X,
					RotY = rotation.Y,
					RotZ = rotation.Z,
					RotW = rotation.W,
					ObjectId = objectId,
					Name = name,
					DisplayName = displayName
				});
			}

			return entries;
		}

		// Dedup by (map, spawn point, identity); keep the first seen. Identity (the display_name) is part of the key
		// because two distinct props can occupy the EXACT same coordinates (#222) - position alone would drop one.
		// ⚠️ First-seen means ordinal CAPTURE FILENAME order, which is arbitrary for any field that changes during
		// play: the same door is captured both closed and open, so which state ships is decided by file order rather
		// than by the door (Gitea #554).
		public static List<WorldObjectEntry> Merge(IEnumerable<WorldObjectEntry> all)
		{
			var seen = new Dictionary<(uint, float, float, float, string), WorldObjectEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.X, e.Y, e.Z, e.DisplayName), e);

			return seen.Values.ToList();
		}

		// A LootBagFullUpdateIIR object is a TRANSIENT loot-container DROP (skip it) unless it was PLACED by the world
		// builder, which every placed/scripted container in the corpus signals with a bracketed [script-tag] in its
		// NAME ([N7FU], [QRON], ...). The container NAME alone is NOT the signal: a boss-drop chest is named exactly
		// like a placed one ("Chest", "Large Chest"), so keying on "chest" wrongly baked the Large Chest drops (#363);
		// only the [script-tag] distinguishes a placement from a drop.
		internal static bool IsTransientLootContainer(string name)
		{
			return !(name ?? "").Contains('[');
		}




	}
}
