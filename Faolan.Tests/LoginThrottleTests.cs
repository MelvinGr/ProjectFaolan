using System;
using Faolan.Core.Helpers;
using Xunit;

namespace Faolan.Tests
{
	// Failed logins are counted per account name and per address over a sliding window. The account limit stops
	// guessing one password from many places, the address limit one place guessing across many accounts.
	public class LoginThrottleTests
	{
		[Fact]
		public void AnAccount_IsBlockedAtItsLimit_AndFreedAsTheOldestFailureAgesOut()
		{
			var clock = new Clock();
			var throttle = new LoginThrottle(clock);

			for (var i = 0; i < LoginThrottle.AccountLimit - 1; i++)
			{
				throttle.RecordFailure("Conan", $"10.0.0.{i}");
				clock.Advance(TimeSpan.FromMinutes(1));
			}

			Assert.False(throttle.IsBlocked("Conan", "10.0.1.1"));

			throttle.RecordFailure("conan", "10.0.0.99"); // the name is case-insensitive, as the account lookup is
			Assert.True(throttle.IsBlocked("CONAN", "10.0.1.1")); // from any address
			Assert.False(throttle.IsBlocked("Thoth", "10.0.1.1"));

			// The first failure was AccountLimit - 1 minutes before the last; once it is a full window old, one slot frees.
			clock.Advance(LoginThrottle.Window - TimeSpan.FromMinutes(LoginThrottle.AccountLimit - 1));
			Assert.False(throttle.IsBlocked("Conan", "10.0.1.1"));
		}

		[Fact]
		public void AnAddress_IsBlockedAcrossAccounts()
		{
			var throttle = new LoginThrottle(new Clock());

			for (var i = 0; i < LoginThrottle.AddressLimit; i++)
				throttle.RecordFailure($"name{i}", "10.0.0.1");

			Assert.True(throttle.IsBlocked("someone-else", "10.0.0.1"));
			Assert.False(throttle.IsBlocked("someone-else", "10.0.0.2"));
			Assert.False(throttle.IsBlocked("someone-else", null)); // an unknown address is not counted
		}

		// A guesser with one valid account must not be able to wipe its address's count by logging into it.
		[Fact]
		public void ASuccess_ForgivesTheAccount_ButNotTheAddress()
		{
			var throttle = new LoginThrottle(new Clock());

			for (var i = 0; i < LoginThrottle.AddressLimit - 1; i++)
				throttle.RecordFailure("victim", "10.0.0.1");

			throttle.RecordSuccess("victim");
			Assert.False(throttle.IsBlocked("victim", null));

			throttle.RecordFailure("other", "10.0.0.1");
			Assert.True(throttle.IsBlocked("mine", "10.0.0.1"));
		}

		private sealed class Clock : TimeProvider
		{
			private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

			public override DateTimeOffset GetUtcNow()
			{
				return _now;
			}

			public void Advance(TimeSpan by)
			{
				_now += by;
			}
		}
	}
}
