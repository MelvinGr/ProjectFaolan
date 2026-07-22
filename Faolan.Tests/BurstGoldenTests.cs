using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Faolan.GameServer.CreateFlow;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the byte-exact output of the server's verbatim packet bursts so they can be converted from literal
	// byte blobs to explicit WriteHeader()/Write*() calls WITHOUT changing a single wire byte (the client is
	// proven to accept these exact bytes; any drift hangs it on the loading screen). The golden .bin is captured
	// once from the pre-conversion code, committed, and asserted forever after. Each burst is fully determined by
	// the client instance, so a fixed instance gives deterministic output.
	public class BurstGoldenTests
	{
		private const uint TestInstance = 0x02000001; // a valid 4-byte-varint character id (retail range)

		[Fact]
		public Task CreationScriptBurst_IsByteStable()
		{
			return AssertBurstStable("CreationScriptBurst", CreateFlowService.SendRetailCreateEditorCreationScriptBurst);
		}

		// NOTE: the InitialBurst golden is asserted by CreateEditorSceneDynelTests.InitialBurst_WithDb_IsByteIdenticalToGolden
		// instead - its galley scene-prop dynels are now SPAWNED FROM the DB (WorldObjectBuilder), so the burst needs a DB
		// to reproduce the golden; a bare no-DB run here would omit them.

		// NOTE: the WorldPrepBurst golden was retired with SendWorldPrepBurst - the create-flow world-prep grant burst
		// was proven redundant in-client and removed; grants now come from the shared class-driven world-entry loadout.

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
	}
}
