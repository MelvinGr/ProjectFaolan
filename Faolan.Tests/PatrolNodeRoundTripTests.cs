using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #316 columnarization gate: every committed patrol body (Golden/PatrolBodies.bin - the 136 PatrolPath.Body blobs)
	// must survive MoveTaskComposite -> PatrolNode tree -> MoveTaskComposite byte-for-byte. This proves the DB
	// columnarization (PatrolNode/PatrolNodeMapper) loses nothing, so the runtime can build patrols from columns
	// instead of replaying the captured Body.
	public class PatrolNodeRoundTripTests
	{
		public static IEnumerable<object[]> Bodies()
		{
			var blob = Golden.Bytes("PatrolBodies");
			var count = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4));
			var offset = 4;
			for (var i = 0; i < count; i++)
			{
				var len = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset, 4));
				offset += 4;
				yield return [blob[offset..(offset + len)], i];
				offset += len;
			}
		}

		[Theory]
		[MemberData(nameof(Bodies))]
		public void EveryCommittedPatrol_ColumnarizesAndRebuildsByteExact(byte[] body, int index)
		{
			_ = index;

			// PatrolPath.Body is the record from the subop onward - the shape MoveTaskComposite.Parse accepts.
			var original = MoveTaskComposite.Parse(body);

			var rootNode = PatrolNodeMapper.ToNode(original.Task);

			var rebuilt = new MoveTaskComposite
			{
				Handle = original.Handle,
				FromX = original.FromX,
				FromY = original.FromY,
				FromZ = original.FromZ,
				Task = PatrolNodeMapper.ToTask(rootNode)
			};

			Assert.Equal(body, rebuilt.Encode());
		}
	}
}
