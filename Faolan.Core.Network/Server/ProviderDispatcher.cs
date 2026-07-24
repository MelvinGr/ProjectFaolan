using System;
using System.Threading;

namespace Faolan.Core.Network.Server
{
	/// <summary>
	///     Late-bound slot holding the ONE live listener that implements <typeparamref name="TProvider" />.
	///     <para>
	///         Listeners can't simply be injected into each other: chat and world depend on each other
	///         mutually (the chat listener routes ".cmd" GM commands into the world; the world sends system
	///         messages back through chat), the MCP tools live in a project that cannot even reference the
	///         listener types, and a listener only exists once the host starts it. So each listener registers
	///         itself here on start and unregisters on stop, and consumers depend on this seam instead.
	///     </para>
	///     <para>
	///         A process hosts at most ONE realm (<c>--role realm=&lt;id&gt;</c>, #324), so the slot holds one
	///         provider - registering a second distinct one is a wiring bug, not a routing case. Derived
	///         dispatchers implement the provider interface itself and forward through
	///         <see cref="Provider" />, absorbing "nothing registered" into that op's natural empty answer
	///         (false / empty / null) so the ~100 call sites don't each repeat the null check.
	///     </para>
	/// </summary>
	public abstract class ProviderDispatcher<TProvider> where TProvider : class
	{
		private readonly Lock _lock = new Lock();
		private TProvider? _provider;

		/// <summary>The registered listener, or null while none is running (before start / after shutdown).</summary>
		protected TProvider? Provider
		{
			get
			{
				lock (_lock)
				{
					return _provider;
				}
			}
		}

		public void Register(TProvider provider)
		{
			lock (_lock)
			{
				if (_provider != null && _provider != provider)
					throw new InvalidOperationException($"Only one {typeof(TProvider).Name} can be registered at a time.");

				_provider = provider;
			}
		}

		public void Unregister(TProvider provider)
		{
			lock (_lock)
			{
				if (_provider == provider)
					_provider = null;
			}
		}
	}
}
