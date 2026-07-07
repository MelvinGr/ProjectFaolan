using System;
using System.Text;
using Faolan.GameServer.Spawn;
using Xunit;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	// Byte-exact round-trip for the dynel-family full-update records (#187, docs/iir-readers-combat-world.md):
	// Door (typeWord 0xC748) and TombStone (0xC87B). Oracles are captured bodies (full record incl. length prefix).
	// Proves the codec splits the record into [header][env][length-prefixed identity blob][opaque geometry tail] and
	// rebuilds it byte-exact - the same lift SimpleDynelMessageCodec does, extended to the door/destructible/
	// climbing/tombstone family.
	public class DynelFullUpdateCodecTests
	{
		private const string DoorBody =
			"000000da33a56fb00000c74800000ab1000000002c0a0f0dd9e9624315319dc6421d4d950e4412140d00000000158735bdbe1d0000000025c6e06d3f1884ecc0020000006408b2dc45120641726d6f72791a4e3c6c6f63616c697a65642069643d2233383634303636222063617465676f72793d2235353030302220666c6167733d2222206b65793d22292c2c2b6e4f48784c695468284130643941682a22203e280138c88e03600100116e30000f696e0000000000000000bf751e973a523d4fbd69ae4f3f7544cc4069753e3cb93ab43f8e147b000000000000000000";

		private const string TombStoneBody =
			"000000fd0d80e7c90000c87b0000001a000000002c0a0f0db91a784415f62e22431dcef5224412140d000000001594b81f3f1d00000000252110483f18d2b0aa0f0000008308001209546f6d6273746f6e651a6c3c72656d6f7465666f726d61742069643d22323332313035222063617465676f72793d22353032303022206b65793d2263372c6c2b7a382b28332671756f743b527a6d665161602671756f743b4622206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e280140a9950e60010002370f000f69510000000000000000bf000000bf000000bf0000003f0000003f0000003f0000003f80000000000000000000c3500131690f";

		[Fact]
		public void Door_PreservesTemplateName()
		{
			var p = DynelFullUpdateCodec.Parse(Convert.FromHexString(DoorBody));
			// the template name ("Armory") rides in the geometry tail; the codec preserves it verbatim
			Assert.Contains("Armory", Encoding.ASCII.GetString(p.GeometryTail));
		}

		[Theory, InlineData(DoorBody, 0x33A56FB0u, 0x0000C748u), InlineData(TombStoneBody, 0x0D80E7C9u, 0x0000C87Bu)]
		public void DynelFullUpdate_RoundTripsByteExact(string hex, uint expectSub, uint expectType)
		{
			var captured = Convert.FromHexString(hex);
			var p = DynelFullUpdateCodec.Parse(captured);

			Assert.Equal(expectSub, p.SubOpcode);
			Assert.Equal(expectType, p.TypeWord);
			Assert.Equal(hex, Convert.ToHexStringLower(DynelFullUpdateCodec.Build(p)));
		}

		[Fact]
		public void TombStone_PreservesName()
		{
			var p = DynelFullUpdateCodec.Parse(Convert.FromHexString(TombStoneBody));
			Assert.Contains("Tombstone", Encoding.ASCII.GetString(p.GeometryTail));
		}
	}
}
