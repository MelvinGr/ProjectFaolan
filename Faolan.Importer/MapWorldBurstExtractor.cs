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
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2020 = 0x2020; // client spawn poll - ends the world-build window

		// The LoginWorld receiver conversation id "0D 47 C1 67 6C 10 <varint>" - its trailing varint is the client
		// instance the packet was sent to (the local player), unambiguous even when other players are spawned.
		private static readonly byte[] ReceiverMarker = [0x47, 0xC1, 0x67, 0x6C, 0x10];

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
				var subop = body.Length >= 8
					? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4))
					: 0;

				if (p.Opcode == Opcode2000 && subop == MapChangeSubOpcode && p.IsDownload)
				{
					Flush();
					currentMap = body.Length >= 38
						? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4))
						: 0;
					current = [p.Bytes];

					// The receiver conversation id of the world-build downloads is the local player's instance -
					// read it from the first packet's header (unambiguous even when other players are spawned).
					var inst = ExtractReceiverInstance(p.Bytes);
					if (inst != 0)
						capturedInstance = inst;
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

					// Backfill the instance from any download in the window if the MapChange header lacked it.
					if (capturedInstance == 0)
					{
						var inst = ExtractReceiverInstance(p.Bytes);
						if (inst != 0)
							capturedInstance = inst;
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
				// Drop only bursts with an unknown captured instance - those can't be re-addressed. Out-of-range
				// instances (maps 1000/11040 captured e.g. 0x110099BD, a 5-byte varint) ARE kept now: the runtime
				// RewriteBurstInstance pads the live id's varint to the captured length, so the substitution stays
				// length-preserving.
				if (e.CapturedInstance == 0)
					continue;

				if (!best.TryGetValue(e.MapId, out var cur) || e.Packets.Count > cur.Packets.Count)
					best[e.MapId] = e;
			}

			return best.Values.ToList();
		}

		// Reads the local player's client instance from the receiver conversation id "...47 C1 67 6C 10 <varint>"
		// embedded in the packet header. Returns 0 if the marker isn't present.
		private static uint ExtractReceiverInstance(byte[] full)
		{
			var idx = IndexOf(full, ReceiverMarker);
			if (idx < 0)
				return 0;

			var pos = idx + ReceiverMarker.Length;
			return ReadVarint(full, pos);
		}

		private static byte[] GetBody(byte[] full)
		{
			if (full.Length < 12)
				return [];

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= full.Length
				? full[bodyStart..]
				: [];
		}

		private static int IndexOf(byte[] haystack, byte[] needle)
		{
			if (needle.Length == 0 || haystack.Length < needle.Length)
				return -1;

			for (var i = 0; i <= haystack.Length - needle.Length; i++)
			{
				var match = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
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

		// Little-endian base-128 varint (the receiver-tail encoding). Stops at the buffer end / a non-continuation
		// byte; the instance ids of interest fit in 32 bits.
		private static uint ReadVarint(byte[] buf, int pos)
		{
			uint value = 0;
			var shift = 0;
			while (pos < buf.Length && shift < 32)
			{
				var b = buf[pos++];
				value |= (uint)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					break;

				shift += 7;
			}

			return value;
		}
	}
}
