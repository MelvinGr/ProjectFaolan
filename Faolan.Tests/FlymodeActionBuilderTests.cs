using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The GM fly toggle: e_Type_GC_GameAction_StartFlymode delivered as a one-element GameActionListIIR.
	//
	// There is no capture of one (the retail corpus is ordinary play, not GM sessions), so this asserts the element
	// against the RE'd positional grammar and against the SHAPE every captured GameActionList element does share -
	// the 0x2f5 seed and the ReadSubClass trailer - rather than a golden body.
	public class FlymodeActionBuilderTests
	{
		[Fact]
		public void StartFlymodeType_IsTheDescriptorIndexDerivedId()
		{
			// FUN_008bdf40 writes the name pointer at descriptor+0; the table base is 0x025077C8 with stride 0x41 and
			// the first descriptor is type 0xCF09, so typeId = 0xCF09 + index. StartFlymode registers at 0x025102EC.
			const long tableBase = 0x025077C8, stride = 0x41, firstType = 0xCF09, startFlymodeDescriptor = 0x025102EC;

			var index = (startFlymodeDescriptor - tableBase) / stride;
			Assert.Equal(0, (startFlymodeDescriptor - tableBase) % stride); // lands exactly on a descriptor
			Assert.Equal(548, index);
			Assert.Equal(FlymodeActionBuilder.StartFlymodeType, (uint)(firstType + index));

			// ...and it sits PAST 0xD026, which is why the 68-entry docs/re/gameaction_types.json never saw it.
			Assert.True(FlymodeActionBuilder.StartFlymodeType > 0xD026);
		}

		[Fact]
		public void StartFlymodeBody_MatchesThePositionalGrammar()
		{
			const uint frame = 0x0134DEAD;

			var body = FlymodeActionBuilder.BuildStartFlymodeBody(frame);
			var parsed = GameActionListCodec.Parse(body);

			Assert.Equal(frame, parsed.FrameHandle);
			// the codec's "dynelHandle" slot is the element typeId - every captured body carries a GameAction type id
			// there (0xD014, 0xCFEC, 0xD00C, 0xCF0D), not an entity handle.
			Assert.Equal(FlymodeActionBuilder.StartFlymodeType, parsed.DynelHandle);
			Assert.Equal(0u, parsed.Field3);

			// Tail = the element from 0x2d6 on: [0x2d6=0][0x2f2=1][0x2f5=9], no keyList values, then the
			// ReadSubClass [u8 flag][u32] trailer.
			var expectedTail = Be(0, 1, GameActionElement.CapturedSeed0x2f5).Concat(GameActionElement.ReadSubClassTrailer).ToArray();
			Assert.Equal(Convert.ToHexString(expectedTail), Convert.ToHexString(parsed.ActionTail));
		}

		[Fact]
		public void StartFlymodeBody_EndsWithTheReadSubClassTrailer()
		{
			// Every captured GameActionList body ends in five zero bytes ([u8 flag] + [u32], FUN_008679f0). A
			// constructed body that omits them leaves the client reading past the record.
			var body = FlymodeActionBuilder.BuildStartFlymodeBody(0x00035021);
			Assert.Equal(GameActionElement.ReadSubClassTrailer, body[^GameActionElement.ReadSubClassTrailer.Length..]);
		}

		private static byte[] Be(params uint[] words)
		{
			var b = new byte[words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				b.WriteU32Be(i * 4, words[i]);
			return b;
		}
	}
}
