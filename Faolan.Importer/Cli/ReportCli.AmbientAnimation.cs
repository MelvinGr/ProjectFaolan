using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using ProtoBuf;

namespace Faolan.Importer.Cli
{
	// `--report ambient-animation`: what retail streams to make a standing NPC LOOK alive (Gitea #766 / #225).
	//
	// Two `BehaviorControlCenterIIR` commands carry it, and between them they are 63,743 of the corpus' 76,262 BCC
	// records - the busiest thing on the wire after movement:
	//
	//   * `BehaviorId.BccAction` (0x07) with a FIXED `bcc_action_id` - the "is alive / idle" assert.
	//   * `BehaviorId.Emote` (0x05) with a VARYING one plus an `activation_time` - the ambient random animation
	//     (a citizen scratching, looking round, leaning on a wall).
	//
	// Both are `CommandMessage` args with an EMPTY tail, so a record is fully described by its command, its
	// animation id and its timing. That is what this report measures, per creature TYPE and per actor: which
	// animation ids retail sends for which creature, how often one actor gets one, and how `activation_time`
	// relates to the record's spacing.
	//
	// The point of measuring per ACTOR rather than per capture: our own heartbeat (#225) pulses every visible NPC on
	// a shared timer, which is a per-capture rate that looks nothing like retail's. The question a fidelity fix needs
	// answered is what ONE creature receives, and how often.
	public static partial class ReportCli
	{
		// How long after a MoveTask its actor is still counted as MOVING. A patrol path takes many seconds to walk and
		// the record that starts it is the only signal, so a window is the only way to ask the question at all; 10s is
		// comfortably inside the p50 gap between one actor's MoveTasks.
		private const double MovingWindowSeconds = 10.0;

		// The two ambient commands, in the order they are reported.
		private static readonly BehaviorId[] AmbientCommands = [BehaviorId.BccAction, BehaviorId.Emote];

		private static void AmbientAnimation(IReadOnlyList<NamedCapture> captures, string? nameFilter)
		{
			var templateNames = new Dictionary<uint, string>();
			// (command, template) -> the animation ids retail sent for that creature, with counts.
			var perTemplate = new Dictionary<(BehaviorId Command, uint Template), Dictionary<uint, int>>();
			// (command, capture, actor) -> that actor's records (time + the activation_time it stated), so a cadence is
			// per-ENTITY not per-capture, and so the NEXT record's delay can be measured against the LAST one's stated
			// duration - the question that decides whether the data times the cadence or the server does.
			var perActor = new Dictionary<(BehaviorId Command, string Capture, uint Actor), List<(long Ticks, uint? Activation)>>();
			var activations = new Dictionary<BehaviorId, List<uint>>();
			// animation id -> the activation_time values retail stated for it. One value means the duration belongs to
			// the ANIMATION and can be stored beside it; many means it is decided per record.
			var perAnimation = new Dictionary<(BehaviorId Command, uint Animation), Dictionary<uint, int>>();
			var counterSteps = new Dictionary<BehaviorId, List<long>>();
			var players = new Dictionary<BehaviorId, int>();
			var unattributed = new Dictionary<BehaviorId, int>();
			// Does retail play an ambient animation at an actor it is also MOVING? The runtime has to decide whether to
			// suppress one on a walking patroller, and the answer is in whether an ambient record ever lands between two
			// of the same actor's MoveTasks.
			var whileMoving = new Dictionary<BehaviorId, int>();
			var whileStill = new Dictionary<BehaviorId, int>();

			foreach (var c in captures)
			{
				if (!string.IsNullOrEmpty(nameFilter) && !c.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
					continue;

				// Who is who, per capture. A retail character id sits BELOW the handle bases, so the number line
				// cannot answer this - the spawn's `player_stats` is the only reliable test (the BccAttackTail rule).
				var npcTemplates = new Dictionary<uint, uint>();
				var playerHandles = new HashSet<uint>();
				// actor -> the times it was told to MOVE. An ambient record within MovingWindowSeconds after one is
				// "while moving": a MoveTask is an instruction to walk, so the walk is under way for a while after it.
				var moves = new Dictionary<uint, List<long>>();
				foreach (var p in c.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: MoveTaskCodec.Opcode }
					    && p.TryParse(MoveTaskCodec.Parse, out var move))
						Bump(moves, move.Handle).Add(p.Ticks);
				}

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;
					if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn))
						continue;

					if (spawn.Character?.player_stats != null)
					{
						playerHandles.Add(spawn.Handle);
						continue;
					}

					if (spawn.Character?.npc_stats?.TemplateId is not { } template || template == 0)
						continue;

					npcTemplates[spawn.Handle] = template;
					if (!string.IsNullOrEmpty(spawn.Character?.Name))
						templateNames[template] = spawn.Character!.Name!;
				}

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;
					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
						continue;

					var command = (BehaviorId)bcc.BehaviorId;
					if (!AmbientCommands.Contains(command))
						continue;

					var args = Args(bcc);
					if (args == null)
						continue;

					Bump(perActor, (command, c.Name, bcc.Actor)).Add((p.Ticks, args.ShouldSerializeActivationTime() ? args.ActivationTime : null));
					if (args.ShouldSerializeActivationTime())
					{
						Bump(activations, command).Add(args.ActivationTime);
						var durations = Bump(perAnimation, (command, args.BccActionId));
						durations[args.ActivationTime] = durations.GetValueOrDefault(args.ActivationTime) + 1;
					}

					if (playerHandles.Contains(bcc.Actor))
					{
						players[command] = players.GetValueOrDefault(command) + 1;
						continue;
					}

					if (!npcTemplates.TryGetValue(bcc.Actor, out var t))
					{
						unattributed[command] = unattributed.GetValueOrDefault(command) + 1;
						continue;
					}

					var moving = moves.TryGetValue(bcc.Actor, out var times)
					             && times.Any(m => p.Ticks >= m && TimeSpan.FromTicks(p.Ticks - m).TotalSeconds <= MovingWindowSeconds);
					if (moving)
						whileMoving[command] = whileMoving.GetValueOrDefault(command) + 1;
					else
						whileStill[command] = whileStill.GetValueOrDefault(command) + 1;

					var ids = Bump(perTemplate, (command, t));
					ids[args.BccActionId] = ids.GetValueOrDefault(args.BccActionId) + 1;
				}

				// `commandqueue_server_id` walks per actor - measure its STEP, since that is what says whether it is a
				// monotonic tick (as our heartbeat assumes) or an id shared with the rest of the behaviour stream.
				foreach (var command in AmbientCommands)
				{
					foreach (var g in c
						.Packets.Where(p => p is { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						.Select(p => p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) ? bcc : null)
						.Where(b => b != null && b.BehaviorId == (uint)command)
						.Select(b => (b!.Actor, Args: Args(b)))
						.Where(x => x.Args != null)
						.GroupBy(x => x.Actor))
					{
						var counters = g.Select(x => (long)x.Args!.CommandqueueServerId).ToList();
						for (var i = 1; i < counters.Count; i++)
							Bump(counterSteps, command).Add(counters[i] - counters[i - 1]);
					}
				}
			}

			foreach (var command in AmbientCommands)
			{
				var actors = perActor.Where(kv => kv.Key.Command == command).ToList();
				var records = actors.Sum(kv => kv.Value.Count);
				Console.WriteLine();
				Console.WriteLine($"=== 0x{(uint)command:X2} {command}: {records} s2c records over {actors.Count} actors");
				if (records == 0)
					continue;

				Console.WriteLine($"  records per actor: {Percentiles(actors.Select(kv => (double)kv.Value.Count).ToList(), "")}");
				var gaps = new List<double>();
				var ratios = new List<double>();
				var pauses = new List<double>();
				foreach (var (_, times) in actors)
				{
					times.Sort();
					for (var i = 1; i < times.Count; i++)
					{
						var gap = TimeSpan.FromTicks(times[i].Ticks - times[i - 1].Ticks).TotalSeconds;
						gaps.Add(gap);
						// Against the PREVIOUS record's stated activation_time, read as centiseconds: a ratio of 1
						// means the next ambient animation is scheduled for exactly when the last one finishes.
						if (times[i - 1].Activation is { } activation and > 0 && gap > 0)
						{
							ratios.Add(gap / (activation / 100.0));
							pauses.Add(gap - activation / 100.0); // how long the creature STANDS between animations
						}
					}
				}

				if (gaps.Count > 0)
					Console.WriteLine($"  seconds between one ACTOR's records ({gaps.Count} gaps): {Percentiles(gaps, "s")}");
				if (ratios.Count > 0)
					Console.WriteLine($"  gap / the PREVIOUS record's activation_time-as-centiseconds ({ratios.Count}): {Percentiles(ratios, "x")}"
					                  + $", within 10% of 1.0 on {ratios.Count(r => r is > 0.9 and < 1.1)}, BELOW 0.9 on {ratios.Count(r => r < 0.9)}");
				if (pauses.Count > 0)
					Console.WriteLine($"  seconds STANDING between animations (gap - duration): {Percentiles(pauses, "s")}");
				if (activations.TryGetValue(command, out var acts) && acts.Count > 0)
					Console.WriteLine($"  activation_time present on {acts.Count} of {records}: {Percentiles(acts.Select(a => (double)a).ToList(), "")}");
				else
					Console.WriteLine("  activation_time: never sent");
				if (counterSteps.TryGetValue(command, out var steps) && steps.Count > 0)
					Console.WriteLine($"  commandqueue_server_id step per actor: {Percentiles(steps.Select(s => (double)s).ToList(), "")}, +1 on {steps.Count(s => s == 1)} of {steps.Count}");

				Console.WriteLine($"  addressed to a PLAYER: {players.GetValueOrDefault(command)}; to an actor no spawn in the capture named: {unattributed.GetValueOrDefault(command)}");
				Console.WriteLine($"  within {MovingWindowSeconds:F0}s of the actor's own MoveTask: {whileMoving.GetValueOrDefault(command)}; while it was standing: {whileStill.GetValueOrDefault(command)}");

				var byAnimation = perAnimation.Where(kv => kv.Key.Command == command).ToList();
				if (byAnimation.Count > 0)
				{
					var single = byAnimation.Count(kv => kv.Value.Count == 1);
					Console.WriteLine($"  activation_time per ANIMATION id: {single} of {byAnimation.Count} animations state exactly one value"
					                  + $" (so the duration belongs to the animation), distinct values per animation {Percentiles(byAnimation.Select(kv => (double)kv.Value.Count).ToList(), "")}");
				}

				var byTemplate = perTemplate.Where(kv => kv.Key.Command == command).ToList();
				var animations = byTemplate.SelectMany(kv => kv.Value.Keys).Distinct().Count();
				Console.WriteLine($"  attributed to {byTemplate.Count} creature types, carrying {animations} distinct animation ids");
				Console.WriteLine($"  animation ids per creature: {Percentiles(byTemplate.Select(kv => (double)kv.Value.Count).ToList(), "")}");
				Console.WriteLine();
				Console.WriteLine("    template  creature                        recs  animation ids");
				foreach (var kv in byTemplate.OrderByDescending(kv => kv.Value.Values.Sum()).Take(25))
				{
					var name = templateNames.GetValueOrDefault(kv.Key.Template, "?");
					var ids = string.Join(" ", kv.Value.OrderByDescending(a => a.Value).Take(8).Select(a => $"{a.Key}x{a.Value}"));
					Console.WriteLine($"    {kv.Key.Template,8}  {name,-30}  {kv.Value.Values.Sum(),4}  {ids}");
				}
			}
		}

		// The record's `CommandMessage` args, or null when they do not deserialize (a record whose args are not this
		// message at all - reported rather than assumed away).
		private static CommandMessage? Args(BehaviorControlCenterIIR bcc)
		{
			try
			{
				return Serializer.Deserialize<CommandMessage>(new ReadOnlyMemory<byte>(bcc.Args));
			}
			catch (ProtoException)
			{
				return null;
			}
		}

		private static List<T> Bump<TKey, T>(Dictionary<TKey, List<T>> map, TKey key) where TKey : notnull
		{
			if (!map.TryGetValue(key, out var list))
				map[key] = list = [];
			return list;
		}

		private static Dictionary<uint, int> Bump<TKey>(Dictionary<TKey, Dictionary<uint, int>> map, TKey key) where TKey : notnull
		{
			if (!map.TryGetValue(key, out var inner))
				map[key] = inner = [];
			return inner;
		}

		private static string Percentiles(List<double> values, string unit)
		{
			values.Sort();
			double At(double p) => values[Math.Clamp((int)(values.Count * p), 0, values.Count - 1)];
			return $"p10 {At(0.10):F1}{unit} p50 {At(0.50):F1}{unit} p90 {At(0.90):F1}{unit} max {values[^1]:F0}{unit}";
		}
	}
}
