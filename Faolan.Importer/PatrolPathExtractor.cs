using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Faolan.Importer
{
	public sealed record PatrolPathEntry(uint MapId, Vector3 Position, float Speed, IReadOnlyList<Vector3> Waypoints);

	// Extracts NPC patrol routes from a capture's MoveTask packets (MoveTaskIIR 0x96C46740): tracks the current
	// map (MapChange 5D85BFC7), then for each MoveTask whose body is the CANONICAL single-segment waypoint patrol
	// (task type 0x04 - the form MoveTaskBuilder.BuildPatrol reproduces) reads the current/anchor position, the
	// walk speed and the ordered waypoints. Only the canonical form is taken; multi-segment / parametric patrols
	// are skipped (the documented MoveTask remainder, dissect_movetask.py). Sibling of NpcSpawnExtractor; dedup
	// key is (map, position) - the same stable spawn key NpcSpawn uses, so a patrol joins its NPC by position.
	//
	// Carrier-body layout (the bytes after the 0x2000 packet framing), all floats BIG-ENDIAN:
	//   [u32 len][96C46740][u32 ack=0xC350][u32 handle][u16 0][curPos vec3]
	//   04 01 00 02 01 01 00 00 00 00          segment-list + move-block header (constant for this form)
	//   [u8 count][count x waypoint vec3]       the patrol path
	//   00 [speed f32][12x 00]                  block trailer (flag 00 + speed + pad)
	//   [anchor vec3 = curPos][00 01]           anchor + terminator
	public static class PatrolPathExtractor
	{
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint MoveTaskSubOpcode = 0x96C46740;
		private const uint Opcode2000 = 0x2000;
		private const uint InstanceRangeStart = 0x02000000; // player/own-entity handles (a player isn't a patrol)

		// The constant 10-byte segment-list + move-block header of the canonical single-segment patrol.
		private static readonly byte[] CanonicalHeader = [0x04, 0x01, 0x00, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00];

		public static List<PatrolPathEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<PatrolPathEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (p.Opcode != Opcode2000)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < 8)
					continue;

				var subop = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4));
				if (subop == MapChangeSubOpcode)
				{
					if (body.Length >= 38)
						currentMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
					continue;
				}

				if (subop != MoveTaskSubOpcode || currentMap == 0)
					continue;

				if (TryParseCanonicalPatrol(body, out var position, out var speed, out var waypoints))
					entries.Add(new PatrolPathEntry(currentMap, position, speed, waypoints));
			}

			return entries;
		}

		// Dedup by (map, spawn point); keep the first seen - like NpcSpawnExtractor.Merge.
		public static List<PatrolPathEntry> Merge(IEnumerable<PatrolPathEntry> all)
		{
			var seen = new Dictionary<(uint, float, float, float), PatrolPathEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.Position.X, e.Position.Y, e.Position.Z), e);

			return seen.Values.ToList();
		}

		// True (with the parsed fields) iff `body` is EXACTLY the canonical single-segment patrol - the form
		// BuildPatrol reproduces byte-for-byte. Every check is exact, so a row built from these fields round-trips
		// to the captured bytes; any deviation (multi-segment, parametric, trailing data) returns false and is left
		// as the verbatim MoveTask remainder.
		private static bool TryParseCanonicalPatrol(byte[] b, out Vector3 position, out float speed, out IReadOnlyList<Vector3> waypoints)
		{
			position = default;
			speed = 0;
			waypoints = [];

			// header(16) + prefix(2) + curPos(12) + canonicalHeader(10) + count(1)
			if (b.Length < 41)
				return false;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12, 4));
			if (handle >= InstanceRangeStart)
				return false; // a player MoveTask, not an NPC patrol

			if (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(16, 2)) != 0)
				return false;

			var curPos = ReadVec3(b, 18);
			if (!b.AsSpan(30, CanonicalHeader.Length).SequenceEqual(CanonicalHeader))
				return false;

			int count = b[40];
			if (count == 0)
				return false;

			var i = 41;
			// count waypoints + trailer(17) + anchor(12) + terminator(2)
			if (i + 12 * count + 17 + 12 + 2 != b.Length)
				return false;

			var wps = new List<Vector3>(count);
			for (var k = 0; k < count; k++, i += 12)
				wps.Add(ReadVec3(b, i));

			if (b[i] != 0x00) // trailer flag
				return false;
			var spd = BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(i + 1, 4));
			i += 5;
			if (b.AsSpan(i, 12).ContainsAnyExcept((byte)0)) // 12-byte pad must be zero
				return false;
			i += 12;

			if (ReadVec3(b, i) != curPos) // anchor must equal the current position
				return false;
			i += 12;

			if (b[i] != 0x00 || b[i + 1] != 0x01) // terminator
				return false;

			position = curPos;
			speed = spd;
			waypoints = wps;
			return true;
		}

		private static Vector3 ReadVec3(byte[] b, int offset)
		{
			return new Vector3(
				BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(offset, 4)),
				BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(offset + 4, 4)),
				BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(offset + 8, 4)));
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
