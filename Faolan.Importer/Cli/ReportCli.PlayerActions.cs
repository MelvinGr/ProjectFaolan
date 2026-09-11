using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report player-actions`: every `PerformSpecialAction` (0x205A) the corpus contains, grouped by its selector
	// word, with the server's answer - the player-addressed BehaviorControlCenterIIR records and the other IIRs that
	// follow it within a short window.
	//
	// The selector catalog in docs/subsystems/player-actions.md was measured by hand against the chat-annotated
	// captures. This ships that measurement so it can be re-derived instead of trusted, and it adds the half the
	// hand pass could not do mechanically: the selector is the granted ability's `Slot` - the leading u32 of that
	// ability's 0x2052 grant - so a selector names itself out of the Abilities catalog. That is what settles the two
	// selectors the hand pass left as `[V0: unidentified]`, and it is why an ungranted action is one the client never
	// sends: the client only has the number if a grant gave it the number.
	//
	// `--report-arg <hex>` narrows to one selector and dumps every sighting in full.
	public static partial class ReportCli
	{
		// How many packets after the request count as its answer. The BCC lands within a handful of frames; a wider
		// window starts folding in the next action's reply, which is the failure mode a fixed count is chosen to
		// avoid over a time window (the dumps' ticks are coarse on some captures).
		private const int AnswerWindow = 12;

		private static void PlayerActions(IReadOnlyList<NamedCapture> captures, IReadOnlyDictionary<uint, string> abilityBySlot, string? selectorArg)
		{
			var only = ParseHexWord(selectorArg);
			var behaviorNames = Enum.GetValues<BehaviorId>().ToDictionary(b => (uint)b, b => b.ToString());
			var seen = new SortedDictionary<uint, Selector>();

			foreach (var capture in captures)
			{
				var packets = capture.Packets;
				// The player's own handle, so an answer can be told from the ambient NPC traffic that shares the
				// window. The client only ever sends BehaviorControlCenter records for the character it drives, so
				// the most common actor across the c2s ones is the player - no session state needed, and it works
				// on a capture whose character we do not otherwise know. Without this the window folds in every
				// nearby NPC's idle Emote and the per-selector spec reads as noise.
				var player = PlayerHandle(packets);
				for (var i = 0; i < packets.Count; i++)
				{
					if (packets[i].IsDownload || packets[i].WireOpcode != CapturedPacket.Wire(GameCharAgentMethods.PerformSpecialAction))
						continue;

					var body = packets[i].Body;
					if (body.Length < sizeof(uint))
						continue; // a dump-truncated request carries no selector to group by

					var selector = body.ReadU32Be(0);
					if (only != 0 && selector != only)
						continue;

					if (!seen.TryGetValue(selector, out var entry))
						seen[selector] = entry = new Selector();

					entry.Count++;
					entry.Captures.Add(capture.Name);

					// The answer: the IIR records the server pushes next. A BCC is read out properly (its behaviour id
					// is the identity of the stance); anything else is recorded by IIR type, because "rest is answered
					// by four StatUpdates and a sprint bar" is itself a fact about the action.
					for (var j = i + 1; j < packets.Count && j <= i + AnswerWindow; j++)
					{
						var answer = packets[j];
						if (!answer.IsDownload || !answer.IsIirCarrier)
							continue;

						if (answer.SubOpcode == BehaviorControlCenterCodec.Opcode && answer.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
						{
							if (player != 0 && bcc.Actor != player)
								continue; // an NPC's behaviour that merely shares the window

							var key = (bcc.BehaviorId, (uint)bcc.Args.Length, (uint)bcc.Tail.Length);
							entry.Behaviors[key] = entry.Behaviors.GetValueOrDefault(key) + 1;
							// Narrowed to one selector, the report becomes a SPEC rather than a census: the first few
							// answers in full, which is what a server-side implementation of the stance has to
							// reproduce. Bounded because one selector has 296 sightings.
							if (only != 0 && entry.Records.Count < 12)
								entry.Records.Add((capture.Name, packets[j].Frame, bcc));
						}
						else
							entry.OtherIirs[answer.SubOpcode] = entry.OtherIirs.GetValueOrDefault(answer.SubOpcode) + 1;
					}
				}
			}

			Console.WriteLine($"PerformSpecialAction (0x205A) selectors across {captures.Count} captures.");
			Console.WriteLine("The selector IS the granted ability's Slot (the leading u32 of its 0x2052 grant), so it names itself.");
			Console.WriteLine();

			foreach (var (selector, entry) in seen)
			{
				var name = abilityBySlot.TryGetValue(selector, out var a)
					? a
					: "<no ability with this slot>";
				Console.WriteLine($"  0x{selector:X3}  ({selector,4})  x{entry.Count,-4} {name,-32} in {entry.Captures.Count} capture(s)");

				foreach (var ((id, args, tail), n) in entry.Behaviors.OrderByDescending(b => b.Value))
					Console.WriteLine($"           -> BCC behaviour {id,6} {(behaviorNames.TryGetValue(id, out var bn) ? bn : ""),-24} args {args,3}B tail {tail,3}B  x{n}");

				foreach (var (iir, n) in entry.OtherIirs.OrderByDescending(o => o.Value))
					Console.WriteLine($"           -> {(Enum.IsDefined((IirTypes)iir) ? ((IirTypes)iir).ToString() : $"0x{iir:X8}")}  x{n}");

				if (only == 0)
					continue;

				foreach (var c in entry.Captures.Order())
					Console.WriteLine($"           in {c}");

				Console.WriteLine();
				Console.WriteLine("           the answering records in full (what a server-side implementation must reproduce):");
				foreach (var (capture, frame, bcc) in entry.Records)
				{
					var command = bcc.Args.FromProtobuf<CommandMessage>();
					Console.WriteLine($"             {capture} f{frame}  actor 0x{bcc.Actor:X8} form 0x{bcc.Seq:X4} "
						+ $"slot {bcc.BehaviorId} {(behaviorNames.TryGetValue(bcc.BehaviorId, out var n2) ? n2 : "")}");
					Console.WriteLine($"                 args   action={command.BccActionId} phase={command.CommandqueueServerId} flags={command.Flags} "
						+ $"status={command.ExecutionStatus} clientId={(command.ShouldSerializeCommandqueueClientId() ? command.CommandqueueClientId.ToString() : "-")} "
						+ $"animRandom={(command.ShouldSerializeAnimRandom() ? command.AnimRandom.ToString() : "-")} "
						+ $"recharge={(command.ShouldSerializeRechargeTime() ? command.RechargeTime.ToString() : "-")} "
						+ $"activation={(command.ShouldSerializeActivationTime() ? command.ActivationTime.ToString() : "-")}");
					Console.WriteLine($"                 tail   {Convert.ToHexString(bcc.Tail)}");
					Console.WriteLine($"                 record {Convert.ToHexString(BehaviorControlCenterCodec.Build(bcc))}");
				}
			}
		}

		// The handle of the character the capture's client drives: the most common actor among the c2s
		// BehaviorControlCenterIIR records. 0 when the capture carries none (then nothing is filtered, which is the
		// safe direction for a diagnostic - it shows more, not less).
		private static uint PlayerHandle(IReadOnlyList<CapturedPacket> packets)
		{
			var actors = new Dictionary<uint, int>();
			foreach (var p in packets)
			{
				if (p.IsDownload || !p.IsIirCarrier || p.SubOpcode != BehaviorControlCenterCodec.Opcode || !p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
					continue;

				actors[bcc.Actor] = actors.GetValueOrDefault(bcc.Actor) + 1;
			}

			return actors.Count == 0
				? 0u
				: actors.MaxBy(a => a.Value).Key;
		}

		// A selector's sightings: how often, in which captures, and what the server answered with.
		private sealed class Selector
		{
			public readonly Dictionary<(uint Id, uint ArgsLength, uint TailLength), int> Behaviors = [];
			public readonly SortedSet<string> Captures = [];
			public readonly Dictionary<uint, int> OtherIirs = [];
			public readonly List<(string Capture, int Frame, BehaviorControlCenterIIR Bcc)> Records = [];
			public int Count;
		}

		private static uint ParseHexWord(string? arg)
		{
			return uint.TryParse((arg ?? "").TrimStart('0', 'x', 'X'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
				? v
				: 0u;
		}
	}
}
