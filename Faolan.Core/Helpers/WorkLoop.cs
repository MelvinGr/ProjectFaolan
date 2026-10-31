using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Helpers
{
	/// <summary>
	///     A single owner for a piece of mutable state: work posted here runs one item at a time, in order, each to
	///     completion (its awaits included) before the next starts.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The game server gives one to each zone, the way retail gave each playfield its own server: every handler,
	///         tick and timer that touches a zone runs on its loop, so the zone's state needs no locks and two players'
	///         packets can no longer interleave inside it. Each client's packets arrive on its own connection thread,
	///         which is why that is not true without one.
	///     </para>
	///     <para>
	///         <see cref="Run" /> from work already running on this loop executes inline: the caller is that work,
	///         awaiting, so nothing else can be running here, and posting would deadlock. Work that outlives its item -
	///         a delay, a loop, a fire-and-forget - must be started through <see cref="Spawn" />, which stops the
	///         "running on this loop" mark from flowing into it; such work posts its steps back with <see cref="Run" />.
	///     </para>
	///     <para>
	///         Two loops must never wait on each other: whichever calls into the other with <see cref="Run" /> fixes the
	///         direction, and the other side only <see cref="Post" />s back.
	///     </para>
	/// </remarks>
	public sealed class WorkLoop
	{
		private static readonly AsyncLocal<WorkLoop?> Executing = new AsyncLocal<WorkLoop?>();

		private readonly ILogger _logger;
		private readonly string _name;
		private readonly Task _runner;

		private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
		{
			SingleReader = true
		});

		/// <summary>The loop whose work is running on this flow, or null outside any loop's work.</summary>
		public static WorkLoop? Current => Executing.Value;

		/// <summary>True when the calling code is this loop's own work.</summary>
		public bool IsCurrent => ReferenceEquals(Executing.Value, this);

		/// <summary>What this loop owns (a zone, the CS tier), so a caller can tell which kind of loop it is running on.</summary>
		public object? Owner { get; init; }

		public WorkLoop(string name, ILogger logger)
		{
			_name = name;
			_logger = logger;
			using (ExecutionContext.SuppressFlow())
				_runner = Task.Run(RunAsync);
		}

		/// <summary>
		///     Runs <paramref name="work" /> on this loop and completes when it has; its exception surfaces here.
		/// </summary>
		public Task Run(Func<Task> work)
		{
			if (IsCurrent)
				return work();

			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Enqueue(async () =>
			{
				try
				{
					await work();
					done.SetResult();
				}
				catch (Exception ex)
				{
					done.SetException(ex);
				}
			}, done);
			return done.Task;
		}

		/// <summary>The value-returning <see cref="Run(Func{Task})" />.</summary>
		public Task<T> Run<T>(Func<Task<T>> work)
		{
			if (IsCurrent)
				return work();

			var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			Enqueue(async () =>
			{
				try
				{
					done.SetResult(await work());
				}
				catch (Exception ex)
				{
					done.SetException(ex);
				}
			}, done);
			return done.Task;
		}

		/// <summary>
		///     Queues <paramref name="work" /> without waiting for it. A failure is logged, since nobody awaits it.
		/// </summary>
		public void Post(Func<Task> work)
		{
			if (!_work.Writer.TryWrite(work))
				_logger.LogWarning("{Loop}: work posted after the loop stopped was dropped.", _name);
		}

		/// <summary>
		///     Starts long-lived or delayed work off every loop: the "running on a loop" mark does not flow into it, so its
		///     own <see cref="Run" /> calls post rather than run inline beside the work that started it.
		/// </summary>
		public static void Spawn(Func<Task> work, ILogger logger, string what)
		{
			using (ExecutionContext.SuppressFlow())
			{
				_ = Task.Run(async () =>
				{
					try
					{
						await work();
					}
					catch (Exception ex)
					{
						logger.LogWarning(ex, "{What} failed.", what);
					}
				});
			}
		}

		/// <summary>
		///     Stops taking work and completes once everything already queued has run.
		/// </summary>
		public Task StopAsync()
		{
			_work.Writer.TryComplete();
			return _runner;
		}

		private void Enqueue(Func<Task> item, TaskCompletionSource done)
		{
			if (!_work.Writer.TryWrite(item))
				done.SetException(new InvalidOperationException($"{_name} has stopped."));
		}

		private void Enqueue<T>(Func<Task> item, TaskCompletionSource<T> done)
		{
			if (!_work.Writer.TryWrite(item))
				done.SetException(new InvalidOperationException($"{_name} has stopped."));
		}

		private async Task RunAsync()
		{
			Executing.Value = this;
			await foreach (var item in _work.Reader.ReadAllAsync())
			{
				try
				{
					await item();
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "{Loop}: posted work failed; the loop carries on.", _name);
				}
			}
		}
	}
}
