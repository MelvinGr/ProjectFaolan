using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Columnarization gate: every committed patrol body (the 136 PatrolPath.Body blobs in the PatrolBodies golden)
	// must survive MoveTaskIIR -> PatrolNode tree -> MoveTaskIIR byte-for-byte. That is what lets the runtime build
	// patrols from PatrolNode columns instead of replaying the captured Body: the mapping loses nothing.
	public class PatrolNodeRoundTripTests
	{
		[Theory, MemberData(nameof(Bodies))]
		public void EveryCommittedPatrol_ColumnarizesAndRebuildsByteExact(byte[] body, int index)
		{
			_ = index;

			// PatrolPath.Body is the record from the subop onward - the shape MoveTaskCodec.Parse accepts.
			var original = MoveTaskCodec.Parse(body);

			var rootNode = PatrolNodeMapper.ToNode(original.Task);

			var rebuilt = original with { Task = PatrolNodeMapper.ToTask(rootNode) };

			Assert.Equal(body, MoveTaskCodec.Build(rebuilt));
		}

		public static IEnumerable<object[]> Bodies()
		{
			var blob = Golden.Bytes("PatrolBodies");
			var count = blob.ReadU32Be(0);
			var offset = 4;
			for (var i = 0; i < count; i++)
			{
				var len = (int)blob.ReadU32Be(offset);
				offset += 4;
				yield return [blob[offset..(offset + len)], i];

				offset += len;
			}
		}
	}
}
