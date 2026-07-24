using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Faolan.Core.Enums;
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
			// Only the GenericDisplayAreaName|<id> token is read out of the FormatFeedback XML; the real frames wrap it
			// in a <remoteformat>/<parameter> pair whose surroundings this harvest ignores.
			var xml = Encoding.Latin1.GetBytes($"<localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|{areaNameId}'>");
			var body = new byte[8 + xml.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)body.Length);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), (uint)GameServer0X2000Opcodes.FormatFeedbackIIR);
			xml.CopyTo(body, 8);
			return Packet(true, (uint)GameServer0X2000Opcodes.FormatFeedbackIIR, body);
		}

		private static byte[] Frame(byte[] body)
		{
			var full = new byte[12 + body.Length]; // [len4][crc4][headerLen4=0][body]
			body.CopyTo(full, 12);
			return full;
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			// MapChangeCodec reads the map id at body offset 26 after the sub-opcode word.
			var body = new byte[4 + 4 + 99 + 4];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)body.Length);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), (uint)GameServer0X2000Opcodes.PlayfieldFIIR);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8 + 26, 4), mapId);
			return Packet(true, (uint)GameServer0X2000Opcodes.PlayfieldFIIR, body);
		}

		private static CapturedPacket Move(float x, float y, float z)
		{
			// The c2s CharDCMove carries the position as three big-endian floats at body offset 18.
			var body = new byte[18 + 12];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)body.Length);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), (uint)GameServer0X2000Opcodes.CharDCMoveIIR);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(18, 4), x);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(22, 4), y);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(26, 4), z);
			return Packet(false, (uint)GameServer0X2000Opcodes.CharDCMoveIIR, body);
		}

		private static CapturedPacket Packet(bool isDownload, uint subOpcode, byte[] body)
		{
			return new CapturedPacket(0, isDownload, 0x2000, subOpcode, Frame(body));
		}
	}
}
