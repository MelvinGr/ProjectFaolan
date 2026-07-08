using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CreateFlowService = Faolan.GameServer.CreateFlow.CreateFlowService;

namespace Faolan.Tests
{
	// Locks the byte-exact output of the server's verbatim packet bursts so they can be converted from literal
	// byte blobs to explicit WriteHeader()/Write*() calls WITHOUT changing a single wire byte (the client is
	// proven to accept these exact bytes; any drift hangs it on the loading screen). The golden .bin is captured
	// once from the pre-conversion code, committed, and asserted forever after. Each burst is fully determined by
	// the client instance, so a fixed instance gives deterministic output.
	public class BurstGoldenTests
	{
		private const uint CreateWorldCapturedInstance = 0x01316E71; // the hoxxling create capture's character instance
		private const uint TestInstance = 0x02000001; // a valid 4-byte-varint character id (retail range)

		// The create-world INTRO CUTSCENE burst (RetailCreateWorldEntryBurst, #234): chunk 0 (SCENE) on the world
		// Hello, then one chunk per client cutscene trigger. Run with the CAPTURED instance (0x01316E71) so the
		// re-addressed output is byte-identical to the retail capture this burst was generated from - the golden .bin
		// is those exact retail bytes. This locks the burst so the step-by-step construction (#234 steps 4-6) can be
		// proven to not drift a single wire byte.
		//
		// PROVEN equal to retail (Captures/scripts/decode_createworld_burst.py --verify-golden): the 604 emitted packets are
		// byte-identical (opcode + body) to the create capture's :7012 s2c stream [4:608]; the 4 leading packets the
		// burst omits (0x2000/0x20D0/0x20C5/0x2038) are the pre-Hello auth/redirect handshake, sent by the auth path
		// (ReportDimensionId/ReportServerId/AckAuthenticate), not the cutscene burst.
		[Fact]
		public Task CreateWorldEntryBurst_IsByteStable()
		{
			return AssertBurstStable("CreateWorldEntryBurst",
				async c =>
				{
					await RetailCreateWorldEntryBurst.SendCreateWorldSceneBurst(c);
					for (var i = 0; i < 5; i++)
						await RetailCreateWorldEntryBurst.SendNextCreateWorldChunk(c);
				},
				CreateWorldCapturedInstance);
		}

		[Fact]
		public Task CreationScriptBurst_IsByteStable()
		{
			return AssertBurstStable("CreationScriptBurst", CreateFlowService.SendRetailCreateEditorCreationScriptBurst);
		}

		// NOTE: the InitialBurst golden is asserted by CreateEditorSceneDynelTests.InitialBurst_WithDb_IsByteIdenticalToGolden
		// instead - its galley scene-prop dynels are now SPAWNED FROM the DB (WorldObjectBuilder), so the burst needs a DB
		// to reproduce the golden; a bare no-DB run here would omit them.

		[Fact]
		public Task WorldPrepBurst_IsByteStable()
		{
			// Needs a DB: the starting-ability grants are now built from Ability rows (GetAbility) rather than verbatim,
			// so the burst reproduces the golden only when the committed faolan.db supplies the 7 abilities.
			return AssertBurstStable("WorldPrepBurst", c => CreateFlowService.SendWorldPrepBurst(c, Repo()));
		}

		private static async Task AssertBurstStable(string name, Func<RecordingNetworkClient, Task> run, uint instance = TestInstance)
		{
			var client = new RecordingNetworkClient(instance);
			await run(client);
			var bytes = client.Sent.SelectMany(p => p).ToArray();

			var path = Path.Combine(GoldenDir(), name + ".bin");
			if (!File.Exists(path))
			{
				Directory.CreateDirectory(GoldenDir());
				await File.WriteAllBytesAsync(path, bytes);
			}

			var golden = await File.ReadAllBytesAsync(path);
			if (!Enumerable.SequenceEqual(golden, bytes))
				await File.WriteAllBytesAsync(path + ".actual", bytes);
			Assert.Equal(golden, bytes);
		}

		private static string GoldenDir([CallerFilePath] string thisFile = "")
		{
			return Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");
		}

		private static DatabaseRepository Repo([CallerFilePath] string thisFile = "")
		{
			var db = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!, "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			return new DatabaseRepository(new Factory(options));
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseContext(options));
		}
	}
}
