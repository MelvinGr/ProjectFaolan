using System;
using System.Collections.Generic;
using System.Threading;

namespace Faolan.Core.Helpers
{
	/// <summary>
	///     Counts failed logins per account name and per remote address over a sliding window, and refuses a further try
	///     once either has failed too often. Used by the game's UniverseAgent and Faolan.Web's sign-in, each with its own
	///     instance, since they are separate processes.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Two keys because each stops a different attack: the account limit stops guessing one account's password
	///         from many addresses, the address limit stops one address guessing across many accounts (or enumerating
	///         names). A success clears its account's count but not its address's, so a guesser holding one valid
	///         account cannot reset its own address between guesses at others.
	///     </para>
	///     <para>
	///         A refusal is decided before the password is checked, so a blocked key also stops costing a PBKDF2 hash per
	///         try. The block lifts on its own as the oldest failure ages out of the window. The state is per process and
	///         dies with it: a restart forgives everyone, which is acceptable for a throttle.
	///     </para>
	/// </remarks>
	public sealed class LoginThrottle
	{
		/// <summary>Failures an account name may collect inside <see cref="Window" />.</summary>
		public const int AccountLimit = 5;

		/// <summary>
		///     Failures one address may collect inside <see cref="Window" />. Higher than the account limit because one
		///     address can be several players behind a NAT.
		/// </summary>
		public const int AddressLimit = 20;

		// Keys kept before a failure sweeps the ones whose failures have all aged out, so a stream of made-up names
		// cannot grow the tables without bound.
		private const int SweepAbove = 10_000;

		/// <summary>How long a failure counts against its keys.</summary>
		public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

		private readonly Dictionary<string, Queue<DateTimeOffset>> _accounts = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, Queue<DateTimeOffset>> _addresses = new(StringComparer.Ordinal);
		private readonly Lock _gate = new();
		private readonly TimeProvider _time;

		public LoginThrottle(TimeProvider time)
		{
			ArgumentNullException.ThrowIfNull(time);
			_time = time;
		}

		/// <summary>
		///     True when <paramref name="account" /> or <paramref name="address" /> has failed its limit inside the window;
		///     the caller refuses without checking the password. A null address (not known) is not counted.
		/// </summary>
		public bool IsBlocked(string account, string? address)
		{
			var now = _time.GetUtcNow();
			lock (_gate)
			{
				return Count(_accounts, account, now) >= AccountLimit || address != null && Count(_addresses, address, now) >= AddressLimit;
			}
		}

		/// <summary>Counts a failed login against both keys.</summary>
		public void RecordFailure(string account, string? address)
		{
			var now = _time.GetUtcNow();
			lock (_gate)
			{
				Add(_accounts, account, now);
				if (address != null)
					Add(_addresses, address, now);
			}
		}

		/// <summary>Forgives the account's failures once it has logged in; its address's stay.</summary>
		public void RecordSuccess(string account)
		{
			lock (_gate)
				_accounts.Remove(account);
		}

		private static void Add(Dictionary<string, Queue<DateTimeOffset>> table, string key, DateTimeOffset now)
		{
			if (table.Count > SweepAbove)
				Sweep(table, now);

			if (!table.TryGetValue(key, out var failures))
				table[key] = failures = new Queue<DateTimeOffset>();

			failures.Enqueue(now);
		}

		private static int Count(Dictionary<string, Queue<DateTimeOffset>> table, string key, DateTimeOffset now)
		{
			if (!table.TryGetValue(key, out var failures))
				return 0;

			while (failures.Count > 0 && now - failures.Peek() >= Window)
				failures.Dequeue();

			if (failures.Count == 0)
				table.Remove(key);

			return failures.Count;
		}

		private static void Sweep(Dictionary<string, Queue<DateTimeOffset>> table, DateTimeOffset now)
		{
			foreach (var key in new List<string>(table.Keys))
				Count(table, key, now);
		}
	}
}
