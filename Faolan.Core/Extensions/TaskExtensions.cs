using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Extensions
{
	public static class TaskExtensions
	{
		extension(Task)
		{
			/// <summary>
			///     Fire-and-forget: after <paramref name="delay" /> run <paramref name="action" /> on the thread pool,
			///     swallowing + logging any exception (a detached continuation must never crash the caller, and an
			///     unobserved faulted task is a silent leak).
			/// </summary>
			/// <remarks>
			///     The action starts detached from the caller's flow, so it does not inherit a <c>WorkLoop</c>'s "running on
			///     this loop" mark: an action that touches a zone posts itself to that zone's loop, and run with the mark
			///     it would execute inline instead, beside whatever the zone is doing by then.
			/// </remarks>
			public static void RunAfter(TimeSpan delay, Func<Task> action, ILogger? logger = null, [CallerMemberName] string? caller = null)
			{
				using (ExecutionContext.SuppressFlow())
				{
					_ = Task.Run(async () =>
					{
						try
						{
							await Task.Delay(delay);
							await action();
						}
						catch (Exception ex)
						{
							logger?.LogWarning(ex, "Task.RunAfter deferred action (from {Caller}) failed.", caller);
						}
					});
				}
			}

			/// <summary>Millisecond overload of <see cref="RunAfter(TimeSpan,Func{Task},ILogger,string)" />.</summary>
			public static void RunAfter(int delayMs, Func<Task> action, ILogger? logger = null, [CallerMemberName] string? caller = null)
			{
				Task.RunAfter(TimeSpan.FromMilliseconds(delayMs), action, logger, caller);
			}
		}
	}
}
