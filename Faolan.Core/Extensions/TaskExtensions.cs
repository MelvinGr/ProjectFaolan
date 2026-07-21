using System;
using System.Runtime.CompilerServices;
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
			public static void RunAfter(TimeSpan delay, Func<Task> action, ILogger? logger = null, [CallerMemberName] string? caller = null)
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

			/// <summary>Millisecond overload of <see cref="RunAfter(TimeSpan,Func{Task},ILogger,string)" />.</summary>
			public static void RunAfter(int delayMs, Func<Task> action, ILogger? logger = null, [CallerMemberName] string? caller = null)
			{
				Task.RunAfter(TimeSpan.FromMilliseconds(delayMs), action, logger, caller);
			}
		}
	}
}
