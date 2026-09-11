using System;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.GameServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The realm's service graph resolves. `AddRealmGameServer` builds the sixty-odd world services through the
	// container, which is a runtime construction sequence rather than a compile-time one - so a missing registration
	// is not a build error, and without this gate it would surface as a failed boot.
	//
	// `ValidateOnBuild` walks every registration's constructor and reports every unsatisfied dependency at once,
	// without invoking a single factory - so nothing here touches a database, a socket or the file system.
	public class GameServerCompositionTests
	{
		[Fact]
		public void EveryRealmServiceCanBeConstructed_IncludingTheListenerItself()
		{
			var services = Externals();
			services.AddRealmGameServer(Realm);
			services.AddSingleton<GameServerListener>();

			// Throws an AggregateException naming every unresolvable service if any registration is missing.
			using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
			Assert.NotNull(provider);
		}

		// The realm registration provides the bare, realm-tagged `ILogger` every composed service takes. Its category
		// is what lets a multi-realm deployment tell two realms' lines apart in one log, so it is asserted rather than
		// assumed - a plain `ILogger` registration that lost the tag would be invisible otherwise.
		[Fact]
		public void TheBareLogger_IsRealmTagged()
		{
			var services = Externals();
			services.AddRealmGameServer(Realm);

			using var provider = services.BuildServiceProvider();
			var logger = provider.GetRequiredService<ILogger>();

			Assert.Equal($"{typeof(GameServerListener).FullName}-{Realm.Id}", ((TestLoggerFactory)provider.GetRequiredService<ILoggerFactory>()).LastCategory);
			Assert.NotNull(logger);
		}

		// The guard on the registration above: a bare `ILogger` singleton is realm-scoped, so any other
		// container-resolved type asking for a non-generic ILogger would silently inherit the GameServer's realm
		// category. Every listener takes `ILogger<T>` (or, for the GameServer, the factory it tags with) - this fails
		// if a new one starts taking the bare form.
		[Fact]
		public void NoListenerTakesABareLogger()
		{
			var listeners = typeof(GameServerListener).Assembly.GetTypes()
				.Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Listener", StringComparison.Ordinal))
				.ToList();
			Assert.NotEmpty(listeners);

			foreach (var listener in listeners)
			foreach (var ctor in listener.GetConstructors())
			{
				var bare = ctor.GetParameters().Where(p => p.ParameterType == typeof(ILogger)).ToList();
				Assert.True(bare.Count == 0,
					$"{listener.Name} takes a bare ILogger ('{string.Join(", ", bare.Select(p => p.Name))}'). It would inherit the GameServer's "
					+ "realm-tagged category from AddRealmGameServer. Take ILogger<T>, or ILoggerFactory if the category must be built.");
			}
		}

		private static HostRealm Realm => new HostRealm(7, "Test Realm", 7014, 7012, 7001);

		private static ServiceCollection Externals()
		{
			var services = new ServiceCollection();
			services.AddSingleton<ILoggerFactory>(new TestLoggerFactory());
			services.AddSingleton(Realm);
			// Registered as factories that are never invoked: ValidateOnBuild checks the call sites, it does not run
			// them, so the graph is proved without a database, a socket or a config file.
			services.AddSingleton<IConfiguration>(_ => throw new InvalidOperationException("not built"));
			services.AddSingleton<IDatabaseRepository>(_ => throw new InvalidOperationException("not built"));
			services.AddSingleton<IChatServerDispatcher>(_ => throw new InvalidOperationException("not built"));
			services.AddSingleton<IGameServerDispatcher>(_ => throw new InvalidOperationException("not built"));
			services.AddSingleton<ICsPlayerAgentDispatcher>(_ => throw new InvalidOperationException("not built"));
			return services;
		}

		private sealed class TestLoggerFactory : ILoggerFactory
		{
			public string? LastCategory { get; private set; }

			public ILogger CreateLogger(string categoryName)
			{
				LastCategory = categoryName;
				return NullLogger.Instance;
			}

			public void AddProvider(ILoggerProvider provider)
			{
			}

			public void Dispose()
			{
			}
		}
	}
}
