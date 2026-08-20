using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report sprint`: the whole sprint/regen message family in one place - what the client sends, what retail
	// answers, which entity each answer is addressed to, and what the bodies hold (#867).
	//
	// It exists because the census says the family is the busiest thing we do not implement (`SetSprintInputState` is
	// 9,329 c2s frames, the third-busiest client message in the corpus) while our inbound handler carries the claim
	// that retail "does NOT reply to" it. Both cannot be true, and the question is not answerable from counts: a
	// 1-byte `DispatchSetIsSprinting` body carries no entity, so whether it is a REPLY about you or a BROADCAST about
	// somebody else is decided by the RECEIVER ENDPOINT - which is header, not body, and therefore invisible to a
	// count-only census.
	//
	// So the report prints three things a decision needs:
	//   1. the body value census per method - what the payloads actually are, not what a signature says they are;
	//   2. the receiver-instance census - one instance per capture means "addressed to you", many means "about them";
	//   3. the c2s -> s2c pairing - whether an input state change is answered, how fast, and with what value.
	public static partial class ReportCli
	{
		// The family, by (receiver class, method). The two directions live on different interfaces: the sprint KEY
		// goes to GameCharAgentProject, every answer comes back on GameCharInterfaceProject.
		private static readonly (uint Class, Enum Method, string Name)[] SprintFamily =
		[
			(RpcInterfaces.GameCharAgentProjectType, GameCharAgentProjectMethods.SetSprintInputState, "SetSprintInputState"),
			(RpcInterfaces.GameCharInterfaceProjectType, GameCharInterfaceProjectMethods.DispatchSetIsSprinting, "DispatchSetIsSprinting"),
			(RpcInterfaces.GameCharInterfaceProjectType, GameCharInterfaceProjectMethods.DispatchSyncCanSprint, "DispatchSyncCanSprint"),
			(RpcInterfaces.GameCharInterfaceProjectType, GameCharInterfaceProjectMethods.DispatchSyncRegenDelayed, "DispatchSyncRegenDelayed"),
			(RpcInterfaces.GameCharInterfaceProjectType, GameCharInterfaceProjectMethods.DispatchUpdateSprintBar, "DispatchUpdateSprintBar")
		];

		private sealed record SprintEvent(string Capture, long Ticks, string Method, bool IsDownload, uint ReceiverInstance, byte[] Body,
			uint SenderType, uint SenderInstance);

		private static void Sprint(IReadOnlyList<NamedCapture> captures)
		{
			var events = new List<SprintEvent>();
			var unreadableHeaders = 0;

			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				var match = SprintFamily.FirstOrDefault(f => f.Class == p.ReceiverClass && CapturedPacket.Wire(f.Method) == p.WireOpcode);
				if (match.Name == null)
					continue;

				// The receiver ENDPOINT (header field 2) is the addressed object. Its instance is the character the
				// message is about - the only thing that can distinguish a reply from a broadcast here.
				Rpc rpc;
				try
				{
					rpc = Rpc.Parse(p.Bytes);
				}
				catch (Exception)
				{
					unreadableHeaders++;
					continue;
				}

				events.Add(new SprintEvent(capture.Name, p.Ticks, match.Name, p.IsDownload, rpc.Receiver.Instance, p.Body,
					rpc.Sender.Type, rpc.Sender.Instance));
			}

			Console.WriteLine($"sprint: {events.Count} frame(s) of the sprint/regen family across {captures.Count} capture(s)"
			                  + (unreadableHeaders > 0 ? $" ({unreadableHeaders} with an unreadable header, skipped)" : ""));
			Console.WriteLine();

			Console.WriteLine("== bodies: every distinct payload, by method");
			foreach (var group in events.GroupBy(e => e.Method).OrderBy(g => g.Key))
			{
				var bodies = group.GroupBy(e => Convert.ToHexString(e.Body)).OrderByDescending(b => b.Count()).ToList();
				Console.WriteLine($"  {group.Key,-24} {group.Count(),6} frame(s), {bodies.Count} distinct body value(s)");
				foreach (var body in bodies.Take(12))
					Console.WriteLine($"      {(body.Key.Length == 0 ? "(empty)" : body.Key),-24} x{body.Count()}");
				if (bodies.Count > 12)
					Console.WriteLine($"      ... and {bodies.Count - 12} more");
			}

			Console.WriteLine();
			Console.WriteLine("== receiver instances: is the answer addressed to ONE character, or to many?");
			Console.WriteLine("   (per capture: the number of distinct addressed instances. 1 = it is about the session's own");
			Console.WriteLine("    character, >1 = it is a broadcast about other entities too.)");
			foreach (var group in events.Where(e => e.IsDownload).GroupBy(e => e.Method).OrderBy(g => g.Key))
			{
				var perCapture = group.GroupBy(e => e.Capture).Select(c => c.Select(e => e.ReceiverInstance).Distinct().Count()).ToList();
				Console.WriteLine($"  {group.Key,-24} captures: {perCapture.Count,4}   distinct instances per capture: "
				                  + $"min {perCapture.Min()}, max {perCapture.Max()}, "
				                  + $"{perCapture.Count(c => c == 1)} capture(s) with exactly 1");
			}

			Console.WriteLine();
			Console.WriteLine("== endpoints: the sender retail addresses each answer FROM (a send cannot be built without it)");
			foreach (var group in events.GroupBy(e => e.Method).OrderBy(g => g.Key))
			{
				var senders = group.GroupBy(e => (e.SenderType, e.SenderInstance)).OrderByDescending(g => g.Count()).ToList();
				Console.WriteLine($"  {group.Key,-24} {senders.Count} distinct sender endpoint(s)");
				foreach (var sender in senders.Take(6))
					Console.WriteLine($"      type 0x{sender.Key.SenderType:X8} ({RpcInterfaces.ByType(sender.Key.SenderType)?.Name ?? "?"}) instance 0x{sender.Key.SenderInstance:X} x{sender.Count()}");
			}

			Console.WriteLine();
			Console.WriteLine("== pairing: does an input state change get answered, and with what?");
			PairSprintInput(events);

			Console.WriteLine();
			RegenDelayedTriggers(events);
		}

		// The client's own signature is `SetSprintInputState(bool sprintInput, bool clientCheck)` and
		// `DispatchSetIsSprinting(isSprinting)`, so the c2s body is TWO bytes and only the first is the key state.
		// The question this answers: retail sends a third as many answers as it gets inputs, so it is not answering
		// every frame - is it answering every CHANGE?
		private static void PairSprintInput(List<SprintEvent> events)
		{
			var inputPairs = new SortedDictionary<string, int>();
			var changesVsReplies = new List<(string Capture, int Changes, int Replies, int Agreeing)>();

			foreach (var capture in events.GroupBy(e => e.Capture))
			{
				var ordered = capture.OrderBy(e => e.Ticks).ToList();

				foreach (var input in ordered.Where(e => e is { IsDownload: false, Method: "SetSprintInputState" }).Where(e => e.Body.Length >= 2))
					inputPairs[$"sprintInput={input.Body[0]} clientCheck={input.Body[1]}"] =
						inputPairs.GetValueOrDefault($"sprintInput={input.Body[0]} clientCheck={input.Body[1]}") + 1;

				// A CHANGE is a frame whose sprintInput differs from the previous one on this capture.
				int changes = 0, agreeing = 0;
				int? previous = null;
				int? lastInput = null;
				foreach (var e in ordered)
				{
					if (e is { IsDownload: false, Method: "SetSprintInputState" } && e.Body.Length >= 2)
					{
						lastInput = e.Body[0];
						if (previous != e.Body[0])
							changes++;
						previous = e.Body[0];
					}
					else if (e is { IsDownload: true, Method: "DispatchSetIsSprinting" } && e.Body.Length >= 1 && lastInput == e.Body[0])
					{
						agreeing++;
					}
				}

				var replies = ordered.Count(e => e is { IsDownload: true, Method: "DispatchSetIsSprinting" });
				if (changes > 0 || replies > 0)
					changesVsReplies.Add((capture.Key, changes, replies, agreeing));
			}

			Console.WriteLine("  c2s body, as the client's own (bool sprintInput, bool clientCheck):");
			foreach (var (key, count) in inputPairs.OrderByDescending(v => v.Value))
				Console.WriteLine($"      {key,-38} x{count}");

			var totalChanges = changesVsReplies.Sum(c => c.Changes);
			var totalReplies = changesVsReplies.Sum(c => c.Replies);
			var totalAgreeing = changesVsReplies.Sum(c => c.Agreeing);
			Console.WriteLine();
			Console.WriteLine($"  sprintInput CHANGES: {totalChanges}   DispatchSetIsSprinting frames: {totalReplies}"
			                  + $"   ratio {(totalChanges == 0 ? 0 : (double)totalReplies / totalChanges):0.00}");
			Console.WriteLine($"  of those answers, {totalAgreeing} ({(totalReplies == 0 ? 0 : 100.0 * totalAgreeing / totalReplies):0.0}%) carry the SAME value as the most recent sprintInput.");
			Console.WriteLine();
			Console.WriteLine("  per capture (the ten with the most answers):");
			foreach (var c in changesVsReplies.OrderByDescending(c => c.Replies).Take(10))
				Console.WriteLine($"      {c.Capture,-44} changes {c.Changes,5}  answers {c.Replies,5}  agreeing {c.Agreeing,5}");
		}

		// What SyncRegenDelayed follows: it is always 0x00 in the corpus, so the interesting question is not its
		// value but its TRIGGER - which message precedes it, and how closely.
		private static void RegenDelayedTriggers(List<SprintEvent> events)
		{
			var precedingWithin = new SortedDictionary<string, int>();
			var gaps = new List<long>();

			foreach (var capture in events.GroupBy(e => e.Capture))
			{
				var ordered = capture.OrderBy(e => e.Ticks).ToList();
				for (var i = 0; i < ordered.Count; i++)
				{
					if (ordered[i] is not { IsDownload: true, Method: "DispatchSyncRegenDelayed" })
						continue;

					if (i == 0)
					{
						precedingWithin["(first frame of the family in the capture)"] = precedingWithin.GetValueOrDefault("(first frame of the family in the capture)") + 1;
						continue;
					}

					var before = ordered[i - 1];
					var label = $"{(before.IsDownload ? "s2c" : "c2s")} {before.Method}" + (before.Body.Length >= 1 ? $" = {before.Body[0]}" : "");
					precedingWithin[label] = precedingWithin.GetValueOrDefault(label) + 1;
					gaps.Add(ordered[i].Ticks - before.Ticks);
				}
			}

			Console.WriteLine("== DispatchSyncRegenDelayed: what immediately precedes it (within the family)");
			foreach (var (key, count) in precedingWithin.OrderByDescending(v => v.Value).Take(12))
				Console.WriteLine($"      {key,-46} x{count}");

			if (gaps.Count > 0)
			{
				gaps.Sort();
				Console.WriteLine($"  gap from that predecessor (ms): min {gaps[0] / TimeSpan.TicksPerMillisecond}, "
				                  + $"median {gaps[gaps.Count / 2] / TimeSpan.TicksPerMillisecond}, "
				                  + $"max {gaps[^1] / TimeSpan.TicksPerMillisecond}");
			}
		}
	}
}
