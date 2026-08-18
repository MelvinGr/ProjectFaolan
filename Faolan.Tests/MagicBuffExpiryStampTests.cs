using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
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
			var before = ServerUptimeClock.Microseconds;
			var millis = ServerUptimeClock.Milliseconds;
			var after = ServerUptimeClock.Microseconds;

			// Read across a boundary they can differ by the elapsed time, never by an origin - so bracket the middle
			// read between the two outer ones rather than allowing a fixed window that machine load can blow through.
			Assert.InRange(millis, before / 1000, after / 1000);
		}

		[Fact]
		public void ExpiryFromNowIsNowPlusTheDuration()
		{
			const ulong durationMs = 7_000;
			var before = ServerUptimeClock.Milliseconds;
			var stamp = ServerUptimeClock.ExpiryFromNow(durationMs);
			var after = ServerUptimeClock.Milliseconds;

			Assert.InRange(stamp - durationMs, before, after);
		}

		// The whole chain, end to end, through the REAL pong handler and the REAL buff factory: take the clock the
		// client would be holding, then run Activate's formula over the payload we would send it.
		[Fact]
		public async Task ClientsOwnArithmeticRecoversTheDuration_FromTheRealPongAndTheRealBuffPayload()
		{
			const uint durationCs = 700; // Rend: 7s, one of the in-client-calibrated durations (#274)

			// 1. What the client stores, taken from the bytes KeepAliveService actually puts on the wire.
			var client = new RecordingNetworkClient(Instance);
			var latencyCheck = Rpc.Parse(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceType, Instance),
				(ushort)GameCharInterfaceMethods.DispatchPong, (byte[])[0x00, 0x00, 0x00, 0x07]).Serialize());
			await KeepAliveService.HandleLatencyCheck(client, latencyCheck, latencyCheck.BodyReader());

			var pong = Rpc.Parse(client.Sent[^1]).BodyReader();
			pong.ReadUInt32(); // the echoed counter
			var serverTimeMs = (((ulong)pong.ReadUInt32() << 32) | pong.ReadUInt32()) / 1000;

			// 2. What we send it, through the factory every live cast uses.
			var payload = MagicBuffDatas.ApplyNow(0x31EB15, durationCs, casterLevel: 80, casterIsPlayer: true);
			var stamp = MagicBuffDataCodec.Parse(MagicBuffDataCodec.Build(payload)).ExpiryStamp;
			// Bracket the second clock read from the outside, so the gap between the two senders' reads is MEASURED
			// rather than assumed short: a fixed window would depend on machine load, and did fail under a loaded
			// full-suite run (#624).
			var gapCs = (long)(ServerUptimeClock.Milliseconds - serverTimeMs) / 10;

			// 3. MagicBuffIIR_c::Activate. Signed, because the client's __alldiv is - a stamp behind the clock has to
			// come out NEGATIVE and be rejected, not wrap to a huge positive remaining time.
			var remainingCs = ((long)stamp - (long)serverTimeMs) / 10;

			Assert.True(remainingCs > 0, $"the client would reject a non-positive remaining time ({remainingCs}cs)");
			// The pong's read comes FIRST, so the recovered remaining time is the duration plus however long the
			// factory's read trailed it - never less, and never more than the bracket. Rooted differently, the two
			// would be apart by however long the two origins started apart, which is unbounded in either direction.
			Assert.InRange(remainingCs, durationCs, durationCs + gapCs);
		}

		// A payload built for a buff that is already partly elapsed must produce a SHORTER remaining time than its
		// Duration field - that is the branch Activate takes to resume a buff, and the only branch that changes what
		// the client renders. Built the way the restore path builds it: Duration = the remaining time.
		[Fact]
		public void APartlyElapsedBuffResumesShortOfItsFullDuration()
		{
			const uint fullDurationCs = 8_640_000; // the 24h mount ride buff
			const uint remainingCs = 8_509_794; // what retail's captured world-entry restore carries (21.7 min in)

			var before = ServerUptimeClock.Milliseconds;
			var resumed = MagicBuffDatas.ApplyNow(0x31EB15, remainingCs, 80, casterIsPlayer: true);
			var serverTimeMs = ServerUptimeClock.Milliseconds;
			var recovered = ((long)resumed.ExpiryStamp - (long)serverTimeMs) / 10;

			// Here the factory's read comes FIRST, so the recovered time falls SHORT by the gap - bracketed, not a
			// fixed window (#624).
			//
			// ⚠️ The bound ROUNDS UP, and that is the whole of #860. `recovered` is `(10*R - d) / 10` for a gap `d`
			// in milliseconds, and C# truncation of a subtraction makes that `R - ceil(d/10)`; a bound written as
			// `R - g/10` is `R - floor(g/10)`. So the two straddle a centisecond boundary in OPPOSITE directions and
			// the bound comes out one centisecond too tight whenever the elapsed gap is not a multiple of 10 ms.
			// Normally all three clock reads land inside one millisecond and the gap is 0, which is why it passed;
			// under a loaded full-suite run it does not, and the test failed for a reason that had nothing to do with
			// what it measures.
			//
			// ⭐ Its neighbour above looks symmetric and is NOT affected, which is the tell: that one ADDS the gap
			// (`stamp - serverTime` with the pong read first), so both sides floor and the inequality survives. Only
			// the subtracting direction flips the rounding.
			var gapCs = (long)(serverTimeMs - before + 9) / 10; // ceil, to match `recovered`'s own truncation
			Assert.InRange(recovered, remainingCs - gapCs, remainingCs);
			Assert.True(recovered < fullDurationCs, "a resumed buff must come back short of the spell's full duration");
			Assert.Equal(remainingCs, resumed.Duration);
		}

		// The stamp is ABSOLUTE, so it is a 64-bit field with a real high half once the server has been up past
		// 2^32 ms (49.7 days). The old "nonce" only ever occupied the low word, which is what made it fail Activate's
		// `0 < x <= Duration` test in every session: the whole u64 read as a value far in the past.
		[Fact]
		public void TheStampIsAWholeU64_NotJustItsLowWord()
		{
			var built = MagicBuffDataCodec.Build(MagicBuffDatas.Apply(0x31EB15, 8_640_000, 0x0000_0004_1560_F9BBuL, 80, casterIsPlayer: true));

			Assert.Equal(MagicBuffDataCodec.Length, built.Length);
			Assert.Equal(0x0000_0004_1560_F9BBuL, MagicBuffDataCodec.Parse(built).ExpiryStamp);
		}
	}
}
