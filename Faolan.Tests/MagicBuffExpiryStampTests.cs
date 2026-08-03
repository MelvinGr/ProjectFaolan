using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Session;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The MagicBuffIIR apply payload's @46 u64 is an ABSOLUTE EXPIRY on the server's UPTIME clock in milliseconds, and
	// the only way the client ever learns that clock is the DispatchPong reply we send to its LatencyCheck poll (#480).
	// So the two senders are one subsystem, and this is the gate that keeps them one:
	//
	//   client:  ServerTime_c   = pongMicroseconds / 1000                      (FUN_007a0ea0, the signal slot)
	//   client:  remainingCs    = (stamp - ServerTime_c) / 10                  (FUN_007a1580, MagicBuffIIR_c::Activate)
	//            ...and Duration is overwritten with it when 0 < remainingCs <= Duration.
	//
	// Nothing on the wire looks wrong if these drift apart - the client just silently discards the stamp and counts
	// down the Duration field, which is exactly the state this field sat in for as long as it was written as an
	// "effect nonce". So the assertions here run the CLIENT's arithmetic over what our two real senders produce.
	//
	// The identity is corpus-verified, not only read out of the client: across the 94 captures carrying both a pong and
	// a 54-byte apply, `stamp - (uptimeMs + duration_cs*10)` has a median of +6 ms and lands within 250 ms for 2551 of
	// 2929 applies (98.6% within 5 s; the residue is captures spanning a reconnect, where the nearest pong belongs to a
	// different server instance). The mount capture's own numbers are pinned in NpcSpellCastCodecTests.
	public class MagicBuffExpiryStampTests
	{
		private const uint Instance = 0x0802E5D4;

		// The two units must come off ONE origin, by the same division the client does. A second Stopwatch root
		// anywhere would put the pong and the buff stamp on clocks that differ by however long apart they started.
		[Fact]
		public void MillisecondsIsTheMicrosecondCounterDividedByAThousand()
		{
			var micros = ServerUptimeClock.Microseconds;
			var millis = ServerUptimeClock.Milliseconds;

			// Read across a boundary they can differ by the elapsed time, never by an origin.
			Assert.InRange((long)millis - (long)(micros / 1000), 0, 50);
		}

		[Fact]
		public void ExpiryFromNowIsNowPlusTheDuration()
		{
			const ulong durationMs = 7_000;
			var before = ServerUptimeClock.Milliseconds;
			var stamp = ServerUptimeClock.ExpiryFromNow(durationMs);

			Assert.InRange(stamp - durationMs, before, before + 50);
		}

		// The whole chain, end to end, through the REAL pong handler and the REAL buff factory: take the clock the
		// client would be holding, then run Activate's formula over the payload we would send it.
		[Fact]
		public async Task ClientsOwnArithmeticRecoversTheDuration_FromTheRealPongAndTheRealBuffPayload()
		{
			const uint durationCs = 700; // Rend: 7s, one of the in-client-calibrated durations (#274)

			// 1. What the client stores, taken from the bytes KeepAliveService actually puts on the wire.
			var client = new RecordingNetworkClient(Instance);
			var latencyCheck = Rpc.Parse(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, Instance),
				(ushort)GameCharInterfaceMethods.DispatchPong, (byte[])[0x00, 0x00, 0x00, 0x07]).Serialize());
			await KeepAliveService.HandleLatencyCheck(client, latencyCheck, latencyCheck.BodyReader());

			var pong = Rpc.Parse(client.Sent[^1]).BodyReader();
			pong.ReadUInt32(); // the echoed counter
			var serverTimeMs = (((ulong)pong.ReadUInt32() << 32) | pong.ReadUInt32()) / 1000;

			// 2. What we send it, through the factory every live cast uses.
			var payload = MagicBuffDatas.ApplyNow(0x31EB15, durationCs, casterLevel: 80);
			var stamp = MagicBuffDataCodec.Parse(MagicBuffDataCodec.Build(payload)).ExpiryStamp;

			// 3. MagicBuffIIR_c::Activate. Signed, because the client's __alldiv is - a stamp behind the clock has to
			// come out NEGATIVE and be rejected, not wrap to a huge positive remaining time.
			var remainingCs = ((long)stamp - (long)serverTimeMs) / 10;

			Assert.True(remainingCs > 0, $"the client would reject a non-positive remaining time ({remainingCs}cs)");
			// Both readings are of the same monotonic clock a moment apart, so the recovered remaining time is the
			// duration minus that moment. A whole second of slack is far more than the gap and far less than the 7s
			// duration, so this fails loudly if the two senders are ever rooted differently.
			Assert.InRange(remainingCs, durationCs - 100, durationCs);
		}

		// A payload built for a buff that is already partly elapsed must produce a SHORTER remaining time than its
		// Duration field - that is the branch Activate takes to resume a buff, and the only branch that changes what
		// the client renders. Built the way the restore path builds it: Duration = the remaining time.
		[Fact]
		public void APartlyElapsedBuffResumesShortOfItsFullDuration()
		{
			const uint fullDurationCs = 8_640_000; // the 24h mount ride buff
			const uint remainingCs = 8_509_794; // what retail's captured world-entry restore carries (21.7 min in)

			var resumed = MagicBuffDatas.ApplyNow(0x31EB15, remainingCs, 80);
			var serverTimeMs = ServerUptimeClock.Milliseconds;
			var recovered = ((long)resumed.ExpiryStamp - (long)serverTimeMs) / 10;

			Assert.InRange(recovered, remainingCs - 100, remainingCs);
			Assert.True(recovered < fullDurationCs, "a resumed buff must come back short of the spell's full duration");
			Assert.Equal(remainingCs, resumed.Duration);
		}

		// The stamp is ABSOLUTE, so it is a 64-bit field with a real high half once the server has been up past
		// 2^32 ms (49.7 days). The old "nonce" only ever occupied the low word, which is what made it fail Activate's
		// `0 < x <= Duration` test in every session: the whole u64 read as a value far in the past.
		[Fact]
		public void TheStampIsAWholeU64_NotJustItsLowWord()
		{
			var built = MagicBuffDataCodec.Build(MagicBuffDatas.Apply(0x31EB15, 8_640_000, 0x0000_0004_1560_F9BBuL, 80));

			Assert.Equal(MagicBuffDataCodec.Length, built.Length);
			Assert.Equal(0x0000_0004_1560_F9BBuL, MagicBuffDataCodec.Parse(built).ExpiryStamp);
		}
	}
}
