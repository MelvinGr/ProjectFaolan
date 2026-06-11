using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.GameServer.Spawn;

namespace Faolan.Importer
{
	public sealed record WorldObjectEntry(uint MapId, uint SubOpcode, uint Handle, float X, float Y, float Z, byte[] Data)
	{
		public float RotX { get; init; }
		public float RotY { get; init; }
		public float RotZ { get; init; }
		public float RotW { get; init; }
		public uint ObjectId { get; init; }
		public string Name { get; init; } = string.Empty;
	}

	// Extracts the static world objects per map from a capture: tracks the current map (MapChange 5D85BFC7), then
	// for each world-object packet reads the handle + transform (same offsets as the entity spawns) and the object
	// message's id (f1) + name (f2). The object classes are the FullUpdate families that share the object transform
	// grammar (0x2C group, no pre-IIR block): 66AEDD50 decor/props, F096251E interactive containers, 33A56FB0 zone
	// labels, 6BD56736 destructibles, 1FD6412B climbing dynels. Sibling of NpcSpawnExtractor; dedup key is (map, pos).
	public static class WorldObjectExtractor
	{
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint Opcode2000 = 0x2000;

		private static readonly HashSet<uint> ObjectSubOpcodes = [0x66AEDD50, 0xF096251E, 0x33A56FB0, 0x6BD56736, 0x1FD6412B];

		public static List<WorldObjectEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<WorldObjectEntry>();
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

				if (!ObjectSubOpcodes.Contains(subop) || currentMap == 0)
					continue;
				if (body.Length < SpawnPacketBuilder.RotWOffset + 4)
					continue; // too short to hold the transform

				var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
				var (objectId, name) = ReadMessage(body);

				entries.Add(new WorldObjectEntry(currentMap, subop, handle, ReadLE(body, SpawnPacketBuilder.PosXOffset), ReadLE(body, SpawnPacketBuilder.PosYOffset), ReadLE(body, SpawnPacketBuilder.PosZOffset), p.Bytes)
				{
					RotX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
					RotY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
					RotZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
					RotW = ReadLE(body, SpawnPacketBuilder.RotWOffset),
					ObjectId = objectId,
					Name = name
				});
			}

			return entries;
		}

		// Dedup by (map, spawn point); keep the first seen.
		public static List<WorldObjectEntry> Merge(IEnumerable<WorldObjectEntry> all)
		{
			var seen = new Dictionary<(uint, float, float, float), WorldObjectEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.X, e.Y, e.Z), e);

			return seen.Values.ToList();
		}

		// The object message is [u32 BE length][message] right after the 0x2C transform group (no pre-IIR block).
		// Reads field 1 (id, varint) and field 2 (name, string). Returns (0, "") if the layout is unexpected.
		private static (uint Id, string Name) ReadMessage(byte[] body)
		{
			var start = TransformEnd(body);
			if (start < 0 || start + 4 > body.Length)
				return (0, string.Empty);

			var len = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start, 4));
			var i = start + 4;
			var end = i + len;
			if (len <= 0 || end > body.Length)
				return (0, string.Empty);

			uint id = 0;
			var name = string.Empty;
			while (i < end)
			{
				if (!TryReadVarint(body, ref i, end, out var tag))
					break;

				var field = tag >> 3;
				var wire = tag & 7;
				switch (wire)
				{
					case 0:
						if (!TryReadVarint(body, ref i, end, out var v))
							return (id, name);
						if (field == 1)
							id = (uint)v;
						break;
					case 2:
						if (!TryReadVarint(body, ref i, end, out var sl) || i + (int)sl > end)
							return (id, name);
						if (field == 2)
							name = Encoding.ASCII.GetString(body, i, (int)sl);
						i += (int)sl;
						break;
					case 5:
						i += 4;
						break;
					case 1:
						i += 8;
						break;
					default:
						return (id, name);
				}
			}

			return (id, name);
		}

		// Offset just after the 0x2C transform group (where the object message's u32 length prefix begins):
		// 0x2C, 0x0A vec3, 0x12 quat, optional 0x25 scale, 0x18 playfield-varint. Returns -1 if malformed.
		private static int TransformEnd(byte[] b)
		{
			var i = 20;
			if (b.Length <= i || b[i] != 0x2C)
				return -1;
			i++;
			if (i + 1 >= b.Length || b[i] != 0x0A)
				return -1;
			i += 2 + b[i + 1]; // 0A vec3
			if (i + 1 >= b.Length || b[i] != 0x12)
				return -1;
			i += 2 + b[i + 1]; // 12 quat
			if (i < b.Length && b[i] == 0x25)
				i += 5; // optional 25 scale
			if (i >= b.Length || b[i] != 0x18)
				return -1;
			i++;
			return TryReadVarint(b, ref i, b.Length, out _) ? i : -1;
		}

		private static bool TryReadVarint(byte[] b, ref int i, int end, out ulong value)
		{
			value = 0;
			var shift = 0;
			while (i < end)
			{
				var x = b[i++];
				value |= (ulong)(x & 0x7F) << shift;
				if ((x & 0x80) == 0)
					return true;
				shift += 7;
			}

			return false;
		}

		private static float ReadLE(byte[] body, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(offset, 4));
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
	}
}
