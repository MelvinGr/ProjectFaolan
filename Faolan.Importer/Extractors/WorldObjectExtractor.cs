using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

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

		// proto field 12, as this capture saw it - null when ABSENT. The ONE identity field observed to change
		// during play, so it is the one Merge cannot resolve by taking whichever capture sorted first (#554). Carried
		// here rather than re-read from `Data` at merge time so there is a single place the field is parsed.
		public bool? IsClosed { get; init; }
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
				// "Chest") - spawns dynamically on a kill and fades after 300s (WorldBroadcaster.LootBagExpirySeconds);
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
					DisplayName = displayName,
					IsClosed = identity.ShouldSerializeIsClosed()
						? identity.IsClosed
						: null
				});
			}

			return entries;
		}

		// Dedup by (map, spawn point, identity). Identity (the display_name) is part of the key because two distinct
		// props can occupy the EXACT same coordinates (#222) - position alone would drop one.
		//
		// The structural fields agree across captures, so for those any candidate would do. `IsClosed` does NOT
		// (#554): the map 4020 "Wharf Door" is captured closed in one session and open in another, and taking the
		// first-seen entry made ordinal CAPTURE FILENAME order the thing that decided a door's state. Adding a
		// capture that happened to sort earlier and caught that door open would have flipped the shipped row, and no
		// test would have noticed - both are legitimate wire shapes, so the whole-corpus lift gate passes either way.
		//
		// `IsClosed` is SESSION state, not a placement fact: a door captured open was opened by the capturing player,
		// so no capture is evidence of anything but that moment. Since #598 the row does NOT store it - the live world
		// owns it (LiveWorldObject.IsClosed) and the lift feeds the captured reading into its byte comparison - so
		// which candidate wins no longer decides anything that ships.
		//
		// The ranking stays anyway, and is not dead weight: it is what keeps the dedup a function of what the entries
		// SAY rather than of the order the capture files happened to sort in. Candidates are ranked closed-present >
		// present > absent, a property of the ENTRY. Falling back to first-seen here would put filename order back in
		// charge of which capture's bytes every other field is lifted from - the #554 bug, one field over.
		public static List<WorldObjectEntry> Merge(IEnumerable<WorldObjectEntry> all)
		{
			return Merge(all, out _);
		}

		/// <summary>
		///     As <see cref="Merge(IEnumerable{WorldObjectEntry})" />, reporting how many objects were captured in
		///     DISAGREEING states - so an import can say it resolved them rather than resolving them silently. A
		///     count above zero is not a fault; it is the corpus having seen a door both ways.
		/// </summary>
		public static List<WorldObjectEntry> Merge(IEnumerable<WorldObjectEntry> all, out int stateConflicts)
		{
			stateConflicts = 0;
			var seen = new Dictionary<(uint, float, float, float, string), WorldObjectEntry>();
			foreach (var e in all)
			{
				var key = (e.MapId, e.X, e.Y, e.Z, e.DisplayName);
				if (!seen.TryGetValue(key, out var kept))
				{
					seen[key] = e;
					continue;
				}

				if (kept.IsClosed == e.IsClosed)
					continue;

				stateConflicts++;
				if (Rank(e) > Rank(kept))
					seen[key] = e;
			}

			return seen.Values.ToList();
		}

		// Closed-and-explicit beats explicit beats absent. Deliberately NOT a tie-break that falls through to
		// "whichever came first": every candidate is ranked by what it SAYS, so the winner is the same whatever
		// order the captures are read in.
		private static int Rank(WorldObjectEntry e)
		{
			return e.IsClosed switch
			{
				true => 2,
				false => 1,
				null => 0
			};
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
