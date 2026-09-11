using System;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// A realm that crashes and is never restarted must stop being advertised.
	//
	// `OnlineStatus` means serving, and a realm's own restart clears the stale Online its previous life left behind.
	// That leaves the case with no restart: nothing re-reconciles the row, so the login tier keeps handing clients to
	// a host that is not there - a quiet failure in the wrong direction, and one that survives a reboot.
	//
	// A two-value column cannot decay on its own; the absence has to be observed. `Realm.LastSeenUtc` is that
	// observation and `RealmLiveness` is the rule read over the pair.
	public class RealmLivenessTests
	{
		private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

		// The whole point: Online plus a stale stamp is down. This is the crashed realm.
		[Fact]
		public void OnlineButStale_IsNotServing()
		{
			var stale = Now - RealmLiveness.StaleAfter - TimeSpan.FromSeconds(1);

			Assert.False(RealmLiveness.IsServing(RealmOnlineStatus.Online, stale, Now));
			Assert.Equal(RealmOnlineStatus.Offline, RealmLiveness.Effective(RealmOnlineStatus.Online, stale, Now));
		}

		[Fact]
		public void OnlineAndFresh_IsServing()
		{
			var fresh = Now - RealmLiveness.HeartbeatInterval;

			Assert.True(RealmLiveness.IsServing(RealmOnlineStatus.Online, fresh, Now));
			Assert.Equal(RealmOnlineStatus.Online, RealmLiveness.Effective(RealmOnlineStatus.Online, fresh, Now));
		}

		// A null stamp is not serving even with the flag set — a row whose host predates the heartbeat, or whose
		// flag was set by hand. Neither is evidence that anything is listening, and reading "no news" as good news is
		// the exact failure this exists to remove.
		[Fact]
		public void OnlineWithNoHeartbeatEver_IsNotServing()
		{
			Assert.False(RealmLiveness.IsServing(RealmOnlineStatus.Online, null, Now));
		}

		// The flag still has the final say in the down direction: a fresh stamp on an Offline row is a realm that
		// shut down cleanly seconds ago, and it must not be advertised.
		[Fact]
		public void OfflineIsOffline_HoweverFreshTheStamp()
		{
			Assert.False(RealmLiveness.IsServing(RealmOnlineStatus.Offline, Now, Now));
		}

		// Exactly at the ttl is still up. The boundary matters because the margin is the whole design: three
		// consecutive missed stamps must not take a healthy realm out of the list.
		[Fact]
		public void TheBoundaryIsInclusive()
		{
			Assert.True(RealmLiveness.IsServing(RealmOnlineStatus.Online, Now - RealmLiveness.StaleAfter, Now));
			Assert.False(RealmLiveness.IsServing(RealmOnlineStatus.Online, Now - RealmLiveness.StaleAfter - TimeSpan.FromTicks(1), Now));
		}

		// A stamp in the future is accepted rather than rejected. The two processes may be on different machines
		// and a little clock skew must not take a live realm down; only the past is measured.
		[Fact]
		public void AClockSkewedStampIsNotTreatedAsStale()
		{
			Assert.True(RealmLiveness.IsServing(RealmOnlineStatus.Online, Now + TimeSpan.FromMinutes(5), Now));
		}

		// The ttl must leave room for missed stamps, or a GC pause takes a healthy realm off the list. Asserted
		// rather than left to the two constants happening to be right: they are what the whole rule is calibrated on.
		[Fact]
		public void TheTtlAllowsSeveralMissedHeartbeats()
		{
			Assert.True(RealmLiveness.StaleAfter >= RealmLiveness.HeartbeatInterval * 3,
				$"a {RealmLiveness.StaleAfter} TTL on a {RealmLiveness.HeartbeatInterval} heartbeat leaves no margin for a missed stamp");
		}
	}
}
