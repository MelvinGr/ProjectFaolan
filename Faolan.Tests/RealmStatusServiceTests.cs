using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #909: a realm was marked Online in `StartAsync` - i.e. before the host was known to be serving - and only
	// `StopAsync` flipped it back, which a crash skips. The two failures compound: a realm process logged "Realm 2
	// marked Online", then died on a bind with its ports never listening, and the login tier would have advertised it
	// and handed clients to a host that did not exist. It fails quietly, in the direction of pointing players at a
	// dead realm, which is why it gets a gate rather than a comment.
	public class RealmStatusServiceTests
	{
		private const uint RealmId = 2;

		[Fact]
		// Online now means "serving". Starting the hosted service must NOT claim to be up - that is the whole bug.
		public async Task StartAsync_DoesNotMarkOnline_AndHoldsTheRealmOffline()
		{
			var (service, realms, _) = Harness();

			await service.StartAsync(CancellationToken.None);

			Assert.Equal([RealmOnlineStatus.Offline], realms.Writes);
		}

		[Fact]
		// ...and the claim is what clears a stale Online left by this realm's own previous life. A crash, a kill -9, an
		// unhandled exception or a bind failure all skip StopAsync, so without this the row stays Online indefinitely -
		// including across a reboot, since nothing else re-reconciles it.
		public async Task StartAsync_ClearsAStaleOnline_LeftByAPreviousLife()
		{
			var (service, realms, _) = Harness(RealmOnlineStatus.Online);

			await service.StartAsync(CancellationToken.None);

			Assert.Equal(RealmOnlineStatus.Offline, realms.Status);
		}

		[Fact]
		// The flip happens on ApplicationStarted, which fires only once every hosted service has started AND the web
		// host (when there is one) has bound.
		public async Task TheRealmGoesOnline_OnlyWhenTheHostIsActuallyServing()
		{
			var (service, realms, lifetime) = Harness();
			await service.StartAsync(CancellationToken.None);

			lifetime.StartApplication();
			await realms.Settled();

			Assert.Equal([RealmOnlineStatus.Offline, RealmOnlineStatus.Online], realms.Writes);
		}

		[Fact]
		// A startup that fails after the claim never reaches ApplicationStarted, so the row stays Offline - the exact
		// sequence #909 reported, with the realm advertising itself and then dying.
		public async Task AStartupThatFailsAfterTheClaim_LeavesTheRealmOffline()
		{
			var (service, realms, _) = Harness(RealmOnlineStatus.Online);

			await service.StartAsync(CancellationToken.None);
			// ...and the host dies here: ApplicationStarted never fires.

			Assert.Equal(RealmOnlineStatus.Offline, realms.Status);
			Assert.DoesNotContain(RealmOnlineStatus.Online, realms.Writes);
		}

		[Fact]
		public async Task StopAsync_MarksTheRealmOffline()
		{
			var (service, realms, lifetime) = Harness();
			await service.StartAsync(CancellationToken.None);
			lifetime.StartApplication();
			await realms.Settled();

			await service.StopAsync(CancellationToken.None);

			Assert.Equal(RealmOnlineStatus.Offline, realms.Status);
		}

		[Fact]
		// Each process owns only its own realm row, so realm hosts on a shared DB never touch each other's.
		public async Task ItOnlyEverWritesItsOwnRealmRow()
		{
			var (service, realms, lifetime) = Harness();
			await service.StartAsync(CancellationToken.None);
			lifetime.StartApplication();
			await realms.Settled();
			await service.StopAsync(CancellationToken.None);

			Assert.All(realms.Ids, id => Assert.Equal(RealmId, id));
		}

		private static (RealmStatusService Service, FakeRealms Realms, FakeLifetime Lifetime) Harness(RealmOnlineStatus initial = RealmOnlineStatus.Offline)
		{
			var realms = new FakeRealms(initial);
			var lifetime = new FakeLifetime();
			var service = new RealmStatusService(realms, NullLogger<RealmStatusService>.Instance,
				new HostRealm(RealmId, "Faolan Test Realm", 7024, 7022, 7002), lifetime);

			return (service, realms, lifetime);
		}

		private sealed class FakeRealms : IRealmRepository
		{
			private readonly TaskCompletionSource _online = new TaskCompletionSource();

			public FakeRealms(RealmOnlineStatus initial)
			{
				Status = initial;
			}

			public List<RealmOnlineStatus> Writes { get; } = [];
			public List<uint> Ids { get; } = [];
			public RealmOnlineStatus Status { get; private set; }

			public Task<bool> SetRealmOnlineStatus(uint realmId, RealmOnlineStatus status)
			{
				Ids.Add(realmId);
				Writes.Add(status);
				Status = status;
				if (status == RealmOnlineStatus.Online)
					_online.TrySetResult();

				return Task.FromResult(true);
			}

			// The Online write happens on a lifetime callback, so it is not awaited by the caller.
			public Task Settled()
			{
				return _online.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}

			public Task<Realm?> GetRealm(uint id)
			{
				return Task.FromResult<Realm?>(null);
			}

			public Task<Realm[]> GetRealms()
			{
				return Task.FromResult<Realm[]>([]);
			}

			public Task<ChatMessage[]> GetRecentChatMessages(int limit = 100, uint? channelId = null)
			{
				return Task.FromResult<ChatMessage[]>([]);
			}

			public Task<bool> SaveChatMessage(ChatMessage message)
			{
				return Task.FromResult(true);
			}
		}

		private sealed class FakeLifetime : IHostApplicationLifetime
		{
			private readonly CancellationTokenSource _started = new CancellationTokenSource();
			private readonly CancellationTokenSource _stopped = new CancellationTokenSource();
			private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

			public CancellationToken ApplicationStarted => _started.Token;
			public CancellationToken ApplicationStopped => _stopped.Token;
			public CancellationToken ApplicationStopping => _stopping.Token;

			public void StartApplication()
			{
				_started.Cancel();
			}

			public void StopApplication()
			{
				_stopping.Cancel();
			}
		}
	}
}
