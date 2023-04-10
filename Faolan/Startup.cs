using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.AgentServer;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.CSPlayerAgent;
using Faolan.Extensions;
using Faolan.GameServer;
using Faolan.PlayerAgent;
using Faolan.UniverseAgent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan
{
	public class Startup
	{
		public IConfiguration Configuration { get; }

		public Startup(IConfiguration configuration)
		{
			Configuration = configuration;
		}

		public void ConfigureServices(IServiceCollection services)
		{
			services.AddLogging();

			services.AddDbContext<IDatabaseContext, DatabaseContext>(options => { options.UseSqlite($"Data Source={Configuration.DatabasePath()}", x => x.MigrationsAssembly("Faolan")); });

			services.AddScoped<IDatabaseRepository, DatabaseRepository>();
			services.AddHostedService<DatabaseMigrator>();

			services.AddHostedService<UniverseAgentListener>();
			services.AddHostedService<PlayerAgentListener>();
			services.AddHostedService<CsPlayerAgentListener>();

			//services.AddHostedService<AgentServerListener>(); // workaround below
			services.AddSingleton<AgentServerListener>();
			services.AddHostedService(p => p.GetRequiredService<AgentServerListener>());

			services.AddHostedService(provider =>
			{
				var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
				var configuration = provider.GetRequiredService<IConfiguration>();
				var dataRepository = provider.GetRequiredService<IDatabaseRepository>();
				var agentServerListener = provider.GetRequiredService<AgentServerListener>();

				var realm = dataRepository.Context.Realms.FirstOrDefault();
				if (realm == null)
					throw new Exception("realm == null");

				return new GameServerListener(loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{realm.Id}"),
					configuration, dataRepository, realm.Port, agentServerListener);
			});
		}

		[STAThread]
		public static async Task Main(string[] args)
		{
			Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
			Console.WriteLine(Statics.Banner);

			var host = Host.CreateDefaultBuilder(args)
				.ConfigureAppConfiguration((hostingContext, config) =>
				{
					config.SetBasePath(Directory.GetCurrentDirectory());
					config.AddJsonFile(Path.GetFullPath(Statics.ConfigFilePath));
					config.AddCommandLine(args);
					//config.AddEnvironmentVariables();
				})
				.ConfigureLogging(builder =>
				{
					builder.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

					//builder.AddDebug();
					builder.AddSimpleConsole(options =>
					{
						options.IncludeScopes = true;
						options.SingleLine = true;
					});
				})
				.UseStartup<Startup>()
				.Build();

			await host.RunAsync();
		}
	}

	public class DatabaseMigrator : BackgroundService
	{
		private readonly IDatabaseContext _databaseContext;

		public DatabaseMigrator(IDatabaseContext databaseContext)
		{
			_databaseContext = databaseContext;
		}

		protected override Task ExecuteAsync(CancellationToken cancellationToken)
		{
			return _databaseContext.Database.MigrateAsync(cancellationToken);
		}
	}

	public class DatabaseContextFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>();
			options.UseSqlite("Data Source=faolan.db", x => x.MigrationsAssembly("Faolan"));
			return new DatabaseContext(options.Options);
		}
	}
}
