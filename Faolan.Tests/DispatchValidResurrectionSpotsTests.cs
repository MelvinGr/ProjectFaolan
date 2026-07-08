using System;
using System.Numerics;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for GameCharInterface.DispatchValidResurrectionSpots (s2c 0x2024) - the death/release screen.
	// Decoded from account-faolan-...-resurect (idx616 f328; idx4050 is identical bar the death position). One
	// resurrection spot: deathPos + [spotPos][0xC7A0][mapId 2010][0][0][0][spawnPointId 0x000F5C46][0x0A] + 0x00.
	// Exact float bit patterns are reconstructed so the assertion is byte-for-byte against the capture.
	public class DispatchValidResurrectionSpotsTests
	{
		private const string CapturedBody = "446b8aa04326059444877ccd000007e244812c7b431a6ac14473f5c30000c7a0000007da000000000000000000000000000f5c460000000a00";

		[Fact]
		public void ValidResurrectionSpotsBody_MatchesCapture()
		{
			var deathPosition = new Vector3(Bits(0x446b8aa0), Bits(0x43260594), Bits(0x44877ccd));
			var spot = new RespawnPoint
			{
				Id = 0x000f5c46,
				MapId = 2010,
				PositionX = Bits(0x44812c7b),
				PositionY = Bits(0x431a6ac1),
				PositionZ = Bits(0x4473f5c3)
			};

			var s = new FaolanStream();
			ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(s, deathPosition, [spot]);

			Assert.Equal(CapturedBody, Convert.ToHexStringLower(s.ToArray()));
		}

		private static float Bits(uint u)
		{
			return BitConverter.UInt32BitsToSingle(u);
		}
	}
}
