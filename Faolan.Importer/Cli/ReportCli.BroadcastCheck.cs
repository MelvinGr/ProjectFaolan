using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report broadcast-check`: which server records does retail send to EVERY player who can see the subject, and
	// which only to the one client concerned? (#272.)
	//
	// The question has one honest oracle in the whole corpus - `login_2-clients_group-trade-friend`, the only capture
	// with TWO game connections. With one client on the wire a broadcast and a private reply look identical; with two,
	// a record that reaches both streams IS a broadcast and one that reaches only one is not. Everything this report
	// says comes from that single distinction.
	//
	// ⭐ What it is FOR. A record addressed to a SUBJECT and describing world-visible state has to go to everyone who
	// can see that subject, or the world desynchronises per player: an NPC killed by someone else stays standing for
	// you, an animation plays for one witness, a corpse never freezes. That class of bug is invisible in single-client
	// testing and invisible in a single-client capture, which is exactly why it needs a standing measurement rather
	// than a convention someone remembers.
	//
	// ⚠️ READ THE MIXED COLUMN, do not automate off the type alone. An IIR type is not one message: a
	// `BehaviorControlCenterIIR` carries an entity's animation (broadcast) AND the ack of a client's own request
	// (not), so its 11/11 here says only that this capture's eleven were all the first kind - the two players never
	// fought. The type-level verdict is a starting point; the record's ROLE decides.
	public static partial class ReportCli
	{
		// The one capture with two game connections. Named rather than searched for: no other capture can answer this,
		// and silently measuring a single-client one would report "nothing is broadcast" with total confidence.
		private const string TwoClientCapture = "login_2-clients_group-trade-friend";

		private static int BroadcastCheck(IReadOnlyList<NamedCapture> captures)
		{
			var capture = captures.FirstOrDefault(c => c.Name.Contains(TwoClientCapture, StringComparison.OrdinalIgnoreCase));
			if (capture == null)
			{
				Console.Error.WriteLine($"broadcast-check needs {TwoClientCapture} - it is the only capture with two game connections, and no other can answer this.");
				return 1;
			}

			// The GAME connections are the TCP STREAMS that carry IIRs at all; a client also holds login/chat/agent
			// connections which carry none, so counting every stream would split one client into several. It cannot be
			// `Port` - that is the SERVER port and is identical for every client on a tier.
			var gameStreams = capture.Packets.Where(p => p is { IsDownload: true, IsIirCarrier: true }).Select(p => p.Stream).Distinct().OrderBy(s => s).ToArray();
			Console.WriteLine($"broadcast-check over {capture.Name}: {gameStreams.Length} game connection(s) - streams {string.Join(", ", gameStreams)}.");
			if (gameStreams.Length < 2)
			{
				Console.Error.WriteLine("  ⚠️ fewer than two game connections - a broadcast is indistinguishable from a private reply here.");
				return 1;
			}

			// (record type, subject handle) -> the connections it reached. The subject is the handle the record is
			// ADDRESSED to, which is what "everyone who can see it" is about.
			var reached = new Dictionary<(uint Type, uint Subject), HashSet<int>>();
			foreach (var p in capture.Packets)
			{
				if (p is not { IsDownload: true, IsIirCarrier: true } || p.Record.Length < 12)
					continue;

				// The subject is the handle the record is ADDRESSED to. `Record` starts at the type word, so the
				// layout is [u32 type][u32 entityClass][u32 handle] - reading the CLASS instead collapses every
				// record onto one subject, because it is the constant 0xC350 on all of them.
				var key = (p.SubOpcode, p.Record.ReadU32Be(8));
				if (!reached.TryGetValue(key, out var streams))
					reached[key] = streams = [];
				streams.Add(p.Stream);
			}

			var byType = reached
				.GroupBy(kv => kv.Key.Type)
				.Select(g => (Type: g.Key, Both: g.Count(kv => kv.Value.Count > 1), Total: g.Count()))
				.OrderByDescending(t => t.Total)
				.ToList();

			Console.WriteLine();
			Console.WriteLine($"  {"record",-34} {"broadcast",9} {"subjects",9} {"verdict",-12}");
			foreach (var (type, both, total) in byType)
			{
				var verdict = both == total
					? "BROADCAST"
					: both == 0
						? "per-client"
						: "MIXED";
				Console.WriteLine($"  {NameOfIir(type),-34} {both,9} {total,9} {verdict,-12}");
			}

			Console.WriteLine();
			Console.WriteLine("  BROADCAST  every subject reached both clients - send these to all viewers of the subject.");
			Console.WriteLine("  per-client every subject reached exactly one - the player's OWN state or a reply to their request.");
			Console.WriteLine("  MIXED      both kinds share one type; the record's ROLE decides, not its type.");
			return 0;
		}

		private static string NameOfIir(uint type)
		{
			return Enum.IsDefined(typeof(IirTypes), type)
				? $"{(IirTypes)type} (0x{type:X8})"
				: $"0x{type:X8}";
		}
	}
}
