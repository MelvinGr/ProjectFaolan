using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// One harvested formation-follow: a creature that retail bound to ANOTHER creature's movement, by name (the
	// stable NpcTemplate join key - the wire handle is session-volatile), with the offset and slot it held.
	public sealed record NpcFollowPair(string Follower, string Leader, float OffsetX, float OffsetY, float OffsetZ, uint Slot);

	// Harvests NPC-follows-NPC from the captured mode-07 MoveTasks. The maintainer's annotation "giner &
	// scruffy should be chasing each other" names an ambient behaviour we had nothing for; the wire turns out to be
	// an ordinary MoveTaskIIR whose task is `Formation`, naming the other NPC's handle:
	//
	//   [u32 class 0xC350][u32 leaderHandle][f32 offsetX][f32 offsetY][f32 offsetZ][u32 slot]
	//
	// It is not a mutual chase - one NPC walks a path (a mode-04 list) and the other is BOUND to it, its own
	// from-position landing within a couple of units of the leader's every time.
	//
	// Both handles are resolved to creature names through the FA015807 spawns of the same capture, exactly as
	// NpcDeathBehaviorExtractor resolves a dying handle - so a pair only survives when we know what both ends are,
	// which is also what drops the pairs whose leader is a player's pet or another player.
	public static class NpcFollowPairExtractor
	{
		public static List<NpcFollowPair> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var handleName = new Dictionary<uint, string>();
			foreach (var e in NpcSpawnExtractor.Extract(packets))
			{
				if (!string.IsNullOrEmpty(e.Name))
					handleName[e.Handle] = e.Name;
			}

			var results = new List<NpcFollowPair>();
			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: MoveTaskCodec.Opcode })
					continue;

				MoveTaskIIR move;
				try
				{
					move = MoveTaskCodec.Parse(p.Record);
				}
				catch (CodecParseException)
				{
					continue; // a truncated capture, not a move task
				}

				if (move.Task.Mode != MoveTaskNodes.ModeFormation)
					continue;

				// Both ends have to be creatures we know. A follower we cannot name cannot be stored against a
				// template, and a leader we cannot name would make the pair meaningless.
				if (!handleName.TryGetValue(move.Handle, out var follower) || !handleName.TryGetValue(move.Task.Formation6[1], out var leader))
					continue;

				results.Add(new NpcFollowPair(follower, leader,
					BitConverter.UInt32BitsToSingle(move.Task.Formation6[2]),
					BitConverter.UInt32BitsToSingle(move.Task.Formation6[3]),
					BitConverter.UInt32BitsToSingle(move.Task.Formation6[4]),
					move.Task.Formation6[5]));
			}

			return results;
		}

		// Merge harvests across captures to one leader per follower name. A creature follows the same kind of leader
		// everywhere in the corpus; where a name were ever seen behind two different leaders the MAJORITY wins, the
		// same defensive rule the death-animation merge uses.
		//
		// The OFFSET is deliberately not merged in: it is per-instance, not per-type - the four
		// `Black Dragon Guard Patroller`s that share one captain each hold a different corner of a 3x3 grid. Picking
		// one of them and stamping it on the type would put every follower of that type in the same spot.
		public static List<NpcFollowPair> Merge(IEnumerable<NpcFollowPair> all)
		{
			return all
				.GroupBy(f => f.Follower)
				.Select(g => g.GroupBy(f => f.Leader).OrderByDescending(x => x.Count()).First().First())
				.ToList();
		}
	}
}
