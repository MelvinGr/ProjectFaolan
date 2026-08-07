using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Extensions;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// How retail tells an OBSERVER about buffs on an entity streaming into view (#677).
	//
	// The ticket's blocker was *"decide the trigger: the spawn body itself, or a MagicBuffIIR fan-out right after
	// the spawn. Needs a capture check first - the corpus should show whether retail attaches buffs to the spawn or
	// sends them separately, and this must not be guessed."*
	//
	// It does not need a new check: the answer was already decoded and round-tripping. The spawn's post-IIR carries
	// a count-framed `BuffRecords` list, and it is populated on real spawns. So retail ATTACHES them, and there is
	// no fan-out to design.
	//
	// ⚠️ And each record carries `TimerRemaining`, which settles the ticket's other half. It reasoned that an
	// observer arriving mid-buff has no event to count down from, so *"an absolute `@46` expiry on the server's
	// uptime clock earns its place"* - and that the observer's client would therefore need `ServerTime_c` live from
	// a `DispatchPong`. None of that is needed: retail states the remaining time in the spawn record itself.
	public class SpawnBuffRecordTests
	{
		[Fact]
		public void RetailAttachesBuffsToTheSPAWN_NotAFanOutAfterIt()
		{
			var (spawns, withBuffs, buffs) = Scan();

			// Measured: 17,717 spawn bodies parse whole, 1,064 of them carry buffs, 1,471 records in all.
			Assert.True(spawns > 10_000, $"only {spawns} spawn bodies parsed - the gate would be vacuous");
			Assert.True(withBuffs > 0,
				"no captured spawn carries a buff record, which would mean retail does NOT attach them and #677's fan-out question is live again");
			Assert.True(buffs.Count >= withBuffs, "every buff-carrying spawn contributes at least one record");
		}

		// The field the observer needs. A record whose remaining time is always 0 would carry no countdown and the
		// ticket's absolute-stamp design would be back on the table, so this is the assertion that retires it.
		[Fact]
		public void ABuffRecordCarriesItsOwnRemainingTime()
		{
			var (_, _, buffs) = Scan();

			Assert.NotEmpty(buffs);
			Assert.Contains(buffs, b => b.TimerRemaining > 0);
		}

		// The remaining time is a REMAINDER, not a restatement of the duration: if the two were always equal the
		// field would be the total under another name and an observer would still learn nothing about elapsed time.
		[Fact]
		public void RemainingIsNotJustTheTotalAgain()
		{
			var (_, _, buffs) = Scan();
			var timed = buffs.Where(b => b.TimerTotal > 0).ToList();

			Assert.NotEmpty(timed);
			Assert.Contains(timed, b => b.TimerRemaining != b.TimerTotal);
			Assert.All(timed, b => Assert.True(b.TimerRemaining <= b.TimerTotal,
				$"a buff has {b.TimerRemaining} remaining of {b.TimerTotal} - a remainder cannot exceed its total"));
		}

		// The record names WHO applied it, which is what lets an observer attribute the icon rather than just draw
		// it. Without this the fan-out design would have had to carry the caster separately.
		[Fact]
		public void ABuffRecordNamesItsSourceAndTarget()
		{
			var (_, _, buffs) = Scan();

			Assert.Contains(buffs, b => b.SourceHandle != 0);
			Assert.Contains(buffs, b => b.TargetHandle != 0);
		}

		private static (int Spawns, int WithBuffs, List<ScaffoldBuff> Buffs) Scan()
		{
			int spawns = 0, withBuffs = 0;
			var buffs = new List<ScaffoldBuff>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				SpawnPostIir post;
				try
				{
					post = SimpleCharFullUpdateCodec.Parse(body.Length > 4 && body[0] == 0 ? body.AsSpan(4) : body.AsSpan()).Post;
				}
				catch (CodecParseException)
				{
					continue; // a body this codec cannot take whole is not evidence either way
				}

				spawns++;
				if (post.BuffRecords.Count == 0)
					continue;

				withBuffs++;
				buffs.AddRange(post.BuffRecords);
			}

			return (spawns, withBuffs, buffs);
		}
	}
}
