using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// A harvested door->destination link: a zone-line world object (SourceMap + Name + spawn position) that, when
	// USED, teleported the player to DestMap at the arrival position. Src position keys it back to the WorldObject row
	// (two doors can share a name across maps); Dest is the truth extracted from the N3Teleport the server sent.
	public sealed record TeleportLink(uint SourceMapId, string SourceName, float SrcX, float SrcY, float SrcZ, uint DestMapId, float DestX, float DestY, float DestZ);

	// Harvests zone-line teleport destinations from captured gameplay: a client UseObject (BehaviorControlCenterIIR
	// actionType 0x12) on a door (DoorFullUpdateIIR 0x33A56FB0 OR SimpleDynelFullUpdateIIR 0x66AEDD50) followed by the
	// server's IntendedTeleport (0x10D27BC0) + N3Teleport (0x5A32F0D7). Both wire encodings are harvested (see
	// TeleportCodec):
	//   - CROSS-map (a full changemap handoff): the N3Teleport carries the destination WHOLE - a PlayfieldProxy
	//     (`[0x62][0xC79C][destMapId]`) + a spawn-position blob (tag 0x00018704 + Vector3 BE).
	//   - SAME-map (a same-playfield reposition, e.g. the Thirsty Dog Inn): the N3Teleport uses a 0xC7A0 proxy with
	//     map 0 and the arrival position INLINE; the destination MAP (== the current map) comes from the preceding
	//     IntendedTeleport's 0xC79C proxy.
	// The used object is resolved by precisely parsing the UseObject target handle and matching it against the doors
	// streamed on the CURRENT map. Only doors actually walked through in a capture are recoverable; the rest keep a
	// null destination (the server surfaces a message on use).
	public static class WorldTeleportExtractor
	{
		// UseObject -> teleport window. A cross-map handoff sends the N3Teleport within a handful of packets, but a
		// same-map (same-playfield) relocation streams the new locale's entities BETWEEN the IntendedTeleport and the
		// N3Teleport, so the arrival record can be ~90 packets out (Hoxxling inn: UseObject #10369 -> N3Teleport #10459).
		private const int LookaheadPackets = 150;

		// The world-object classes that can be teleporter doors: DoorFullUpdateIIR (0x33A56FB0, the Tarantia/castle
		// zone-lines) AND SimpleDynelFullUpdateIIR (0x66AEDD50, which also backs same-playfield doors like the Thirsty
		// Dog Inn). A link is only ever created when a used object is followed by an N3Teleport, so tracking the broader
		// SimpleDynel class as a candidate is harmless (decor/benches never emit a teleport). The module enriches the
		// same two classes (a subset of the WorldObjectExtractor object families that actually back doors).
		internal static readonly HashSet<uint> DoorSubOpcodes = new(WorldObjectClasses.Doors);

		public static List<TeleportLink> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var links = new List<TeleportLink>();
			uint currentMap = 0;
			// Zone-lines streamed on the current map: handle -> (name, position). Cleared on a map change (handles are
			// session-scoped and re-issued per playfield).
			var zoneLines = new Dictionary<uint, (string Name, float X, float Y, float Z)>();

			for (var i = 0; i < packets.Count; i++)
			{
				var p = packets[i];
				if (!p.IsIirCarrier)
					continue;

				if (p is { IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
				{
					if (p.TryParse(PlayfieldFCodec.Parse, out var change) && change.MapId != currentMap)
					{
						currentMap = change.MapId;
						zoneLines.Clear();
					}

					continue;
				}

				if (p.IsDownload && DoorSubOpcodes.Contains(p.SubOpcode) && currentMap != 0)
				{
					if (!p.TryParse(WorldObjectCodec.Parse, out var door) || !WorldObjectColumns.TryReadIdentity(p.Record, out var identity))
						continue;

					var at = door.Transform.FromProtobuf<ProtoGameCodeDynelIIR>().Position;
					zoneLines[door.Handle] = (identity.Name ?? string.Empty, at.X, at.Y, at.Z);
					continue;
				}

				// Client used an object: is the target one of the current map's zone-lines, and does a teleport follow?
				if (p.IsDownload || p.SubOpcode != BehaviorControlCenterCodec.Opcode)
					continue;
				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var interact) || interact.BehaviorId != (uint)BehaviorId.UseDynel)
					continue;

				// Only a handle that is actually a tracked door produces a link - the used object is named in the
				// behaviour TAIL, not in its args (see BehaviorTailScan).
				if (!zoneLines.TryGetValue(BehaviorTailScan.InteractTargetHandle(interact.Tail), out var used))
					continue;

				// The destination MAP comes from the IntendedTeleport (0x10D27BC0, PlayfieldProxy 0xC79C) - present for
				// BOTH a cross-map handoff and a same-playfield reposition. The arrival POSITION comes from the following
				// N3Teleport (0x5A32F0D7): a cross-map teleport carries it after the 0x00018704 blob tag; a same-map one
				// carries it INLINE right after the record's envelope byte (its own proxy is 0xC7A0, map 0). So read the
				// map from the IntendedTeleport and the position from the N3Teleport.
				uint destMap = 0;
				var haveDest = false;
				for (var j = i + 1; j < packets.Count && j <= i + LookaheadPackets; j++)
				{
					var q = packets[j];
					if (!q.IsIirCarrier || !q.IsDownload || !TeleportCodec.HandledTypes.Contains((IirTypes)q.SubOpcode))
						continue;

					if (!q.TryParse(TeleportCodec.Parse, out var teleport))
						continue;

					if (teleport is IntendedTeleportIIR begin)
					{
						if (DestinationMap(begin) is var dm and not 0)
						{
							destMap = dm;
							haveDest = true;
						}

						continue;
					}

					var arrive = (N3TeleportIIR)teleport;

					// The N3Teleport closes the sequence. If no IntendedTeleport carried the map, fall back to the
					// N3Teleport's own proxy (a cross-map record) or the current map (a same-map record).
					if (!haveDest)
					{
						destMap = DestinationMap(arrive) is var n3Map and not 0
							? n3Map
							: currentMap;
					}

					// A cross-map record states its arrival in the position blob; a same-map one carries it inline, and
					// an all-zero inline slot means the record does not name one.
					if (destMap != 0 && (arrive.Form == N3TeleportForm.CrossMap || arrive.Position != Vector3.Zero))
						links.Add(new TeleportLink(currentMap, used.Name, used.X, used.Y, used.Z, destMap, arrive.Position.X, arrive.Position.Y, arrive.Position.Z));

					break;
				}
			}

			return links;
		}

		// Merge duplicate harvests (the same door walked through in several captures) by (source map + name + spawn
		// position), keeping the first. Consistent captures yield the same destination; a genuine ambiguity would show
		// as two entries with the same source but different destinations (none observed).
		public static List<TeleportLink> Merge(IEnumerable<TeleportLink> all)
		{
			var seen = new Dictionary<(uint, string, float, float, float), TeleportLink>();
			foreach (var l in all)
				seen.TryAdd((l.SourceMapId, l.SourceName, l.SrcX, l.SrcY, l.SrcZ), l);

			return [.. seen.Values];
		}




		// The destination map a teleport record NAMES, or 0. Present on the IntendedTeleport for BOTH teleport kinds
		// and on a CROSS-map N3Teleport; a SAME-map N3Teleport's proxy is the revive class with map 0, so it names
		// none and the caller falls back to the IntendedTeleport map / the current map.
		private static uint DestinationMap(TeleportIIR teleport)
		{
			return teleport.Proxy.ClassWord == PlayfieldProxy.PlayfieldClass
				? teleport.Proxy.MapId
				: 0;
		}
	}
}
