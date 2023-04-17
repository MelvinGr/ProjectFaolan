using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.AgentServer;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Network.AgentServer;
using Faolan.CSPlayerAgent;
using Faolan.GameServer;
using Faolan.Helpers;
using Faolan.PlayerAgent;
using Faolan.UniverseAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan
{
	public static class Program
	{
		[STAThread]
		public static async Task Main(string[] args)
		{
			Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
			Console.WriteLine(Statics.StartupBanner);

			var builder = Host.CreateApplicationBuilder(args);

			if (Statics.IsDebug)
				builder.Environment.EnvironmentName = "Development";

			builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
			builder.Configuration.AddJsonFile(Path.GetFullPath("../../../faolan.json"));
			builder.Configuration.AddCommandLine(args);

			builder.Logging.SetMinimumLevel(Statics.IsDebug
				? LogLevel.Debug
				: LogLevel.Information);
			builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

			builder.Logging.AddSimpleConsole(options =>
			{
				options.IncludeScopes = true;
				options.SingleLine = true;
			});

			builder.Logging.AddProvider(new FileLoggerProvider("/home/melvin/Development/C#/Project Faolan/logs.txt"));

			builder.Services.AddLogging();

			builder.Services.ConfigureFaolanDatabase(builder.Configuration);
			builder.Services.AddScopedHostedService<DatabaseMigrator>();

			builder.Services.AddScopedHostedService<UniverseAgentListener>();
			builder.Services.AddScopedHostedService<PlayerAgentListener>();
			builder.Services.AddScopedHostedService<CsPlayerAgentListener>();

			builder.Services.AddSingleton<IAgentServerDispatcher, AgentServerDispatcher>();
			builder.Services.AddScopedHostedService<AgentServerListener>();

			builder.Services.AddScopedHostedService(provider =>
			{
				var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
				var configuration = provider.GetRequiredService<IConfiguration>();
				var dataRepository = provider.GetRequiredService<IDatabaseRepository>();
				var agentServerDispatcher = provider.GetRequiredService<IAgentServerDispatcher>();

				// One-time startup resolution of the listener's realm/port; blocking here is fine (host build).
				var realm = dataRepository.GetRealms().GetAwaiter().GetResult().FirstOrDefault();
				if (realm == null)
					throw new Exception("realm == null");

				return new GameServerListener(loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{realm.Id}"), configuration, dataRepository, realm.Port, agentServerDispatcher);
			});

			var host = builder.Build();
			await host.RunAsync();
		}
	}
}
