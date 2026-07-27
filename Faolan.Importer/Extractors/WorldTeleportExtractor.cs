using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;

using Faolan.Core.Network.Constants;

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
		private const int ActionTypeOffset = 18; // BCC body: [len][type][unk0][clientInst][unk1 u16] -> actionType u32

		// UseObject -> teleport window. A cross-map handoff sends the N3Teleport within a handful of packets, but a
		// same-map (same-playfield) relocation streams the new locale's entities BETWEEN the IntendedTeleport and the
		// N3Teleport, so the arrival record can be ~90 packets out (Hoxxling inn: UseObject #10369 -> N3Teleport #10459).
		private const int LookaheadPackets = 150;
		private const uint IntendedTeleportSubOpcode = (uint)GameServer0X2000Opcodes.IntendedTeleportIIR; // 0x10D27BC0
		private const uint MapChangeSubOpcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR; // 0x5D85BFC7
		private const uint N3TeleportSubOpcode = (uint)GameServer0X2000Opcodes.N3TeleportIIR; // 0x5A32F0D7

		private const uint UseObjectActionType = (uint)ActionType.UseObject; // 0x12
		private const uint UseObjectSubOpcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR; // 0x642CD3D6

		// The world-object classes that can be teleporter doors: DoorFullUpdateIIR (0x33A56FB0, the Tarantia/castle
		// zone-lines) AND SimpleDynelFullUpdateIIR (0x66AEDD50, which also backs same-playfield doors like the Thirsty
		// Dog Inn). A link is only ever created when a used object is followed by an N3Teleport, so tracking the broader
		// SimpleDynel class as a candidate is harmless (decor/benches never emit a teleport). The module enriches the
		// same two classes (a subset of the WorldObjectExtractor object families that actually back doors).
		internal static readonly HashSet<uint> DoorSubOpcodes =
		[
			(uint)GameServer0X2000Opcodes.DoorFullUpdateIIR, // 0x33A56FB0
			(uint)GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR // 0x66AEDD50
		];

		// The spawn-position blob tag that precedes the Vector3 arrival (TeleportCodec.PositionBlobTag).
		private static readonly byte[] PositionBlobTag = [0x00, 0x01, 0x87, 0x04];

		// N3Teleport PlayfieldProxy: version 0x62, u32[0]=class word 0xC79C, u32[1]=destination map (TeleportCodec).
		private static readonly byte[] ProxyMarker = [0x62, 0x00, 0x00, 0xC7, 0x9C];

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
				if (p.Opcode != RpcOpcode.IirCarrier)
					continue;

				var body = WorldObjectExtractor.GetBody(p.Bytes);
				if (body.Length < 38)
					continue;

				if (p is { IsDownload: true, SubOpcode: MapChangeSubOpcode })
				{
					var map = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					if (map != currentMap)
					{
						currentMap = map;
						zoneLines.Clear();
					}

					continue;
				}

				if (p.IsDownload && DoorSubOpcodes.Contains(p.SubOpcode) && currentMap != 0)
				{
					if (body.Length < SpawnPacketBuilder.PosZOffset + 4)
						continue;

					var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
					var (_, name) = WorldObjectExtractor.ReadMessage(body);
					zoneLines[handle] = (name, WorldObjectExtractor.ReadLE(body, SpawnPacketBuilder.PosXOffset), WorldObjectExtractor.ReadLE(body, SpawnPacketBuilder.PosYOffset),
						WorldObjectExtractor.ReadLE(body, SpawnPacketBuilder.PosZOffset));
					continue;
				}

				// Client used an object: is the target one of the current map's zone-lines, and does a teleport follow?
				if (p.IsDownload || p.SubOpcode != UseObjectSubOpcode)
					continue;
				if (body.Length < ActionTypeOffset + 4)
					continue;
				if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(ActionTypeOffset, 4)) != UseObjectActionType)
					continue;

				if (!TryFindUsedZoneLine(body, zoneLines, out var used))
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
					if (q.Opcode != RpcOpcode.IirCarrier || !q.IsDownload)
						continue;

					var qbody = WorldObjectExtractor.GetBody(q.Bytes);
					if (q.SubOpcode == IntendedTeleportSubOpcode)
					{
						if (TryParseProxyDestMap(qbody, out var dm))
						{
							destMap = dm;
							haveDest = true;
						}

						continue;
					}

					if (q.SubOpcode != N3TeleportSubOpcode)
						continue;

					// The N3Teleport closes the sequence. If no IntendedTeleport carried the map, fall back to the
					// N3Teleport's own proxy (a cross-map record) or the current map (a same-map record).
					if (!haveDest)
					{
						destMap = TryParseProxyDestMap(qbody, out var n3Map)
							? n3Map
							: currentMap;
					}

					if (destMap != 0 && TryParseArrival(qbody, out var ax, out var ay, out var az))
						links.Add(new TeleportLink(currentMap, used.Name, used.X, used.Y, used.Z, destMap, ax, ay, az));

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

		private static int IndexOf(byte[] haystack, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= haystack.Length; i++)
			{
				var match = true;
				for (var k = 0; k < needle.Length; k++)
				{
					if (haystack[i + k] != needle[k])
					{
						match = false;
						break;
					}
				}

				if (match)
					return i;
			}

			return -1;
		}

		// The used object's handle, parsed precisely from the UseObject (0x12) body. Layout after the fixed header
		// (GetBody yields [0:4]=len, [4:8]=subop, [12:16]=carrier handle): [16:18]=unk1, [18:22]=actionType,
		// [22:26]=protobuf length, [26 : 26+pblen]=protobuf args, then the trailer [from u32][target u32][subaction u32]
		// where `target` is the used object's handle (from = its typetag). A precise parse (vs scanning every 4-byte
		// word) avoids a false hit against the broader SimpleDynel candidate set. Only a handle that is actually a
		// tracked door produces a link.
		private static bool TryFindUsedZoneLine(byte[] body, Dictionary<uint, (string Name, float X, float Y, float Z)> zoneLines, out (string Name, float X, float Y, float Z) used)
		{
			used = default;
			if (body.Length < 26)
				return false;

			var pblen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(22, 4));
			if (pblen < 0)
				return false;

			var targetOffset = 26 + pblen + 4; // skip the protobuf args + the [from] (typetag) word
			if (targetOffset + 4 > body.Length)
				return false;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(targetOffset, 4));
			return zoneLines.TryGetValue(handle, out used);
		}

		// Arrival Vector3 from an N3Teleport body. A CROSS-map teleport carries it after the 0x00018704 position-blob
		// tag; a SAME-map (same-playfield) teleport carries it INLINE right after the record's envelope byte
		// (body: [0:4] len [4:8] subop [8:12] class [12:16] handle [16] env [17:29] position). Prefer the tagged form;
		// the inline slot is all-zero in a cross-map record, so an all-zero inline reads as 'no position'.
		private static bool TryParseArrival(byte[] body, out float x, out float y, out float z)
		{
			x = y = z = 0;

			var pos = IndexOf(body, PositionBlobTag);
			if (pos >= 0 && pos + PositionBlobTag.Length + 12 <= body.Length)
			{
				var o = pos + PositionBlobTag.Length;
				x = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(o, 4));
				y = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(o + 4, 4));
				z = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(o + 8, 4));
				return true;
			}

			const int inlineOffset = 17; // right after [12:16] handle + [16] envelope byte
			if (inlineOffset + 12 > body.Length)
				return false;

			x = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(inlineOffset, 4));
			y = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(inlineOffset + 4, 4));
			z = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(inlineOffset + 8, 4));
			return x != 0 || y != 0 || z != 0;
		}

		// Destination map from a PlayfieldProxy: the u32 after the 0x62/0xC79C marker (TeleportCodec). Present in the
		// IntendedTeleport (BOTH teleport kinds) and in a CROSS-map N3Teleport; a SAME-map N3Teleport uses a 0xC7A0
		// proxy with map 0, so this returns false for it (the caller then uses the IntendedTeleport map / current map).
		private static bool TryParseProxyDestMap(byte[] body, out uint destMap)
		{
			destMap = 0;
			var proxy = IndexOf(body, ProxyMarker);
			if (proxy < 0 || proxy + ProxyMarker.Length + 4 > body.Length)
				return false;

			destMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(proxy + ProxyMarker.Length, 4));
			return destMap != 0;
		}
	}
}
