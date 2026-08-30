using System;
using System.Threading;
using System.Threading.Tasks;
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

			// Arms racing on one session (an AddShortcut path left armed, a retry, a re-entered flow). A second 0x2002
			// would re-redirect a client that is already reconnecting, so exactly one must win.
			//
			// Deliberately MANY arms, not two. With two, the losing interleaving is rare enough that this passed on its
			// own and failed only inside the full suite under CPU load - which read as an undiagnosable flake for a
			// while. Widening the race makes the bug deterministic instead of load-dependent.
			//
			// ⚠️ It took TWO fixes, and this test kept failing intermittently between them. The first made the CLAIM an
			// Interlocked compare-and-swap, which stops two tasks sharing one arm. But arming still wrote the latch
			// unconditionally, so the surviving interleaving was arm -> claim -> send -> arm again -> claim -> send
			// again - and in a tight arming loop that happens whenever a task completes before the loop finishes, i.e.
			// exactly under load. #706 made ARMING a claim as well, onto a terminal `Sent` state.
			for (var i = 0; i < 64; i++)
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
			//
			// NB since #706 the connection ends TERMINAL rather than back at Idle - the claim moves it to `Sent`
			// before the send is attempted, so a throw leaves it Sent. That loses nothing: there is exactly one arming
			// site (creation-finished) and it has already run, so there was never a retry to preserve.
			var service = Service(() => throw new InvalidOperationException("connection gone"));
			var client = new RecordingNetworkClient(0x0802E5D4);

			service.ScheduleEnterWorldHandoff(client, TimeSpan.Zero);
			await WaitFor(() => !client.Session.AwaitingEnterWorld);

			Assert.False(client.Session.AwaitingEnterWorld);
		}

		// The primitive the single-shot property rests on: claim once, and the claim clears the latch.
		//
		// ⚠️ Be honest about what this can and cannot do. It does NOT reliably catch the non-atomic latch - the racy
		// "if (!AwaitingEnterWorld) return; AwaitingEnterWorld = false;" was measured to pass this too, because the
		// window between the read and the write is a couple of instructions and threads rarely land inside it. So the
		// argument for TryClaimEnterWorldHandoff being an Interlocked compare-and-swap is that it is atomic BY
		// CONSTRUCTION, not that a test can demonstrate the alternative failing on demand.
		//
		// What this gate is actually for is the contract - exactly one winner, latch cleared in the same step - so a
		// future "simplification" back to a plain bool property has something to fail against on the days it does
		// lose the race. The real evidence that the race is live is the suite: the integration test above failed at
		// 1579/1580 under full-suite CPU load and passed in isolation, every time, until the CAS landed.
		[Fact]
		public void ClaimingTheHandoff_HasExactlyOneWinner_UnderContention()
		{
			for (var attempt = 0; attempt < 50; attempt++)
			{
				var session = new RecordingNetworkClient(0x0802E5D4).Session;
				Assert.True(session.TryArmEnterWorldHandoff());

				var winners = 0;
				var threads = new Thread[Math.Max(4, Environment.ProcessorCount)];
				using var gate = new ManualResetEventSlim(false);

				for (var i = 0; i < threads.Length; i++)
				{
					threads[i] = new Thread(() =>
					{
						gate.Wait();
						if (session.TryClaimEnterWorldHandoff())
							Interlocked.Increment(ref winners);
					});
					threads[i].Start();
				}

				gate.Set(); // release them all at once, so the claims genuinely overlap
				foreach (var t in threads)
					t.Join();

				Assert.Equal(1, Volatile.Read(ref winners));
				Assert.False(session.AwaitingEnterWorld); // and the claim cleared the latch
			}
		}

		// An unarmed session has no handoff to claim, so nobody wins - otherwise a stray retry after the handoff
		// already completed would send a second 0x2002.
		[Fact]
		public void ClaimingAnUnarmedHandoff_Fails()
		{
			var session = new RecordingNetworkClient(0x0802E5D4).Session;

			Assert.False(session.AwaitingEnterWorld);
			Assert.False(session.TryClaimEnterWorldHandoff());
		}

		// `Sent` is TERMINAL for a connection (#706), and this is the half the first fix missed: re-arming after a
		// handoff has gone out must be REFUSED, not silently allowed to open a second one.
		//
		// It is safe to be terminal because a session is per CONNECTION and the world entry uses a NEW one - the client
		// reconnects in answer to the handoff. So a second arm means the creation-finished path ran twice on one
		// connection (a retried or duplicated message), which is exactly the case that must not re-redirect a client
		// that is already on its way.
		[Fact]
		public void OnceSent_TheHandoffCannotBeReArmed()
		{
			var session = new RecordingNetworkClient(0x0802E5D4).Session;

			Assert.True(session.TryArmEnterWorldHandoff());
			Assert.False(session.TryArmEnterWorldHandoff()); // already armed
			Assert.True(session.TryClaimEnterWorldHandoff()); // -> Sent

			Assert.False(session.AwaitingEnterWorld);
			Assert.False(session.TryArmEnterWorldHandoff()); // terminal: no second handoff on this connection
			Assert.False(session.TryClaimEnterWorldHandoff());

			// ...and the world-entry path's reset is the one way back, for the session that legitimately needs it.
			session.ResetEnterWorldHandoff();
			Assert.True(session.TryArmEnterWorldHandoff());
		}

		private static CreateFlowService Service(Action onHandoff)
		{
			return new CreateFlowService(NullLogger.Instance, null!, null!, _ =>
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
