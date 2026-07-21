using System;
using System.Numerics;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte tests for GameCharInterface.DispatchValidResurrectionSpots (s2c 0x2024).
	//
	// The one-spot case (the death/release screen) was the ONLY case covered here, and it is exactly the case in which
	// the body's three decoded fields are indistinguishable from constants: at count 1 the count prefix IS 0x7E2, and
	// the per-record terminator is the body's last byte either way. The multi-spot goldens below are what pin the real
	// layout - [u32 (count+1) * 0x3F1], a 41-byte record, RestorationLevel in the tail - and what the old builder
	// would have failed. Exact float bit patterns are reconstructed so each assertion is byte-for-byte.
	public class DispatchValidResurrectionSpotsTests
	{
		// Barbarian-create f=104 - the create-flow variant: spawnPointId 0, restoration 0x64 (= 100, a fresh character
		// revives at full), type word 0xC79E. 57 bytes; the old builder emitted 55 (it wrote restoration as a u16).
		private const string CapturedCreateFlow = "43a740004314666644254ccd000007e243a740004314666644254ccd0000c79e00000faa000000000000000000000000000000000000006400";

		// account-faolan-...-resurect (idx616 f328; idx4050 is identical bar the death position). One spot, on death.
		private const string CapturedOneSpot = "446b8aa04326059444877ccd000007e244812c7b431a6ac14473f5c30000c7a0000007da000000000000000000000000000f5c460000000a00";

		// Hoxxling_mage-heraldofxotli_login_gameplay f=7862 - the DISCOVERY re-send (#372): the player is ALIVE and
		// mid-walk, and the list now holds two spots, the newly-found 784237 ahead of the always-known 784236. Count
		// prefix 0xBD3 = 3 * 0x3F1, and 784236's record here is byte-identical to its record in f=138's one-spot frame
		// - which is what proves the 0x00 is per-record rather than end-of-body.
		private const string CapturedTwoSpots =
			"43e0ac82431dd43b443aad9900000bd343d9f979431feccd443b38f60000c7a000000faa000000000000000000000000000bf76d0000000a0043a8278d4314f78d44253df40000c7a000000faa000000000000000000000000000bf76c0000000a00";

		[Fact]
		public void CreateFlowBody_MatchesCapture()
		{
			var spawn = new Vector3(Bits(0x43a74000), Bits(0x43146666), Bits(0x44254ccd));

			var s = new FaolanStream();
			ResurrectionSpotsBody.WriteCreateFlowValidResurrectionSpots(s, spawn);

			Assert.Equal(CapturedCreateFlow, Convert.ToHexStringLower(s.ToArray()));
		}

		// The count prefix is (count+1) * 0x3F1 - the same list framing the guild lists use. 0x7E2 was only ever
		// "right" because we only ever sent one spot.
		[Theory, InlineData(1, "000007e2"), InlineData(2, "00000bd3"), InlineData(3, "00000fc4"), InlineData(7, "00001f88")]
		public void ValidResurrectionSpotsBody_CountPrefixIsListFramed(int spotCount, string expectedPrefix)
		{
			var spots = new RespawnPoint[spotCount];
			for (var i = 0; i < spotCount; i++)
				spots[i] = Spot((uint)(784236 + i), 4010, 0x43a8278d, 0x4314f78d, 0x44253df4, 10);

			var s = new FaolanStream();
			ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(s, Vector3.Zero, spots);

			var bytes = s.ToArray();
			Assert.Equal(expectedPrefix, Convert.ToHexStringLower(bytes)[24..32]); // after the 12-byte reference position
			Assert.Equal(16 + 41 * spotCount, bytes.Length); // 41-byte records, no separate end-of-body byte
		}

		// The tail is the point's RestorationLevel, not a constant: the captures carry 10/50/100, and for every one of
		// the 21 spot ids in them the wire value equals the RDB value we import. Khemi's points are the 0x32 witness.
		[Theory, InlineData(10f, "0000000a"), InlineData(50f, "00000032"), InlineData(100f, "00000064")]
		public void ValidResurrectionSpotsBody_EmitsThePointsRestorationLevel(float restorationLevel, string expectedTail)
		{
			var spot = Spot(0x000f5c46, 2010, 0x44812c7b, 0x431a6ac1, 0x4473f5c3, restorationLevel);

			var s = new FaolanStream();
			ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(s, Vector3.Zero, [spot]);

			var hex = Convert.ToHexStringLower(s.ToArray());
			Assert.Equal(expectedTail, hex[^10..^2]); // the u32 ahead of the record's 0x00 terminator
		}

		[Fact]
		public void ValidResurrectionSpotsBody_MatchesCapture()
		{
			var deathPosition = new Vector3(Bits(0x446b8aa0), Bits(0x43260594), Bits(0x44877ccd));
			var spot = Spot(0x000f5c46, 2010, 0x44812c7b, 0x431a6ac1, 0x4473f5c3, 10);

			var s = new FaolanStream();
			ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(s, deathPosition, [spot]);

			Assert.Equal(CapturedOneSpot, Convert.ToHexStringLower(s.ToArray()));
		}

		// The captured order is 784237 then 784236, and 784237 is the nearer of the two (13.7 vs 141.9 units from the
		// player). Feeding them in the OPPOSITE order must still produce the captured bytes: the builder sorts
		// nearest-first, so no caller has to know the wire's ordering rule.
		[Fact]
		public void ValidResurrectionSpotsBody_TwoSpots_MatchCapture_OrderedNearestFirstWhateverTheInputOrder()
		{
			var playerPosition = new Vector3(Bits(0x43e0ac82), Bits(0x431dd43b), Bits(0x443aad99));
			var far = Spot(784236, 4010, 0x43a8278d, 0x4314f78d, 0x44253df4, 10);
			var near = Spot(784237, 4010, 0x43d9f979, 0x431feccd, 0x443b38f6, 10);

			var s = new FaolanStream();
			ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(s, playerPosition, [far, near]);

			Assert.Equal(CapturedTwoSpots, Convert.ToHexStringLower(s.ToArray()));
		}

		private static float Bits(uint u)
		{
			return BitConverter.UInt32BitsToSingle(u);
		}

		private static RespawnPoint Spot(uint id, uint mapId, uint x, uint y, uint z, float restorationLevel)
		{
			return new RespawnPoint
			{
				Id = id,
				MapId = mapId,
				PositionX = Bits(x),
				PositionY = Bits(y),
				PositionZ = Bits(z),
				RestorationLevel = restorationLevel
			};
		}
	}
}
