using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Importer
{
	public sealed record NpcSpawnEntry(uint MapId, string Name, uint Handle, float X, float Y, float Z, byte[] Data);

	// Extracts NPC entity spawns (FA015807) per map from a capture: tracks the current map (MapChange 5D85BFC7),
	// then for each spawn reads the handle (body@12), position (body@24/29/34, LE) and a best-effort name (the
	// `0A <len> <ascii>` marker). The player's own spawn is skipped - its handle is the session client instance
	// (>= 0x02000000), whereas NPC handles are small. Dedup key is (map, position); the handle/name vary.
	public static class NpcSpawnExtractor
	{
		private const uint InstanceRangeStart = 0x02000000; // player/own-entity handles live in the instance range
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint Opcode2000 = 0x2000;
		private const uint SpawnSubOpcode = 0xFA015807;

		public static List<NpcSpawnEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<NpcSpawnEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (p.Opcode != Opcode2000)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < 38)
					continue;

				var subop = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4));

				if (subop == MapChangeSubOpcode)
				{
					currentMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					continue;
				}

				if (subop != SpawnSubOpcode || currentMap == 0)
					continue;

				var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
				if (handle >= InstanceRangeStart)
					continue; // the player's own spawn

				var x = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(24, 4));
				var y = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(29, 4));
				var z = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(34, 4));

				entries.Add(new NpcSpawnEntry(currentMap, ExtractName(body), handle, x, y, z, p.Bytes));
			}

			return entries;
		}

		// Dedup by (map, spawn point); keep the first seen.
		public static List<NpcSpawnEntry> Merge(IEnumerable<NpcSpawnEntry> all)
		{
			var seen = new Dictionary<(uint, float, float, float), NpcSpawnEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.X, e.Y, e.Z), e);

			return seen.Values.ToList();
		}

		// The entity name is a `0A <len> <utf8>` field whose offset varies; scan past the position block for the
		// first plausible printable-ASCII run. Best-effort (display only - not the dedup key).
		private static string ExtractName(byte[] body)
		{
			for (var i = 40; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A)
					continue;

				int len = body[i + 1];
				if (len < 2 || len > 40 || i + 2 + len > body.Length)
					continue;

				var slice = body.AsSpan(i + 2, len);
				if (IsPrintableName(slice))
					return Encoding.ASCII.GetString(slice);
			}

			return string.Empty;
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

		private static bool IsPrintableName(ReadOnlySpan<byte> s)
		{
			var hasLetter = false;
			foreach (var c in s)
			{
				if (c < 32 || c > 126)
					return false;

				if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
					hasLetter = true;
			}

			return hasLetter;
		}
	}
}
