using System.Threading.Tasks;
using Faolan.GameServer.CreateFlow;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #197: the create-editor post-morph trio is now fired by the deterministic batch-drained trigger
	// (FlushPostMorphBurstOnBatchDrained, called from GameServerListener.ReceivedBatchComplete when the recv that
	// carried Finish + the SetMorphValue burst drains) instead of the ~350ms debounce. This locks the trigger's
	// guard logic: it sends the trio exactly once, only after the customization has been committed (Finish/a morph
	// bumped MorphActivitySequence), and never twice (so the batch-drain trigger + the fallback timer can't double).
	public class PostMorphBurstTriggerTests
	{
		private const uint Instance = 0x0802E5D4;

		[Fact]
		public async Task FlushOnBatchDrained_SendsTheTrioExactlyOnce_OnlyAfterArmed()
		{
			var service = new CreateFlowService(NullLogger.Instance, null!, null!, null!);
			var client = new RecordingNetworkClient(Instance);

			// Not armed yet (no Finish / no morph seen) -> a drained batch must NOT send the trio.
			await CreateFlowService.FlushPostMorphBurstOnBatchDrained(client);
			Assert.Empty(client.Sent);
			Assert.False(client.Session.PostMorphBurstSent);

			// Finish (or a morph) has been processed this batch -> the drain fires the trio (3 packets) once.
			client.Session.MorphActivitySequence = 1;
			await CreateFlowService.FlushPostMorphBurstOnBatchDrained(client);
			Assert.Equal(3, client.Sent.Count); // StatIIR + StartScriptGroup + StatUpdate
			Assert.True(client.Session.PostMorphBurstSent);

			// Idempotent: a later drained batch (or the fallback timer) sends nothing more.
			await CreateFlowService.FlushPostMorphBurstOnBatchDrained(client);
			Assert.Equal(3, client.Sent.Count);
		}
	}
}
