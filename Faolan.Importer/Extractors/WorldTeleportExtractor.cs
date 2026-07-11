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
	public sealed record TeleportLink(
		uint SourceMapId, string SourceName, float SrcX, float SrcY, float SrcZ,
		uint DestMapId, float DestX, float DestY, float DestZ);

	// Harvests zone-line teleport destinations from captured gameplay: a client UseObject (BehaviorControlCenterIIR
	// actionType 0x12) on a DoorFullUpdateIIR (0x33A56FB0) followed by the server's N3Teleport (0x5A32F0D7). The
	// N3Teleport carries the destination WHOLE - a PlayfieldProxy (`[0x62][0xC79C][destMapId]`) + a spawn-position
	// blob (tag 0x00018704 + Vector3 BE) - so no name-matching heuristic is needed (decoded in TeleportCodec /
	// docs/re decode of the castle-exit capture). The used object is resolved by matching the used handle against the
	// zone-lines streamed on the CURRENT map. Only doors actually walked through in a capture are recoverable; the
	// rest keep a null destination (the server surfaces a message on use).
	public static class WorldTeleportExtractor
	{
		private const uint Opcode2000 = 0x2000;
		private const uint MapChangeSubOpcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR; // 0x5D85BFC7
		private const uint ZoneLineSubOpcode = (uint)GameServer0X2000Opcodes.DoorFullUpdateIIR; // 0x33A56FB0
		private const uint UseObjectSubOpcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR; // 0x642CD3D6
		private const uint N3TeleportSubOpcode = (uint)GameServer0X2000Opcodes.N3TeleportIIR; // 0x5A32F0D7

		private const uint UseObjectActionType = (uint)ActionType.UseObject; // 0x12
		private const int ActionTypeOffset = 18; // BCC body: [len][type][unk0][clientInst][unk1 u16] -> actionType u32

		// N3Teleport PlayfieldProxy: version 0x62, u32[0]=class word 0xC79C, u32[1]=destination map (TeleportCodec).
		private static readonly byte[] ProxyMarker = [0x62, 0x00, 0x00, 0xC7, 0x9C];
		// The spawn-position blob tag that precedes the Vector3 arrival (TeleportCodec.PositionBlobTag).
		private static readonly byte[] PositionBlobTag = [0x00, 0x01, 0x87, 0x04];

		private const int LookaheadPackets = 40; // UseObject -> N3Teleport window

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
				if (p.Opcode != Opcode2000)
					continue;

				var body = WorldObjectExtractor.GetBody(p.Bytes);
				if (body.Length < 38)
					continue;

				if (p.IsDownload && p.SubOpcode == MapChangeSubOpcode)
				{
					var map = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					if (map != currentMap)
					{
						currentMap = map;
						zoneLines.Clear();
					}

					continue;
				}

				if (p.IsDownload && p.SubOpcode == ZoneLineSubOpcode && currentMap != 0)
				{
					if (body.Length < SpawnPacketBuilder.PosZOffset + 4)
						continue;

					var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
					var (_, name) = WorldObjectExtractor.ReadMessage(body);
					zoneLines[handle] = (name, WorldObjectExtractor.ReadLE(body, SpawnPacketBuilder.PosXOffset),
						WorldObjectExtractor.ReadLE(body, SpawnPacketBuilder.PosYOffset),
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

				for (var j = i + 1; j < packets.Count && j <= i + LookaheadPackets; j++)
				{
					var q = packets[j];
					if (q.Opcode != Opcode2000 || !q.IsDownload || q.SubOpcode != N3TeleportSubOpcode)
						continue;

					if (TryParseN3Teleport(WorldObjectExtractor.GetBody(q.Bytes), out var destMap, out var ax, out var ay, out var az))
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

		// The used object's handle rides inside the UseObject body (as the from/target handle after the protobuf
		// args). Rather than fully parse the BCC, match any 4-byte BE word in the body against the current map's
		// zone-line handles - the set is tiny (a handful per map) so a false hit is vanishingly unlikely.
		private static bool TryFindUsedZoneLine(byte[] body, Dictionary<uint, (string Name, float X, float Y, float Z)> zoneLines,
			out (string Name, float X, float Y, float Z) used)
		{
			for (var i = 0; i + 4 <= body.Length; i++)
			{
				var word = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i, 4));
				if (zoneLines.TryGetValue(word, out used))
					return true;
			}

			used = default;
			return false;
		}

		// Destination map (PlayfieldProxy u32[1] after the 0x62/0xC79C marker) + arrival Vector3 (BE floats after the
		// 0x00018704 position-blob tag), both lifted from the N3Teleport body.
		private static bool TryParseN3Teleport(byte[] body, out uint destMap, out float x, out float y, out float z)
		{
			destMap = 0;
			x = y = z = 0;

			var proxy = IndexOf(body, ProxyMarker);
			if (proxy < 0 || proxy + ProxyMarker.Length + 4 > body.Length)
				return false;

			destMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(proxy + ProxyMarker.Length, 4));

			var pos = IndexOf(body, PositionBlobTag);
			if (pos < 0 || pos + PositionBlobTag.Length + 12 > body.Length)
				return false;

			var o = pos + PositionBlobTag.Length;
			x = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(o, 4));
			y = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(o + 4, 4));
			z = BinaryPrimitives.ReadSingleBigEndian(body.AsSpan(o + 8, 4));
			return destMap != 0;
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
	}
}
