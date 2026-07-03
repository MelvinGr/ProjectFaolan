using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Golden byte test for the hand-built fallback SpawnPlayer summary blocks (Send0x2000_SpawnPlayer). Those blocks
	// are now the generated client-schema types ProtoCoreCharacterIIR.CharacterStats (f4) / PlayerStats (f6); this
	// locks their serialized bytes so the fallback spawn stays wire-stable. The golden hex was captured from the
	// previous bespoke SpawnPlayerSummary/SpawnSummaryStats models (proven byte-identical before they were deleted).
	// NOTE: CharacterStats.radius is sint32/zig-zag, so the wire value 100 = a semantic radius of 50.
	public class SpawnSummaryGoldenTests
	{
		private const string GoldenStats = "080110910118CC014020580A68818306800100";

		private const string GoldenSummary = "0802100018032066280030ACBC0E38D4A724400048645088F70F58D8CA236000706478008001018801E7B730900150980150A00164A801C861B00101D00114E8010080023C900201";

		[Fact]
		public void CharacterStats_SerializesToGoldenSpawnF4()
		{
			var summary = new ProtoCoreCharacterIIR.CharacterStats
			{
				Sex = 2,
				Side = 0,
				Race = 3,
				MonsterScale = 102,
				MonsterData = 0,
				Life = 237100,
				Health = 594900,
				MaxStamina = 0,
				Stamina = 100,
				MaxMana = 261000,
				Mana = 583000,
				LooksPackage = 0,
				MountScale = 100,
				MountFlags = 0,
				Flags = 1,
				HeadMesh = 793575,
				Level = 80,
				ApprenticeLevel = 80,
				Radius = 50,
				Bcc = 12488,
				BccState = 1,
				TradeskillFlags = 20,
				CarsGroup = 0,
				RunSpeed = 60,
				OrientationMode = 1
			};

			Assert.Equal(GoldenSummary, Convert.ToHexString(summary.ToProtobuf()));
		}

		[Fact]
		public void PlayerStats_SerializesToGoldenSpawnF6()
		{
			var stats = new ProtoCoreCharacterIIR.PlayerStats
			{
				PvpLevel = 1,
				PvpTotalKills = 145,
				PvpTotalDeaths = 204,
				PlayerFlags = 32,
				Strength = 10,
				SubscriptionFlags = 98689,
				HardcoreLevel = 0
			};

			Assert.Equal(GoldenStats, Convert.ToHexString(stats.ToProtobuf()));
		}
	}
}
