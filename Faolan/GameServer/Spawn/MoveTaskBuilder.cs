using Faolan.Core.Network;
using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Data;
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
		private const uint Ack = EntityClass.Character;

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
			var stream = new FaolanStream()
				.WriteUInt32((uint)(total - 4))
				.WriteUInt32(SubOpcode)
				.WriteUInt32(Ack)
				.WriteUInt32(handle)
				.WriteUInt16((ushort)0) // prefix 00 00
				.WriteVector3BigEndian(currentPosition)
				// segment-list + move-block header (04 01 00 02 01 01 00 00 00 00)
				.WriteArray(0x04, 0x01, 0x00, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00)
				.WriteByte((byte)waypoints.Count);
			foreach (var wp in waypoints)
				stream.WriteVector3BigEndian(wp);

			return stream
				// block trailer: flag 00, speed, 12 pad bytes
				.WriteByte(0x00)
				.WriteFloatBigEndian(speed)
				.WriteArray(new byte[12])
				.WriteVector3BigEndian(currentPosition) // anchor
				.WriteByte(0x00)
				.WriteByte(0x01) // terminator
				.ToArray();
		}

		public static byte[] BuildStationary(uint handle, Vector3 position)
		{
			return new FaolanStream()
				.WriteUInt32((uint)(BodyLength - 4))
				.WriteUInt32(SubOpcode)
				.WriteUInt32(Ack)
				.WriteUInt32(handle)
				.WriteUInt16((ushort)0) // prefix 00 00
				.WriteVector3BigEndian(position)
				.WriteByte(0x06) // stationary terminator (vs 0x04 = a movement segment follows)
				.ToArray();
		}
	}
}
