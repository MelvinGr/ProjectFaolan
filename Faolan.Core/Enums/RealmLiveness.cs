using System;

namespace Faolan.Core.Enums
{
	/// <summary>
	///     Whether a realm row means "a host is serving this realm right now" - <see cref="RealmOnlineStatus" /> read
	///     together with the heartbeat that keeps it honest.
	/// </summary>
	/// <remarks>
	///     The flag alone cannot say it. A realm that crashes and is never restarted leaves <c>Online</c> in the row
	///     for ever, because nothing re-reconciles it. A two-value column cannot decay on its own; the absence has to
	///     be observed, and the observation is the stamp's age.
	///     <para />
	///     It is a pure function on purpose: a staleness rule copied per reader is a rule that drifts, so every
	///     consumer of <c>OnlineStatus</c> becomes a consumer of the pair by calling this rather than by re-deriving
	///     it.
	/// </remarks>
	public static class RealmLiveness
	{
		/// <summary>
		///     How long a realm may go without heartbeating before it is read as down.
		/// </summary>
		/// <remarks>
		///     Four times <see cref="HeartbeatInterval" />, so three consecutive missed stamps - a slow save, a GC
		///     pause, a moment of database contention - do not take a healthy realm out of the list. The cost of the
		///     margin is how long a crashed realm keeps being advertised, which this bounds.
		/// </remarks>
		public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

		/// <summary>How often a serving realm host stamps its row.</summary>
		public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

		/// <summary>
		///     Whether a realm should be treated as up: the flag says Online and the heartbeat is fresh.
		/// </summary>
		/// <remarks>
		///     A null <paramref name="lastSeenUtc" /> is not serving even when the flag says Online. That is the row of
		///     a realm whose host predates the heartbeat, or one whose flag was set by hand - neither is evidence that
		///     anything is listening, and reading "no news" as good news is the exact failure this exists to remove.
		///     <para />
		///     A stamp in the future is accepted rather than rejected: the two processes may be on different machines
		///     and a little clock skew must not take a live realm down. Only the past is measured.
		/// </remarks>
		public static bool IsServing(RealmOnlineStatus status, DateTimeOffset? lastSeenUtc, DateTimeOffset utcNow)
		{
			return status == RealmOnlineStatus.Online && lastSeenUtc is { } seen && utcNow - seen <= StaleAfter;
		}

		/// <summary>The status to show for a realm - <see cref="IsServing" /> collapsed back onto the wire enum.</summary>
		public static RealmOnlineStatus Effective(RealmOnlineStatus status, DateTimeOffset? lastSeenUtc, DateTimeOffset utcNow)
		{
			return IsServing(status, lastSeenUtc, utcNow) ? RealmOnlineStatus.Online : RealmOnlineStatus.Offline;
		}
	}
}
