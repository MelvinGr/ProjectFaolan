using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Faolan.GameServer;
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

		private static string GoldenDir([CallerFilePath] string thisFile = "") => Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");

		private static async Task AssertBurstStable(string name, System.Func<RecordingNetworkClient, Task> run)
		{
			var client = new RecordingNetworkClient(TestInstance);
			await run(client);
			var bytes = client.Sent.SelectMany(p => p).ToArray();

			var path = Path.Combine(GoldenDir(), name + ".bin");
			if (!File.Exists(path))
			{
				Directory.CreateDirectory(GoldenDir());
				await File.WriteAllBytesAsync(path, bytes);
			}

			var golden = await File.ReadAllBytesAsync(path);
			if (!golden.SequenceEqual(bytes))
				await File.WriteAllBytesAsync(path + ".actual", bytes);
			Assert.Equal(golden, bytes);
		}

		[Fact]
		public Task CreationScriptBurst_IsByteStable() =>
			AssertBurstStable("CreationScriptBurst", GameServerListener.SendRetailCreateEditorCreationScriptBurst);

		[Fact]
		public Task InitialBurst_IsByteStable() =>
			AssertBurstStable("InitialBurst", GameServerListener.SendRetailCreateEditorInitialBurst);
	}
}
