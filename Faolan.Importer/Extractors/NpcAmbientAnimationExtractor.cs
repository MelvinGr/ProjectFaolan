using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using ProtoBuf;

namespace Faolan.Importer.Extractors
{
	// The ambient ANIMATIONS retail plays for each creature type, harvested from the captured
	// `BehaviorId.Emote` records: `[bcc_action_id = the animation][activation_time = its length in centiseconds]`,
	// no tail. `--report ambient-animation` measures the same records across every other dimension (cadence, the
	// per-actor counter, how the duration relates to the spacing); this extracts the part that is data.
	//
	// Attribution is by actor handle against the same capture's FA015807 spawns, and a record whose actor no spawn
	// named is dropped - the handle is meaningless outside the session that minted it.
	public static class NpcAmbientAnimationExtractor
	{
		// One creature's one ambient animation, with how long it runs and how many times retail was seen playing it.
		public readonly record struct Entry(uint TemplateId, uint AnimationId, uint DurationCentiseconds, uint Weight);

		// Every ambient animation in one capture, already collapsed per (type, animation).
		public static List<Entry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			ArgumentNullException.ThrowIfNull(packets);

			// Who is who. A retail character id sits below the handle bases, so the number line cannot tell an NPC
			// from a player - the spawn's `player_stats` is the only reliable test (the BccAttackTail rule). A later
			// spawn wins: handles are recycled within a session.
			var templates = new Dictionary<uint, uint>();
			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
					continue;
				if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn) || spawn.Character?.player_stats != null)
					continue;

				if (spawn.Character?.npc_stats?.TemplateId is { } template && template != 0)
					templates[spawn.Handle] = template;
				else
					templates.Remove(spawn.Handle); // a recycled handle that is now something untyped
			}

			// (type, animation) -> the durations stated for it, with counts, so the modal one can be chosen below.
			var seen = new Dictionary<(uint Template, uint Animation), Dictionary<uint, uint>>();
			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;
				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) || bcc.BehaviorId != (uint)BehaviorId.Emote)
					continue;
				if (!templates.TryGetValue(bcc.Actor, out var template))
					continue;

				CommandMessage args;
				try
				{
					args = Serializer.Deserialize<CommandMessage>(new ReadOnlyMemory<byte>(bcc.Args));
				}
				catch (ProtoException)
				{
					continue;
				}

				// An animation with no stated length cannot time its own successor, so it is not harvestable as data -
				// 216 of the corpus' 25,778 records. Skipped rather than defaulted to some invented duration.
				if (!args.ShouldSerializeActivationTime() || args.ActivationTime == 0 || args.BccActionId == 0)
					continue;

				if (!seen.TryGetValue((template, args.BccActionId), out var durations))
					seen[(template, args.BccActionId)] = durations = [];

				durations[args.ActivationTime] = durations.GetValueOrDefault(args.ActivationTime) + 1;
			}

			return seen
				.Select(kv => new Entry(kv.Key.Template, kv.Key.Animation,
					kv.Value.OrderByDescending(d => d.Value).ThenBy(d => d.Key).First().Key,
					(uint)kv.Value.Values.Sum(v => v)))
				.ToList();
		}

		// Merges the per-capture harvests: weights add (a creature seen idling in three captures is three captures'
		// worth of evidence) and the duration is the modal one across all of them.
		public static List<Entry> Merge(IEnumerable<Entry> entries)
		{
			ArgumentNullException.ThrowIfNull(entries);

			var merged = new Dictionary<(uint Template, uint Animation), (Dictionary<uint, uint> Durations, uint Weight)>();
			foreach (var e in entries)
			{
				if (!merged.TryGetValue((e.TemplateId, e.AnimationId), out var slot))
					merged[(e.TemplateId, e.AnimationId)] = slot = ([], 0);

				slot.Durations[e.DurationCentiseconds] = slot.Durations.GetValueOrDefault(e.DurationCentiseconds) + e.Weight;
				merged[(e.TemplateId, e.AnimationId)] = (slot.Durations, slot.Weight + e.Weight);
			}

			return merged
				.Select(kv => new Entry(kv.Key.Template, kv.Key.Animation,
					kv.Value.Durations.OrderByDescending(d => d.Value).ThenBy(d => d.Key).First().Key,
					kv.Value.Weight))
				.OrderBy(e => e.TemplateId)
				.ThenBy(e => e.AnimationId)
				.ToList();
		}
	}
}
