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
			// The client indexes the descriptor table as 0x025077C8 + (id - 0xCF09) * 0x104 (FUN_008c3910:
			// `LEA EAX,[ESI + 0xffff30f7]; IMUL EAX,EAX,0x104; ADD EAX,0x25077c8`). StartFlymode registers at
			// 0x025102EC => index 137 => 0xCF92, which is also the id the registration writes at descriptor+8.
			const long tableBase = 0x025077C8, stride = 0x104, firstType = 0xCF09, startFlymodeDescriptor = 0x025102EC;

			Assert.Equal(0, (startFlymodeDescriptor - tableBase) % stride);
			var index = (startFlymodeDescriptor - tableBase) / stride;
			Assert.Equal(137, index);
			Assert.Equal(FlymodeActionBuilder.StartFlymodeType, (uint)(firstType + index));
		}

		// ⚠️ The regression that crashed the client (2026-07-29). Reading the same table with stride 0x41 - what
		// tools/ghidra/DumpGameActionTypes.java used - gives index 548 and id 0xD12D. Because 0x104 == 4 * 0x41 the
		// wrong index still divides evenly, so "it lands on a descriptor" proves nothing; only the RANGE does.
		[Fact]
		public void StartFlymodeType_IsInTheRangeTheClientAsserts()
		{
			Assert.True(GameActionTypeId.IsValid(FlymodeActionBuilder.StartFlymodeType));
			Assert.Equal(0xCF92u, FlymodeActionBuilder.StartFlymodeType);

			const uint fourTimesInflated = 0xD12D; // what the 0x41 stride produced
			Assert.False(GameActionTypeId.IsValid(fourTimesInflated));
			Assert.Equal(0x11Du, GameActionTypeId.End - 1 - GameActionTypeId.First); // (id - 0xCF09) <= 0x11D
		}

		// The builder must refuse an out-of-range type rather than hand it to the client, where it is an assert
		// dialog and a disconnect rather than a dropped packet.
		[Fact]
		public void BuildingAnOutOfRangeTypeThrows()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => GameActionElement.Build(0xD12D, 0, 0, []));
			Assert.Throws<ArgumentOutOfRangeException>(() => GameActionElement.Build(0xCF08, 0, 0, []));
			Assert.Throws<ArgumentOutOfRangeException>(() => GameActionElement.Build(GameActionTypeId.End, 0, 0, []));
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
