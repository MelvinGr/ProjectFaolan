using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The map-marker pair (#868), pinned against the captured bytes. `--report method-bodies --report-arg WayPoint`
	// is the oracle: six add frames over three captures and one remove.
	public class WayPointCodecTests
	{
		// A captured add, verbatim: the tombstone marker in `NecroL80_mage-necromancer_login_gameplay`.
		private static readonly byte[] CapturedAdd =
		[
			0x00, 0x00, 0xC8, 0x7B, // i_cID.class  = EntityClass.TombStone
			0x00, 0x00, 0x00, 0x58, // i_cID.instance
			0x00, 0x00, 0x04, 0x44, // i_nPlayfieldID = 1092
			0x43, 0x48, 0x3C, 0xA0, // x = 200.237
            0x42, 0x56, 0x9A, 0xDE, // y =  53.651
			0x42, 0x3D, 0x24, 0xBC, // z =  47.286
			0x00, 0x00, 0x00, 0x00, // i_vRadius = 0
			0x0F, 0x5A, 0x0B, 0x53, // i_nLabel   = LDB 'Tombstone'
			0x01, 0x3A, 0xF0, 0x90  // i_nToolTip = LDB 'Locate your Tombstone to reduce death penalty!'
		];

		// The corpus's single remove frame - and it is the add's first twelve bytes, same marker.
		private static readonly byte[] CapturedRemove =
		[
			0x00, 0x00, 0xC8, 0x7B,
			0x00, 0x00, 0x00, 0x58,
			0x00, 0x00, 0x04, 0x44
		];

		[Fact]
		public void AddWayPoint_RoundTripsTheCapturedBody()
		{
			var parsed = WayPointCodec.Parse(CapturedAdd);

			Assert.Equal(EntityClass.TombStone, parsed.Id.Class);
			Assert.Equal(0x58u, parsed.Id.Id);
			Assert.Equal(1092u, parsed.PlayfieldId);
			Assert.Equal(0f, parsed.Radius);
			Assert.Equal(WayPointCodec.TombstoneLabel, parsed.Label);
			Assert.Equal(WayPointCodec.TombstoneToolTip, parsed.ToolTip);

			Assert.Equal(CapturedAdd, WayPointCodec.Build(parsed));
		}

		// The convenience overload has to produce the captured bytes too, or it is a second encoding.
		[Fact]
		public void AddWayPoint_TombstoneOverload_IsTheCapturedShape()
		{
			var parsed = WayPointCodec.Parse(CapturedAdd);

			Assert.Equal(CapturedAdd, WayPointCodec.Build(0x58, 1092, parsed.X, parsed.Y, parsed.Z));
		}

		[Fact]
		public void RemoveWayPoint_IsTheAddsFirstTwelveBytes()
		{
			var parsed = RemoveWayPointCodec.Parse(CapturedRemove);

			Assert.Equal(EntityClass.TombStone, parsed.Id.Class);
			Assert.Equal(0x58u, parsed.Id.Id);
			Assert.Equal(1092u, parsed.PlayfieldId);
			Assert.Equal(CapturedRemove, RemoveWayPointCodec.Build(parsed));

			// The identity the corpus states: the remove names the marker the add planted.
			Assert.Equal(CapturedRemove, CapturedAdd[..12]);
		}
	}
}
