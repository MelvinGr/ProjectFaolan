using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.Spawn
{
	// Builds a 96C46740 MoveTask body - the per-entity movement registration paired with each FA015807 spawn
	// (without it the client never polls 0x2020 for the entity). The full form carries a multi-segment patrol
	// path; the MINIMAL stationary form - confirmed constant across 58 captured instances - is just the entity's
	// position, which is all a non-patrolling NPC needs and is constructible from data we already have. NOTE: the
	// MoveTask floats are BIG-ENDIAN, unlike the spawn transform's little-endian (docs/archive/spawn-dissection-plan.md).
	//
	//   [u32 bodyLen=27][u32 0x96C46740][u32 ack=0xC350][u32 handle][00 00][vec3 pos (3x BE f32)][06]
	public static class MoveTaskBuilder
	{
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.MoveTaskIIR;
		private const int BodyLength = 31;
		private const uint Ack = 0x0000C350;

		// Builds the canonical single-segment PATROL MoveTask (task type 0x04, the waypoint-list form). This is
		// what a moving NPC needs: a current position plus a list of waypoints it walks between at `speed`. The
		// grammar (docs/archive/spawn-dissection-plan.md, dissect_movetask.py) for this dominant form is:
		//   [00 00][curPos vec3 BE]
		//   04 01 00 02 01 01 00 00 00 00          segment-list + move-block header (flag bytes constant here)
		//   [count u8][count x waypoint vec3 BE]    the patrol path
		//   00 [speed f32 BE][12x 00]              block trailer (flag 00 + speed + pad)
		//   [anchor vec3 BE = curPos]              the return/anchor point
		//   00 01                                  terminator
		// All floats are BIG-ENDIAN (unlike the spawn transform's little-endian). Reproduces the captured patrol
		// body byte-for-byte (MoveTaskBuilderTests). The multi-segment and parametric ("approach/face") forms are
		// the documented remainder - see the workbench coverage report.
		public static byte[] BuildPatrol(uint handle, Vector3 currentPosition, IReadOnlyList<Vector3> waypoints, float speed)
		{
			if (waypoints == null || waypoints.Count == 0)
				throw new ArgumentException("a patrol needs at least one waypoint", nameof(waypoints));
			if (waypoints.Count > byte.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(waypoints), "waypoint count exceeds one byte");

			var total = 16 + 2 + 12 + 10 + 1 + 12 * waypoints.Count + 17 + 12 + 2;
			var body = new byte[total];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)(total - 4));
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), SubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), Ack);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), handle);
			// body[16..18] stay 0x00 0x00 (the prefix)
			WriteVec3(body, 18, currentPosition);

			var p = 30;
			// segment-list + move-block header (04 01 00 02 01 01 00 00 00 00)
			body[p++] = 0x04;
			body[p++] = 0x01;
			body[p++] = 0x00;
			body[p++] = 0x02;
			body[p++] = 0x01;
			body[p++] = 0x01;
			body[p++] = 0x00;
			body[p++] = 0x00;
			body[p++] = 0x00;
			body[p++] = 0x00;
			body[p++] = (byte)waypoints.Count;
			foreach (var wp in waypoints)
			{
				WriteVec3(body, p, wp);
				p += 12;
			}

			// block trailer: flag 00, speed, 12 pad bytes
			body[p++] = 0x00;
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(p, 4), speed);
			p += 4 + 12;
			WriteVec3(body, p, currentPosition); // anchor
			p += 12;
			body[p++] = 0x00;
			body[p] = 0x01; // terminator
			return body;
		}

		public static byte[] BuildStationary(uint handle, Vector3 position)
		{
			var body = new byte[BodyLength];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), BodyLength - 4);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), SubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), Ack);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), handle);
			// body[16..18] stay 0x00 0x00 (the prefix)
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(18, 4), position.X);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(22, 4), position.Y);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(26, 4), position.Z);
			body[30] = 0x06; // stationary terminator (vs 0x04 = a movement segment follows)
			return body;
		}

		private static void WriteVec3(byte[] body, int offset, Vector3 v)
		{
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(offset, 4), v.X);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(offset + 4, 4), v.Y);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(offset + 8, 4), v.Z);
		}
	}
}
