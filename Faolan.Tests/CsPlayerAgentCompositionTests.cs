using Faolan.Core.Database.Repositories;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Faolan.Tests
{
	// The CS tier's services are container-registered (`AddRealmCsPlayerAgent`), so a missing registration is a
	// runtime failure at host start rather than a build error - the same trade `GameServerCompositionTests` gates
	// for the realm services. `ValidateOnBuild` walks every constructor without invoking a factory, so nothing here
	// opens a socket or a database.
	public class CsPlayerAgentCompositionTests
	{
		[Fact]
		public void EveryCsServiceCanBeConstructed_IncludingTheListenerItself()
		{
			var services = new ServiceCollection();
			services.AddLogging();
			services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
			services.AddSingleton(new HostRealm(7, "Test Realm", 7014, 7012, 7001));
			// Externals the tier takes but does not own: factories, so ValidateOnBuild sees a registration and never
			// runs one.
			services.AddSingleton<IDatabaseRepository>(_ => null!);
			services.AddSingleton<IGameServerDispatcher>(_ => null!);
			services.AddSingleton<ICsPlayerAgentDispatcher>(_ => null!);
			services.AddSingleton<IChatServerDispatcher>(_ => null!);

			services.AddRealmCsPlayerAgent();
			services.AddSingleton<CsPlayerAgentListener>();

			using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
			Assert.NotNull(provider);
		}
	}
}
