using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report npc-debuff`: when an NPC lands a spell on the player, does retail re-send the `MagicBuffIIR` on every
	// swing, and does it ever send the end twin?
	//
	// The reported symptom is a `Butcher` debuff stuck at 0s on the player's bar for ever, surviving a map change.
	// Ours sends one `MagicBuffIIR` per hit (372 sends for 352 hits in one fight) and never an end, because
	// `SendMagicBuffIir`'s dedup+register is gated on `applyTo == applyFrom` - a SELF buff - so an NPC-onto-player
	// record gets no registry entry, no expiry and no end twin.
	//
	// Two different defects could produce that icon, and the fix differs:
	//   1. retail applies once and we re-send per swing  -> the re-send is the bug, the missing expiry is secondary;
	//   2. retail re-sends per swing too                 -> the re-send is correct and the missing end is the bug.
	// The corpus decides it, so this counts apply records per (subject, caster, spell) run and looks for the end
	// (`form` 1) that closes it.
	public static partial class ReportCli
	{
		private static void NpcDebuff(IReadOnlyList<NamedCapture> captures)
		{
			// One row per (capture, subject, caster, spell): how many applies, how many ends.
			var runs = new Dictionary<(string Capture, uint To, uint From, uint Spell), (int Applies, int Ends)>();
			var selfApplies = 0;
			var npcOntoPlayer = 0;
			var noCaster = 0;

			foreach (var c in captures)
			{
				var players = new HashSet<uint>();
				var npcs = new HashSet<uint>();

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode)
					{
						if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn))
							continue;

						if (spawn.Character?.player_stats == null)
							npcs.Add(spawn.Handle);
						else
							players.Add(spawn.Handle);
						continue;
					}

					if (p.SubOpcode != MagicBuffCodec.Opcode)
						continue;
					if (!p.TryParse(MagicBuffCodec.Parse, out var buff))
						continue;

					if (buff.ApplyFrom == null)
					{
						noCaster++;
						continue;
					}

					if (buff.ApplyFrom == buff.ApplyTo)
					{
						selfApplies++;
						continue;
					}

					// The direction this ticket is about: a MOB's spell landing on a player.
					if (!npcs.Contains(buff.ApplyFrom.Value) || !players.Contains(buff.ApplyTo))
						continue;

					npcOntoPlayer++;
					var key = (c.Name, buff.ApplyTo, buff.ApplyFrom.Value, MagicBuffIds.BaseOf(buff.SpellBaseId));
					var (applies, ends) = runs.GetValueOrDefault(key);
					if (buff.Form == MagicBuffIds.FormEnd)
						ends++;
					else
						applies++;

					runs[key] = (applies, ends);
				}
			}

			Console.WriteLine("npc-debuff: what retail sends when a MOB lands a spell on the PLAYER (#907)\n");
			Console.WriteLine($"MagicBuffIIR records: {selfApplies} self-applied (applyFrom == applyTo), {npcOntoPlayer} NPC-onto-player, {noCaster} with no caster named.\n");

			if (runs.Count == 0)
			{
				Console.WriteLine("=> the corpus contains NO mob-onto-player buff record, so retail's behaviour here cannot be measured from it.");
				return;
			}

			var applyCounts = runs.Values.Select(v => v.Applies).Order().ToList();
			var withEnd = runs.Count(r => r.Value.Ends > 0);

			Console.WriteLine($"{runs.Count} distinct (player, mob, spell) pairings.");
			Console.WriteLine($"  applies per pairing: min={applyCounts[0]}  median={applyCounts[applyCounts.Count / 2]}  max={applyCounts[^1]}");
			Console.WriteLine($"  pairings that ever receive the END twin (form 1): {withEnd} of {runs.Count}");
			Console.WriteLine();

			// The discriminating number. If retail applies once per engagement, almost every pairing is a single
			// apply however long the fight ran; if it re-sends per swing, the counts climb with fight length.
			foreach (var bucket in new[] { 1, 2, 5, 10, 50 })
			{
				var n = applyCounts.Count(a => a <= bucket);
				Console.WriteLine($"  pairings with <= {bucket,3} applies: {n,4} of {runs.Count} ({100.0 * n / runs.Count:0.0}%)");
			}

			Console.WriteLine();
			Console.WriteLine("the busiest pairings (spell id, applies, ends):");
			foreach (var r in runs.OrderByDescending(r => r.Value.Applies).Take(12))
				Console.WriteLine($"  spell {r.Key.Spell,-9} applies={r.Value.Applies,4} ends={r.Value.Ends,3}   mob 0x{r.Key.From:X8} -> player 0x{r.Key.To:X8}  [{r.Key.Capture}]");
		}
	}
}
