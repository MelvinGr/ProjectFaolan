using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report spawn-animation`: is `npc_stats.spawn_animation` a property of the spawn EVENT rather than of the
	// entity? (Gitea #607.)
	//
	// #607 established what it is not. Retail never sends it as `0` - over the captured spawns it is either UNSET or
	// `1`, so it is a presence FLAG - and no field of the spawn body predicts which spawns carry it: `--report
	// field-predictors` cannot beat the 91.7% a constant scores, the TYPE itself reaches only 93.6%, and the same
	// creature name appears in both buckets (Black Dragon Guard: 53 with, 234 without).
	//
	// Its own name is the hypothesis: *play the spawn-in animation for this entity right now*. If that is what it means
	// then the discriminator is not IN the body at all - it is WHY the packet was sent. A login burst delivers NPCs that
	// were already standing there; a mid-session spawn is an entity ARRIVING. That is a property of the packet's place
	// in the session, which is exactly the class of predictor a field census cannot see.
	//
	// So this partitions every captured NPC spawn on that axis. The burst boundary is the client's first
	// `UpdateQuestMarkerNPC` poll - the same boundary `NpcSpawnExtractor.ExtractLoginBurst` uses, and it means "the
	// client has taken delivery of the burst and started asking about individual NPCs".
	//
	// ⛔ **The hypothesis is FALSIFIED, and this report is what falsified it.** If the flag meant "this entity is
	// arriving", the burst rate would be ~0 and the mid-session rate high. Measured, it is the other way round:
	//
	//     LOGIN BURST    4,536 spawns,   333 with the flag   7.3%
	//     MID-SESSION   18,580 spawns, 1,076 with the flag   5.8%
	//
	// 333 spawns carry it inside a burst, so it plainly does not mean "arriving". Per creature it is worse than a wash:
	// only 14 of the 351 names in both partitions behave as predicted, and several go the WRONG way outright -
	// `Knight` 8/16 in the burst against 0/156 mid-session, `Priest` 6/26 against 0/132.
	//
	// Kept rather than deleted, because the axis is the right KIND of predictor for a per-spawn-event field (a field
	// census cannot see it at all) and the next hypothesis should be tested the same way. ⚠️ Note the confound if it
	// is: the partition is per capture, so a name concentrated in one session can carry that session's habit rather
	// than a rule - see the `report_predictors_must_be_per_event_not_per_capture` note.
	public static partial class ReportCli
	{
		private static void SpawnAnimation(IReadOnlyList<NamedCapture> captures)
		{
			var burst = new SpawnAnimationBucket();
			var midSession = new SpawnAnimationBucket();
			var noBoundary = 0;

			foreach (var c in captures)
			{
				// Where the login burst ends: the first c2s quest-marker poll. A capture with none (a create-flow or a
				// mid-session-only dump) has no boundary, and its spawns are counted apart rather than guessed at.
				var boundary = -1;
				for (var i = 0; i < c.Packets.Count; i++)
				{
					if (c.Packets[i] is { IsDownload: false } p && p.WireOpcode == CapturedPacket.Wire(GameCharAgentMethods.UpdateQuestMarkerNPC))
					{
						boundary = i;
						break;
					}
				}

				for (var i = 0; i < c.Packets.Count; i++)
				{
					var p = c.Packets[i];
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;
					if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn) || spawn.Character?.player_stats != null)
						continue;
					if (spawn.Character?.npc_stats is not { } npc)
						continue;

					if (boundary < 0)
					{
						noBoundary++;
						continue;
					}

					(i < boundary ? burst : midSession).Add(npc.ShouldSerializeSpawnAnimation(), spawn.Character.Name ?? "?");
				}
			}

			Console.WriteLine("npc_stats.spawn_animation, partitioned by WHY the spawn was sent (#607)");
			Console.WriteLine("The burst boundary is the client's first UpdateQuestMarkerNPC poll.");
			Console.WriteLine();
			Console.WriteLine("  where                   spawns    with flag   without    with%");
			Console.WriteLine($"  LOGIN BURST          {burst.Total,9}  {burst.With,10}  {burst.Without,8}  {burst.Percent,6:F1}%");
			Console.WriteLine($"  MID-SESSION          {midSession.Total,9}  {midSession.With,10}  {midSession.Without,8}  {midSession.Percent,6:F1}%");
			Console.WriteLine($"  (no burst boundary)  {noBoundary,9}");
			Console.WriteLine();

			// The claim only means something if it holds per CREATURE too: a name that appears in both partitions and
			// carries the flag only in the mid-session one is the same fact seen without the population mixing in.
			var both = burst.Names.Keys.Intersect(midSession.Names.Keys).ToList();
			Console.WriteLine($"{both.Count} creature name(s) appear in BOTH partitions - the within-name check:");
			Console.WriteLine();
			Console.WriteLine("  creature                          burst with/total    mid-session with/total");
			foreach (var name in both.OrderByDescending(n => midSession.Names[n].Total + burst.Names[n].Total).Take(25))
			{
				var b = burst.Names[name];
				var m = midSession.Names[name];
				Console.WriteLine($"  {(name.Length > 30 ? name[..30] : name),-30}  {b.With,7}/{b.Total,-8}       {m.With,7}/{m.Total,-8}");
			}

			var agree = both.Count(n => burst.Names[n].With == 0 && midSession.Names[n].With == midSession.Names[n].Total);
			Console.WriteLine();
			Console.WriteLine($"{agree} of {both.Count} names behave EXACTLY as the hypothesis predicts (never in the burst, always mid-session).");
		}

		private sealed class SpawnAnimationBucket
		{
			public int With;
			public int Without;
			public readonly Dictionary<string, SpawnAnimationBucket> Names = [];

			public int Total => With + Without;
			public double Percent => Total == 0 ? 0 : With * 100.0 / Total;

			public void Add(bool present, string? name = null)
			{
				if (present)
					With++;
				else
					Without++;

				if (name == null)
					return;

				if (!Names.TryGetValue(name, out var byName))
					Names[name] = byName = new SpawnAnimationBucket();

				byName.Add(present);
			}
		}
	}
}
