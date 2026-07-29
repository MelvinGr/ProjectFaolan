using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.CreateFlow;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #433: the create flow's enter-world handoff must NOT depend on the client putting something on its bar.
	//
	// It used to be sent in reply to the client's first 0x206E AddShortcut, which is the client auto-barring a grant -
	// so a character whose starting kit came back empty barred nothing, sent no 0x206E, and was stranded on a black
	// screen forever (a level-1 Assassin, 2026-07-29: every ClassAbilities row for the class carries the level of the
	// captured character it was harvested from, so a level-1 lookup matched none). Retail's trigger is TIME from
	// CharCreationFinished - 8.90-9.52s across all 13 create captures that reach a redirect, while the last c2s before
	// the redirect is AddShortcut in 11, a StatIIR PassBlob in one and a bare Ping in another.
	//
	// So these pin the property that matters: the handoff happens on its own, exactly once, with the client silent.
	public class EnterWorldHandoffTriggerTests
	{
		[Fact]
		public async Task TheHandoffFiresWithoutTheClientBarringAnything()
		{
			var handoffs = 0;
			var service = Service(() => Interlocked.Increment(ref handoffs));
			var client = new RecordingNetworkClient(0x0802E5D4);

			service.ScheduleEnterWorldHandoff(client, TimeSpan.Zero);
			await WaitFor(() => Volatile.Read(ref handoffs) > 0);

			// No AddShortcut was ever handled - an empty starting kit can no longer strand creation.
			Assert.Equal(1, Volatile.Read(ref handoffs));
			Assert.False(client.Session.AwaitingEnterWorld); // latch cleared by the send
		}

		[Fact]
		public async Task TheHandoffIsSingleShot()
		{
			var handoffs = 0;
			var service = Service(() => Interlocked.Increment(ref handoffs));
			var client = new RecordingNetworkClient(0x0802E5D4);

			// Two arms racing on one session (an AddShortcut path left armed, a retry, a re-entered flow). A second
			// 0x2002 would re-redirect a client that is already reconnecting, so exactly one must win.
			service.ScheduleEnterWorldHandoff(client, TimeSpan.Zero);
			service.ScheduleEnterWorldHandoff(client, TimeSpan.Zero);
			await WaitFor(() => Volatile.Read(ref handoffs) > 0);
			await Task.Delay(150, TestContext.Current.CancellationToken); // give a second send a chance to appear before asserting it did not

			Assert.Equal(1, Volatile.Read(ref handoffs));
		}

		[Fact]
		public async Task AFailedHandoffClearsTheLatchRatherThanRetryingForever()
		{
			// The send throwing must not leave the session armed-but-silent; it is logged (see the handler) and the
			// flow does not spin. This is the failure whose symptom is indistinguishable from the bug being fixed.
			var service = Service(() => throw new InvalidOperationException("connection gone"));
			var client = new RecordingNetworkClient(0x0802E5D4);

			service.ScheduleEnterWorldHandoff(client, TimeSpan.Zero);
			await WaitFor(() => !client.Session.AwaitingEnterWorld);

			Assert.False(client.Session.AwaitingEnterWorld);
		}

		private static CreateFlowService Service(Action onHandoff)
		{
			return new CreateFlowService(NullLogger.Instance, null!, null!, null!, _ =>
			{
				onHandoff();
				return Task.CompletedTask;
			});
		}

		// The handoff runs on a background task, so poll rather than sleeping a fixed amount.
		private static async Task WaitFor(Func<bool> condition)
		{
			for (var i = 0; i < 200 && !condition(); i++)
				await Task.Delay(10, TestContext.Current.CancellationToken);

			Assert.True(condition(), "the handoff did not happen within 2s");
		}
	}
}
