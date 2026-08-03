using System.Linq;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// The area-banner harvest (#348/#377). The banner is s2c; the position it fired at comes from the client's own c2s
	// CharDCMove, so a sighting is only placeable if a move brackets it.
	//
	// The regression guarded here: the pairing used to take the last move BEFORE the banner and drop the sighting when
	// there wasn't one - which is exactly the ENTRY banner, since it fires right after the MapChange, before the client
	// has moved. That silently discarded 17 of the corpus's 48 sightings and one whole area (1010/3709829, which had
	// nothing but entry banners). The next move on the same playfield places it just as well: the player is standing in
	// the area while the banner is on screen.
	public class AreaNameExtractorTests
	{
		[Fact]
		public void BannerBeforeAnyMove_IsPlacedByTheNextMove()
		{
			var sightings = AreaNameExtractor.Extract([MapChange(1010), Banner(3709829), Move(10, 20, 30)]);

			var s = Assert.Single(sightings);
			Assert.Equal(1010u, s.MapId);
			Assert.Equal(3709829u, s.AreaNameId);
			Assert.Equal((10f, 20f, 30f), (s.X, s.Y, s.Z));
		}

		[Fact]
		public void BannerWithAPrecedingMove_StillUsesThatMove()
		{
			// The crossing case: the move that crossed the boundary is the first position inside the new area, so a
			// LATER move must not override it.
			var sightings = AreaNameExtractor.Extract([MapChange(2000), Move(1, 2, 3), Banner(3373519), Move(9, 9, 9)]);

			var s = Assert.Single(sightings);
			Assert.Equal((1f, 2f, 3f), (s.X, s.Y, s.Z));
		}

		[Fact]
		public void PendingBanner_DoesNotLeakAcrossAPlayfieldChange()
		{
			// An unplaced banner belongs to the playfield it fired on; a move after the next MapChange is somewhere
			// else entirely and would place it nowhere.
			var sightings = AreaNameExtractor.Extract([MapChange(2000), Banner(3373519), MapChange(2010), Move(5, 5, 5)]);

			Assert.Empty(sightings);
		}

		[Fact]
		public void EveryPendingBanner_IsPlaced_NotJustTheLast()
		{
			var sightings = AreaNameExtractor.Extract([MapChange(2000), Banner(3373521), Banner(3373522), Move(7, 8, 9)]);

			Assert.Equal([3373521u, 3373522u], sightings.Select(s => s.AreaNameId));
			Assert.All(sightings, s => Assert.Equal((7f, 8f, 9f), (s.X, s.Y, s.Z)));
		}

		private static CapturedPacket Banner(uint areaNameId)
		{
			// A REAL FormatFeedbackIIR, built by the codec - same reasoning as MapChange below. Only the
			// GenericDisplayAreaName|<id> token is read out of the XML; the real frames wrap it in a
			// <remoteformat>/<parameter> pair whose surroundings this harvest ignores.
			var xml = $"<localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|{areaNameId}'>";
			return Carrier(true, FormatFeedbackCodec.Opcode, FormatFeedbackCodec.Build(0x0012D687, FormatFeedbackCodec.FifoBannerTypeFlags, 1, xml));
		}

		private static byte[] Frame(byte[] body)
		{
			var full = new byte[12 + body.Length]; // [len4][crc4][headerLen4=0][body]
			body.CopyTo(full, 12);
			return full;
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			// A REAL PlayfieldFIIR, built by the codec: PlayfieldFCodec.Parse reads the record to its last byte
			// (#530 rule 3), so a synthetic body with the map id at the right offset and zeros everywhere else is
			// not a MapChange any more - and a fixture shaped like the parser proves only that the file agrees
			// with itself.
			return Carrier(true, PlayfieldFCodec.Opcode, PlayfieldFCodec.Build(Vector3.Zero, mapId, 0, 1, 0x01EA7B9C, 15f, 13.86f, 0));
		}

		private static CapturedPacket Move(float x, float y, float z)
		{
			return Carrier(false, CharDCMoveCodec.Opcode, CharDCMoveCodec.Build(0x0012D687, new Vector3(x, y, z), Quaternion.Identity));
		}

		// A 0x2000 carrier around one IIR record: the body is [u32 recordLen][record].
		private static CapturedPacket Carrier(bool isDownload, uint subOpcode, byte[] record)
		{
			byte[] body = [0, 0, 0, 0, .. record];
			body.WriteU32Be(0, (uint)record.Length);
			return new CapturedPacket(0, isDownload, 0x2000, subOpcode, Frame(body));
		}
	}
}
