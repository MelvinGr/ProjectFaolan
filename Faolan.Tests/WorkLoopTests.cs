using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Helpers;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// A WorkLoop is the single owner of a zone's state (and the CS tier's): what it runs, it runs one item at a time,
	// each to completion, so two players' handlers cannot interleave inside one zone. These pin the rules the zones rely
	// on: serial execution, inline nesting, work that outlives its item not inheriting the loop, and no loop ever
	// waiting on a zone's work (which is what keeps two loops from waiting on each other).
	public class WorkLoopTests
	{
		private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

		// The point of the loop: a read-modify-write across an await is not torn by concurrent callers.
		[Fact]
		public async Task Run_ExecutesOneItemAtATime()
		{
			var loop = new WorkLoop("test", NullLogger.Instance);
			var counter = 0;

			await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => loop.Run(async () =>
			{
				var read = counter;
				await Task.Yield();
				counter = read + 1;
			})))).WaitAsync(Patience, TestContext.Current.CancellationToken);

			Assert.Equal(200, counter);
			await loop.StopAsync();
		}

		// Work already running on the loop that calls Run on it again executes inline: the caller is that work, awaiting,
		// so nothing else is running there, and queueing behind itself would never finish.
		[Fact]
		public async Task Run_FromTheLoopsOwnWork_RunsInline()
		{
			var loop = new WorkLoop("test", NullLogger.Instance);
			var inner = false;

			await loop.Run(async () => await loop.Run(() =>
			{
				inner = loop.IsCurrent;
				return Task.CompletedTask;
			})).WaitAsync(Patience, TestContext.Current.CancellationToken);

			Assert.True(inner);
			await loop.StopAsync();
		}

		// Work that outlives its item - a spawned loop, a delayed action - must not inherit the "running on this loop"
		// mark, or its own Run calls would execute inline beside whatever the loop is doing by then.
		[Fact]
		public async Task SpawnedAndDelayedWork_IsNotTheLoopsWork()
		{
			var loop = new WorkLoop("test", NullLogger.Instance);
			var spawned = new TaskCompletionSource<bool>();
			var delayed = new TaskCompletionSource<bool>();

			await loop.Run(() =>
			{
				WorkLoop.Spawn(() =>
				{
					spawned.SetResult(loop.IsCurrent);
					return Task.CompletedTask;
				}, NullLogger.Instance, "spawned");
				Task.RunAfter(1, () =>
				{
					delayed.SetResult(loop.IsCurrent);
					return Task.CompletedTask;
				});
				return Task.CompletedTask;
			});

			Assert.False(await spawned.Task.WaitAsync(Patience, TestContext.Current.CancellationToken));
			Assert.False(await delayed.Task.WaitAsync(Patience, TestContext.Current.CancellationToken));
			await loop.StopAsync();
		}

		// A failure surfaces to the caller that awaited it, and the loop carries on with the next item.
		[Fact]
		public async Task AFailure_ReachesItsCaller_AndTheLoopCarriesOn()
		{
			var loop = new WorkLoop("test", NullLogger.Instance);

			await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Run(() => throw new InvalidOperationException("boom")));
			Assert.Equal(7, await loop.Run(() => Task.FromResult(7)).WaitAsync(Patience, TestContext.Current.CancellationToken));
			await loop.StopAsync();
		}

		// Stopping runs what is already queued, then refuses more.
		[Fact]
		public async Task Stop_DrainsTheQueue_ThenRefuses()
		{
			var loop = new WorkLoop("test", NullLogger.Instance);
			var ran = 0;
			var queued = Enumerable.Range(0, 10).Select(_ => loop.Run(() =>
			{
				ran++;
				return Task.CompletedTask;
			})).ToList();

			await loop.StopAsync().WaitAsync(Patience, TestContext.Current.CancellationToken);

			await Task.WhenAll(queued);
			Assert.Equal(10, ran);
			await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Run(() => Task.CompletedTask));
		}

		// No loop waits on a zone's work. Called from another loop's work (the CS tier, another zone) while the zone is
		// busy, the call is queued and returns at once; the zone runs it when it is free. If it waited instead, a zone
		// waiting on that loop at the same moment would leave both waiting for ever.
		[Fact]
		public async Task ACallIntoAZone_FromAnotherLoop_IsQueuedNotAwaited()
		{
			var sim = TestWorld.Build().Sim;
			var client = new RecordingNetworkClient(0x01000001);
			var player = new WorldEntity(0x01000001, 4000, true, "Player", client);
			sim.Players[client] = player;
			var zone = sim.World.Place(player, 4000, Vector3.Zero, Quaternion.Identity);

			var release = new TaskCompletionSource();
			var zoneBusy = zone.Loop.Run(() => release.Task); // the zone is in the middle of something long
			var other = new WorkLoop("CS tier", NullLogger.Instance);
			var ran = new TaskCompletionSource();

			await other.Run(() => sim.RunForPlayer(client, () =>
			{
				ran.SetResult();
				return Task.CompletedTask;
			})).WaitAsync(Patience, TestContext.Current.CancellationToken);

			Assert.False(ran.Task.IsCompleted); // queued behind the busy zone, not run, and not waited for
			release.SetResult();
			await zoneBusy.WaitAsync(Patience, TestContext.Current.CancellationToken);
			await ran.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

			await other.StopAsync();
			await zone.StopAsync();
		}
	}
}
