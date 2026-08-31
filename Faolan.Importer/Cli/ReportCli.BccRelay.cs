using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report bcc-relay`: for each `BehaviorId`, does retail send a client a record for SOMEBODY ELSE'S actor
	// handle? (#912)
	//
	// The question behind it is a design one. Our BCC relay is an explicit ALLOWLIST that has been extended once per
	// discovery - #48 the combat behaviours, #428 the mounted jump, #403 `Equip` - each time with the same symptom
	// (an action visible only to the actor) and each time by adding one more id. Before adding a sixth entry it is
	// worth asking what retail relays, and the corpus answers it directly.
	//
	// ⭐ The discriminator does NOT need a multi-client capture, which is what the ticket assumed and what the corpus
	// does not have. It needs only the recipient's OWN handle, and every capture states that: the client sends its
	// own actions, so the modal `actor` over the c2s records IS the player. An s2c record whose actor is that handle
	// is the echo-ACK coming back; one whose actor is anything else is retail rendering another entity's behaviour on
	// this client's screen - which is a relay whether the other entity is a player or an NPC. The client cannot tell
	// the difference either: a handle is a handle.
	//
	// ⚠️ So "relayed" here means "the client is asked to render this behaviour for a handle that is not its own".
	// It does NOT establish that retail relays a PLAYER's instance of that behaviour to another player - the corpus
	// is single-client, so no capture can. What it does establish is the client-side half: a command the client
	// already renders for a foreign handle cannot be one it refuses to render for a foreign PLAYER handle, because
	// nothing downstream of the record knows which it is.
	public static partial class ReportCli
	{
		private static void BccRelay(IReadOnlyList<NamedCapture> captures)
		{
			var known = Enum.GetValues<BehaviorId>().ToDictionary(b => (uint)b, b => b.ToString());
			var stats = new SortedDictionary<uint, Relay>();
			var playerDid = new Dictionary<uint, HashSet<string>>();
			var foreignActorCaptures = new HashSet<string>();

			foreach (var capture in captures)
			{
				var records = new List<BehaviorControlCenterIIR>();
				var directions = new List<bool>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;

					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
						continue;

					records.Add(bcc);
					directions.Add(p.IsDownload);
				}

				// The player: the actor the CLIENT names most often. A capture with no c2s BCC at all cannot say who
				// its player is, so it contributes nothing rather than guessing.
				var self = records.Where((_, i) => !directions[i])
					.GroupBy(r => r.Actor)
					.OrderByDescending(g => g.Count())
					.Select(g => (uint?)g.Key)
					.FirstOrDefault();
				if (self == null)
					continue;

				for (var i = 0; i < records.Count; i++)
				{
					if (!directions[i])
					{
						if (!playerDid.TryGetValue(records[i].BehaviorId, out var did))
							playerDid[records[i].BehaviorId] = did = [];

						did.Add(capture.Name);
						continue;
					}

					if (records[i].Actor != self)
						foreignActorCaptures.Add(capture.Name);

					if (!stats.TryGetValue(records[i].BehaviorId, out var relay))
						stats[records[i].BehaviorId] = relay = new Relay();

					if (records[i].Actor == self)
						relay.ToSelf++;
					else
					{
						relay.ToOthers++;
						relay.OtherActors.Add(records[i].Actor);
						relay.Captures.Add(capture.Name);
					}
				}
			}

			Console.WriteLine("s2c BehaviorControlCenterIIR records, split by whether the ACTOR is the recipient.");
			Console.WriteLine("A record for a foreign actor is retail asking this client to render someone else's behaviour.");
			Console.WriteLine();
			Console.WriteLine("  id     name                            echo-ack   RELAYED   distinct actors   captures");

			foreach (var (id, r) in stats)
			{
				var name = known.TryGetValue(id, out var n) ? n : "*** not in the classmap ***";
				Console.WriteLine($"  0x{id:X2}   {name,-30}  {r.ToSelf,8}  {r.ToOthers,8}   {r.OtherActors.Count,15}   {r.Captures.Count}");
			}

			// ⚠️ A MISS needs a control. An id with zero foreign-actor records may mean "retail does not relay it" or
			// merely "nobody else ever did it near the recorded player" - and the second is the default expectation
			// for an action only a PLAYER performs, in a corpus of single-client sessions. So for every id the player
			// itself performed, print how many of those same captures carried foreign BCC actors at all: that is the
			// opportunity count, and a zero-relay id with a healthy opportunity count is the only one saying anything.
			Console.WriteLine();
			Console.WriteLine("Control for the zero-relay ids - captures where the PLAYER did it, and how many of those");
			Console.WriteLine("had any foreign BCC actor at all (i.e. somebody was there to relay from):");
			Console.WriteLine();
			Console.WriteLine("  id     name                            player-did-it captures   ...with a foreign actor present");

			foreach (var (id, r) in stats.Where(s => s.Value.ToOthers == 0))
			{
				var name = known.TryGetValue(id, out var n) ? n : $"0x{id:X2}";
				var did = playerDid.GetValueOrDefault(id) ?? [];
				Console.WriteLine($"  0x{id:X2}   {name,-30}  {did.Count,20}   {did.Count(c => foreignActorCaptures.Contains(c)),29}");
			}

			Console.WriteLine();
			var relayed = stats.Where(s => s.Value.ToOthers > 0).Select(s => s.Key).ToList();
			var never = stats.Where(s => s.Value.ToOthers == 0).Select(s => s.Key).ToList();
			Console.WriteLine($"RELAYED for a foreign actor ({relayed.Count}): {string.Join(", ", relayed.Select(Name))}");
			Console.WriteLine($"only ever echoed to the actor ({never.Count}): {string.Join(", ", never.Select(Name))}");

			string Name(uint id)
			{
				return known.TryGetValue(id, out var n) ? $"{n} (0x{id:X2})" : $"0x{id:X2}";
			}
		}

		private sealed class Relay
		{
			public HashSet<string> Captures { get; } = [];
			public HashSet<uint> OtherActors { get; } = [];
			public int ToOthers;
			public int ToSelf;
		}
	}
}
