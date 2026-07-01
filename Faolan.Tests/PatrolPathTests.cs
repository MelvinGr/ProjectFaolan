using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Proves the PatrolPath round-trip (#53, construct-don't-replay): the importer's PatrolPathExtractor parses the
	// canonical waypoint patrol out of a captured MoveTask, and MoveTaskBuilder.BuildPatrol then rebuilds that body
	// BYTE-FOR-BYTE from the extracted PatrolPath/PatrolWaypoint rows. Same golden patrol body MoveTaskBuilderTests
	// locks; here it travels the full data path (capture -> rows -> wire) the live MoveTask build will use.
	public class PatrolPathTests
	{
		private const uint MapChangeSubOpcode = 0x5D85BFC7;
		private const uint MoveTaskSubOpcode = 0x96C46740;

		[Fact]
		public void Extractor_ParsesCapturedPatrol_AndBuilderRebuildsItByteExact()
		{
			var captured = Golden.Bytes("MoveTaskPatrol");
			var handle = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(12, 4));

			// Run the real importer extractor over the captured MoveTask (a MapChange first so it has a current map).
			var entries = PatrolPathExtractor.Merge(PatrolPathExtractor.Extract([MapChange(4010), MoveTask(captured)]));
			var entry = Assert.Single(entries);

			Assert.Equal(4010u, entry.MapId);
			Assert.Equal(captured[40], (byte)entry.Waypoints.Count); // count byte == extracted waypoints

			// Materialize the DB rows the importer would persist, then rebuild from them via the DB-sourced overload.
			var path = new PatrolPath
			{
				MapId = entry.MapId,
				Position = entry.Position,
				Speed = entry.Speed
			};
			for (var i = 0; i < entry.Waypoints.Count; i++)
				path.Waypoints.Add(new PatrolWaypoint
				{
					Ordinal = i,
					X = entry.Waypoints[i].X,
					Y = entry.Waypoints[i].Y,
					Z = entry.Waypoints[i].Z
				});

			var built = MoveTaskBuilder.BuildPatrol(handle, path);

			Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(built));
		}

		[Fact]
		public void Extractor_SkipsNonCanonicalMoveTask()
		{
			// A stationary MoveTask (task type 0x06) is not a patrol; the extractor must ignore it.
			var stationary = MoveTaskBuilder.BuildStationary(0x00017E03u, new Vector3(333f, 7f, 706f));
			Assert.Empty(PatrolPathExtractor.Extract([MapChange(4010), MoveTask(stationary)]));
		}

		// Wraps a carrier body ([u32 len][subop]...) into a full captured packet ([u32 len][u32 crc][u32 headerLen=0]
		// [body]) - the shape the extractor's GetBody unwraps (headerLen at [8..12], here 0 so the body follows).
		private static CapturedPacket Frame(byte[] carrierBody)
		{
			var full = new byte[12 + carrierBody.Length];
			carrierBody.CopyTo(full, 12);
			return new CapturedPacket(0, false, 0x2000, BinaryPrimitives.ReadUInt32BigEndian(carrierBody.AsSpan(4, 4)), full);
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			var body = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), MapChangeSubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(34, 4), mapId);
			return Frame(body);
		}

		private static CapturedPacket MoveTask(byte[] carrierBody)
		{
			return Frame(carrierBody);
		}
	}
}
