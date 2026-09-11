using System;
using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The kill chain. Every expectation is measured off the corpus by `--report chain-kill` over its 67
	// ChainKillIIR records:
	//   * counts are 2 (x32) and 3 (x3) and never 1 - the first kill of a chain is silent;
	//   * every non-zero is closed by a 0, 32 of 32, a median 8.97s later (spread 7.90-9.22) - the window;
	//   * escalations (2 -> 3) all land inside that window, at 1.36-7.83s.
	public class KillChainTests
	{
		private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

		[Fact]
		public void TheFirstKillOfAChainIsSilent()
		{
			var player = Player();
			Assert.Null(player.RegisterKillInChain(T0));
		}

		[Fact]
		public void TheSecondKillInsideTheWindowAnnouncesTwo_AndTheThirdThree()
		{
			var player = Player();
			player.RegisterKillInChain(T0);

			Assert.Equal(2u, player.RegisterKillInChain(T0.AddSeconds(1.36))); // the corpus's fastest escalation
			Assert.Equal(3u, player.RegisterKillInChain(T0.AddSeconds(9.19))); // ...and its slowest, 7.83s later
		}

		[Fact]
		public void EachKillExtendsTheWindow_SoASlowStreakKeepsCounting()
		{
			var player = Player();
			var at = T0;
			for (var i = 0; i < 5; i++)
			{
				at = at.AddSeconds(WorldEntity.ChainKillWindowSeconds - 0.5);
				player.RegisterKillInChain(at);
			}

			Assert.Equal(5u, player.KillChain); // the window is since the last kill, not since the chain started
		}

		[Fact]
		public void AKillPastTheWindowStartsAFreshChain_WhichIsSilentAgain()
		{
			var player = Player();
			player.RegisterKillInChain(T0);
			Assert.Equal(2u, player.RegisterKillInChain(T0.AddSeconds(1)));

			var afterTheLapse = T0.AddSeconds(1 + WorldEntity.ChainKillWindowSeconds + 0.1);
			Assert.Null(player.RegisterKillInChain(afterTheLapse));
		}

		[Fact]
		public void AnAnnouncedChainLapsesExactlyOnce_AndAnUnannouncedOneNeverDoes()
		{
			var player = Player();
			player.RegisterKillInChain(T0);
			player.RegisterKillInChain(T0.AddSeconds(1)); // announced 2

			var lapse = T0.AddSeconds(1 + WorldEntity.ChainKillWindowSeconds + 0.1);
			Assert.False(player.TryLapseKillChain(T0.AddSeconds(5))); // still inside the window
			Assert.True(player.TryLapseKillChain(lapse)); // -> send the closing 0
			Assert.False(player.TryLapseKillChain(lapse.AddSeconds(60))); // ...and only once
			Assert.Equal(0u, player.KillChain);

			// A single kill was never announced, so its lapse has nothing to close.
			var loner = Player();
			loner.RegisterKillInChain(T0);
			Assert.False(loner.TryLapseKillChain(T0.AddSeconds(WorldEntity.ChainKillWindowSeconds + 0.1)));
			Assert.Equal(0u, loner.KillChain);
		}

		private static WorldEntity Player()
		{
			return new WorldEntity(0x01317CF8, 4010, true, "Killer", null!)
			{
				Position = Vector3.Zero
			};
		}
	}
}
