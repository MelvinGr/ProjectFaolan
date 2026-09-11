using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report quest-container`: what takes an item out of the quest container (`0x71`)?
	//
	// Two readings of one observation, which one capture cannot separate. In
	// `Hoxxling_mage-heraldofxotli_login_gameplay` the Acheronian Gate Key is added to the quest container when
	// Saddur's corpse is looted and removed four packets after the gate is used:
	//
	//   fr12196  c2s  BCC UseDynel on the gate     fr12205  s2c  QuestIIR 12369 -> completed
	//   fr12204  s2c  QuestGoal 12369/3492 1/1     fr12206  s2c  DispatchRemoveItem 0x71 slot 0
	//
	//   (1) the gate consumed the key - a property of that goal's trigger;
	//   (2) the quest cleared its container - a general rule for every quest holding `0x71` items.
	//
	// The two make different predictions about the REST of the corpus, which is what this measures. Under (2)
	// every completion that happens while quest items are held must be followed by their removal. A single
	// completion that leaves them in place refutes (2) and leaves (1). Under (1) removals cluster on the one goal
	// that uses a world object, and completions elsewhere leave the container alone.
	//
	// The window matters and is stated rather than tuned: a removal is attributed to a completion only if it
	// follows within `AttributionWindow` packets. Widening it until the answer comes out is exactly how a
	// correlation gets manufactured, so it is one constant, printed, and the raw per-capture timeline is printed
	// beside the verdict so the attribution can be checked by eye.
	public static partial class ReportCli
	{
		// Packets, not seconds: the corpus's own frame ordering is what the attribution reasons about (the removal is
		// one packet after the completion), and a packet index needs no clock. Generous enough to cover a completion
		// burst.
		private const int AttributionWindow = 40;

		// An RPC method body is `.Body`, not `.Record` - the `TryParse` extension beside it is for IIR carriers,
		// where a codec owns the record from its type word on. Handing an RPC codec `.Record` fails every parse, and
		// the control above is the only reason that showed up as "0 parsed" rather than as a confident zero answer.
		private static bool TryParseBody<T>(CapturedPacket packet, RecordParser<T> parse, out T value)
		{
			try
			{
				value = parse(packet.Body);
				return true;
			}
			catch (CodecParseException)
			{
				value = default!;
				return false;
			}
		}

		private static int QuestContainer(IReadOnlyList<NamedCapture> captures)
		{
			var addWire = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchAddItem);
			var removeWire = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchRemoveItem);

			// control, counted at ARRIVAL rather than after the parse. Without it "no packet carried this opcode"
			// and "every parse failed" and "every parse succeeded but the container was never 0x71" all print as the
			// same zero - and that zero is the answer this report exists to give, so it would be believed.
			int addArrivals = 0, addParsed = 0, addQuest = 0;
			int removeArrivals = 0, removeParsed = 0, removeQuest = 0;
			var completionArrivals = 0;

			var completionsWithHeld = 0;
			var completionsFollowedByRemoval = 0;
			var removalsWithNoCompletion = 0;
			var timelines = new List<string>();

			foreach (var c in captures)
			{
				// The quest container's live contents, by slot, as the capture walks. An add fills a slot and a
				// remove empties it; that is all this needs to answer "was anything held at completion".
				var held = new Dictionary<uint, uint>();
				var events = new List<(int At, string Kind, uint Key)>();

				for (var i = 0; i < c.Packets.Count; i++)
				{
					var p = c.Packets[i];
					if (!p.IsDownload)
						continue;

					// The wire opcode is LOSSY on the hashed classes, so the parse is what confirms the message -
					// a body that does not decode as an add/remove was some other method rendering to the same word.
					if (p.WireOpcode == addWire)
					{
						addArrivals++;
						if (TryParseBody(p, AddItemCodec.Parse, out var add))
						{
							addParsed++;
							if (add.Container == ContainerKind.Quest)
							{
								addQuest++;
								held[add.ItemPos] = add.ItemPos;
								events.Add((i, "add", add.ItemPos));
							}
						}
					}
					else if (p.WireOpcode == removeWire)
					{
						removeArrivals++;
						if (TryParseBody(p, RemoveItemCodec.Parse, out var rem))
						{
							removeParsed++;
							if (rem.Container == ContainerKind.Quest)
							{
								removeQuest++;
								held.Remove(rem.ItemPos);
								events.Add((i, "remove", rem.ItemPos));
							}
						}
					}
					else if (p is { IsIirCarrier: true, SubOpcode: QuestCodec.Opcode } && p.TryParse(QuestCodec.Parse, out var q) && q.State == QuestCodec.StateCompleted)
					{
						completionArrivals++;
						events.Add((i, "completed", q.QuestId));
					}
				}

				if (events.Count == 0)
					continue;

				// Replay the events, tracking what the container held at each completion. Held-ness is recomputed
				// here rather than reusing the dictionary above, which ends the capture holding only the leftovers.
				var live = new HashSet<uint>();
				var lines = new List<string>();
				foreach (var (at, kind, key) in events)
				{
					switch (kind)
					{
						case "add":
							live.Add(key);
							lines.Add($"    {at,7}  add     0x71 slot {key}");
							break;

						case "remove":
						{
							live.Remove(key);
							var completedJustBefore = events.Any(e => e.Kind == "completed" && e.At <= at && at - e.At <= AttributionWindow);
							if (!completedJustBefore)
								removalsWithNoCompletion++;

							lines.Add($"    {at,7}  remove  0x71 slot {key}{(completedJustBefore ? "   <- within the window of a completion" : "   <- NO completion nearby")}");
							break;
						}

						default:
						{
							if (live.Count == 0)
							{
								lines.Add($"    {at,7}  quest {key} completed  (container empty)");
								break;
							}

							completionsWithHeld++;
							var removedAfter = events.Any(e => e.Kind == "remove" && e.At >= at && e.At - at <= AttributionWindow);
							if (removedAfter)
								completionsFollowedByRemoval++;

							lines.Add($"    {at,7}  quest {key} completed  holding {live.Count} item(s)"
							          + (removedAfter ? "   -> removed within the window" : "   ⛔ -> NOT removed"));
							break;
						}
					}
				}

				if (lines.Any(l => l.Contains("0x71", StringComparison.Ordinal)))
					timelines.Add($"  {c.Name}\n{string.Join("\n", lines)}");
			}

			Console.WriteLine($"=== quest container (0x71) removals vs quest completions - #843 ===");
			Console.WriteLine($"    attribution window: {AttributionWindow} packets");
			Console.WriteLine();
			Console.WriteLine($"  control - DispatchAddItem:    {addArrivals,6} arrived, {addParsed,6} parsed, {addQuest,5} in the QUEST container");
			Console.WriteLine($"  control - DispatchRemoveItem: {removeArrivals,6} arrived, {removeParsed,6} parsed, {removeQuest,5} in the QUEST container");
			Console.WriteLine($"  control - quest completions:  {completionArrivals,6}");
			Console.WriteLine();
			foreach (var t in timelines)
				Console.WriteLine(t + "\n");

			Console.WriteLine($"  completions while holding quest item(s): {completionsWithHeld}");
			Console.WriteLine($"    of which followed by a removal:        {completionsFollowedByRemoval}");
			Console.WriteLine($"  removals with NO completion nearby:      {removalsWithNoCompletion}");
			Console.WriteLine();

			// The verdict, stated as what the evidence can and cannot carry. A single completion that keeps its
			// items refutes the general rule; no completion holding items at all means the corpus simply never
			// exercises the question, which is a different answer from either reading and must not read as one.
			if (completionsWithHeld == 0)
				Console.WriteLine("  ⚠️ NO capture completes a quest while the quest container holds anything, so the corpus\n"
				                  + "     cannot separate #843's two readings at all. This is an absence of evidence, not evidence\n"
				                  + "     for the gate reading - it needs a capture of a quest completing with 0x71 items held.");
			else if (completionsFollowedByRemoval == completionsWithHeld)
				Console.WriteLine($"  ⭐ EVERY one of the {completionsWithHeld} completion(s) holding quest items was followed by a removal,\n"
				                  + "     which is what reading (2) predicts - a completed quest clears its container as a general rule.");
			else
				Console.WriteLine($"  ⛔ {completionsWithHeld - completionsFollowedByRemoval} completion(s) held quest items and did NOT lose them, which REFUTES the general\n"
				                  + "     'a completed quest clears its container' reading. The removal belongs to the trigger, not\n"
				                  + "     to completion - reading (1).");

			return 0;
		}
	}
}
