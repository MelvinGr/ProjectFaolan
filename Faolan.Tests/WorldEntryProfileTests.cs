using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.GameServer;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Faolan.Tests
{
	// An in-process PROFILE of the world entry (#938): the real realm container, a temp copy of the real database,
	// and a recorded client, with one or more characters pushed through the actual `SendBurstlessWorldEntry`. It
	// prints the per-step breakdown the entry now logs, plus the lazily built catalogs and the terrain warm, so the
	// "first player in the map" spike can be attributed without a client, a profiler or a login.
	//
	// OPT-IN, because it reads THIS checkout's faolan.db - the maintainer's play state - and takes seconds:
	//
	//   FAOLAN_PROFILE_ENTRY=<characterId>[,<characterId>...] dotnet test --project Faolan.Tests -- --filter-class '*WorldEntryProfile*'
	//
	// The FIRST character pays every once-per-server cost (the catalogs) and every once-per-map cost (the zone's NPCs
	// and objects, the terrain); a second one on the same map pays only its own rows. That difference is the whole
	// point of running two.
	//
	// ⚠️ The database is COPIED to the temp dir first: the entry writes (the character's transform, the realm's
	// status) and the committed faolan.db must not be dirtied by a measurement.
	public class WorldEntryProfileTests
	{
		[Fact]
		public async Task ProfileWorldEntry()
		{
			var ids = (Environment.GetEnvironmentVariable("FAOLAN_PROFILE_ENTRY") ?? "")
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(uint.Parse)
				.ToArray();
			Assert.SkipWhen(ids.Length == 0, "opt-in: set FAOLAN_PROFILE_ENTRY=<characterId>[,<characterId>] to profile the world entry against a copy of this checkout's faolan.db");

			var source = TestPaths.Repo("faolan.db");
			var copy = Path.Combine(Path.GetTempPath(), $"faolan-entry-profile-{Guid.NewGuid():N}.db");
			File.Copy(source, copy);
			var lines = new List<string>();
			try
			{
				var configuration = new ConfigurationBuilder()
					.AddInMemoryCollection(FaolanSettings.Defaults)
					.AddJsonFile(Global.ConfigPath, true)
					.AddJsonFile(Global.UserConfigPath, true)
					.AddInMemoryCollection(new Dictionary<string, string?>
					{
						[FaolanSettings.Keys.DatabaseProvider] = "Sqlite",
						[FaolanSettings.Keys.DatabaseConnectionString] = $"Data Source={copy}"
					})
					.Build();

				var services = new ServiceCollection();
				services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new ListLoggerProvider(lines)));
				services.AddSingleton<IConfiguration>(configuration);
				services.ConfigureFaolanDatabase(configuration);
				// The real dispatchers with no provider registered behind them: every call is null-safe by design, which
				// is exactly the "no chat tier, no CS tier on this process" shape they exist to express.
				services.AddSingleton<IChatServerDispatcher, ChatServerDispatcher>();
				services.AddSingleton<IGameServerDispatcher, GameServerDispatcher>();
				services.AddSingleton<ICsPlayerAgentDispatcher, CsPlayerAgentDispatcher>();
				var realm = new HostRealm(1, "Profile", 7014, 7012, 7001);
				services.AddSingleton(realm);
				services.AddRealmGameServer(realm);

				await using var provider = services.BuildServiceProvider();
				var repository = provider.GetRequiredService<IDatabaseRepository>();
				var entry = provider.GetRequiredService<WorldEntryService>();
				var terrain = provider.GetRequiredService<TerrainService>();

				var report = new List<string>();
				foreach (var id in ids)
				{
					var character = await repository.GetCharacter(id);
					Assert.True(character != null, $"no character {id} in {source}");

					var client = new RecordingNetworkClient(character!.Id);
					client.Session.Character = character;
					client.Session.EntryMode = WorldEntryMode.Login;

					// The terrain warm is fire-and-forget inside the entry, so it never shows in the entry's own
					// breakdown. It IS a first-player-in-the-map cost, so it is timed here, synchronously, first.
					var clock = System.Diagnostics.Stopwatch.StartNew();
					terrain.Warm(character.MapId);
					var terrainMs = clock.ElapsedMilliseconds;

					var timing = await entry.SendBurstlessWorldEntry(client, character.MapId);
					report.Add($"character {id} (level {character.Level}, {character.Class}) on map {character.MapId}: terrain warm {terrainMs} ms; entry {timing}; {client.Sent.Count} packets sent");

					// FAOLAN_PROFILE_DUMP=<dir>: one line per packet sent, `<index> <length> <sha256>`, so an entry can be
					// diffed byte-for-byte against the same entry on another build - the check that a query rewrite
					// changed the COST and not the WIRE. (The game-time packet carries the clock and differs by design.)
					if (Environment.GetEnvironmentVariable("FAOLAN_PROFILE_DUMP") is { Length: > 0 } dumpDir)
					{
						Directory.CreateDirectory(dumpDir);
						var hashes = client.Sent.Select((packet, i) => $"{i} {packet.Length} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packet))} {Convert.ToHexString(packet.AsSpan(0, Math.Min(24, packet.Length)))}");
						await File.WriteAllLinesAsync(Path.Combine(dumpDir, $"entry-{id}.txt"), hashes, TestContext.Current.CancellationToken);
						for (var i = 0; i < client.Sent.Count; i++)
							await File.WriteAllTextAsync(Path.Combine(dumpDir, $"entry-{id}-{i:D2}.hex"), Convert.ToHexString(client.Sent[i]), TestContext.Current.CancellationToken);
					}
				}

				report.Add("");
				report.Add("once-per-server catalogs and once-per-map loads, from the log:");
				// Every log line that states a cost, from whichever loader wrote it - the entry's own summary line excluded,
				// since it is already in the report above.
				report.AddRange(lines.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\b\d+ ms\b") && !l.StartsWith("Burst-less", StringComparison.Ordinal)).Select(l => "  " + l));

				var text = string.Join(Environment.NewLine, report);
				var outPath = Path.Combine(Path.GetTempPath(), "faolan-entry-profile.txt");
				await File.WriteAllTextAsync(outPath, text, TestContext.Current.CancellationToken);
				TestContext.Current.TestOutputHelper?.WriteLine(text);
				TestContext.Current.TestOutputHelper?.WriteLine($"(written to {outPath})");
				Console.WriteLine(text);
			}
			finally
			{
				foreach (var suffix in new[] { "", "-wal", "-shm" })
					File.Delete(copy + suffix);
			}
		}

		// Captures every log line the container writes, so the catalog-load and zone-load timings can be reported
		// beside the entry's own breakdown.
		private sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
		{
			public ILogger CreateLogger(string categoryName)
			{
				return new ListLogger(lines);
			}

			public void Dispose()
			{
			}

			private sealed class ListLogger(List<string> lines) : ILogger
			{
				public IDisposable? BeginScope<TState>(TState state) where TState : notnull
				{
					return null;
				}

				public bool IsEnabled(LogLevel logLevel)
				{
					return logLevel >= LogLevel.Information;
				}

				public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				{
					lock (lines)
						lines.Add(formatter(state, exception));
				}
			}
		}
	}
}
