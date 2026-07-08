using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
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
		private const int BodyLength = 31;

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
				.WriteUInt32(total - 4)
				.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0) // prefix 00 00
				.WriteVector3(currentPosition)
				// segment-list + move-block header (04 01 00 02 01 01 00 00 00 00)
				.WriteArray(0x04, 0x01, 0x00, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00)
				.WriteByte(waypoints.Count);
			foreach (var wp in waypoints)
				stream.WriteVector3(wp);

			return stream
				// block trailer: flag 00, speed, 12 pad bytes
				.WriteByte(0x00)
				.WriteFloat(speed)
				.WriteArray(new byte[12])
				.WriteVector3(currentPosition) // anchor
				.WriteByte(0x00)
				.WriteByte(0x01) // terminator
				.ToArray();
		}

		// Builds the patrol MoveTask for `handle` from a DB-sourced PatrolPath row (#53): the row's Position is the
		// current/anchor point, its ordered Waypoints the path, and Speed the walk speed. Byte-identical to the
		// captured patrol the row was extracted from (PatrolPath rows only store the canonical single-segment form -
		// PatrolPathExtractor). Same output as the explicit BuildPatrol overload, sourced from data instead of args.
		public static byte[] BuildPatrol(uint handle, PatrolPath path)
		{
			ArgumentNullException.ThrowIfNull(path);
			var waypoints = path.Waypoints.OrderBy(w => w.Ordinal).Select(w => new Vector3(w.PositionX, w.PositionY, w.PositionZ)).ToList();
			return BuildPatrol(handle, path.Position, waypoints, path.Speed);
		}

		public static byte[] BuildStationary(uint handle, Vector3 position)
		{
			return new FaolanStream()
				.WriteUInt32(BodyLength - 4)
				.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0) // prefix 00 00
				.WriteVector3(position)
				.WriteByte(0x06) // stationary terminator (vs 0x04 = a movement segment follows)
				.ToArray();
		}
	}
}
