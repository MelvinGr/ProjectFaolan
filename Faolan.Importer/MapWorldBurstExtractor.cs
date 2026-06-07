using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer
{
	// One map's login world build: the captured player instance + the ordered server download packets from a
	// MapChange up to the client's first spawn poll (0x2020). Replaying these (re-addressed to the live character)
	// is how a map renders - exactly what the embedded beach blob does today, generalized per map from captures.
	public sealed record MapWorldBurstEntry(uint MapId, uint CapturedInstance, List<byte[]> Packets);

	public static class MapWorldBurstExtractor
	{
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2020 = 0x2020; // client spawn poll - ends the world-build window
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint SpawnSubOpcode = 0xFA015807;
		private const uint InstanceRangeStart = 0x02000000;

		// Per capture: each MapChange begins a world-build window of the following DOWNLOAD packets, until the first
		// 0x2020 poll or the next MapChange. Returns the largest window per map (most complete).
		public static List<MapWorldBurstEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var bursts = new List<MapWorldBurstEntry>();
			List<byte[]> current = null;
			uint currentMap = 0;
			uint capturedInstance = 0;

			void Flush()
			{
				if (current is { Count: > 0 } && currentMap != 0)
					bursts.Add(new MapWorldBurstEntry(currentMap, capturedInstance, current));
				current = null;
				currentMap = 0;
				capturedInstance = 0;
			}

			foreach (var p in packets)
			{
				var body = GetBody(p.Bytes);
				var subop = body.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) : 0;

				if (p.Opcode == Opcode2000 && subop == MapChangeSubOpcode && p.IsDownload)
				{
					Flush();
					currentMap = body.Length >= 38 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4)) : 0;
					current = new List<byte[]> { p.Bytes };
					continue;
				}

				if (current == null)
					continue;

				if (p.Opcode == Opcode2020)
				{
					Flush(); // first spawn poll ends the world build
					continue;
				}

				if (p.Opcode == Opcode2000 && p.IsDownload)
				{
					current.Add(p.Bytes);

					// The player's own spawn handle is the session instance (used to re-address the burst).
					if (subop == SpawnSubOpcode && body.Length >= 16)
					{
						var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
						if (handle >= InstanceRangeStart)
							capturedInstance = handle;
					}
				}
			}

			Flush();
			return bursts;
		}

		// One burst per map: the longest (most complete) that also has a known captured instance.
		public static List<MapWorldBurstEntry> Merge(IEnumerable<MapWorldBurstEntry> all)
		{
			var best = new Dictionary<uint, MapWorldBurstEntry>();
			foreach (var e in all)
			{
				if (e.CapturedInstance == 0)
					continue;

				if (!best.TryGetValue(e.MapId, out var cur) || e.Packets.Count > cur.Packets.Count)
					best[e.MapId] = e;
			}

			return best.Values.ToList();
		}

		private static byte[] GetBody(byte[] full)
		{
			if (full.Length < 12)
				return [];

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= full.Length ? full[bodyStart..] : [];
		}
	}
}
