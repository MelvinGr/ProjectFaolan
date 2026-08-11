using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report npc-pose-check`: does every shipped `NpcStatCores` row carry the pose and idle animation the type is
	// USUALLY caught in, or one it was caught in once? (Gitea #729, moved out of `NpcStatCoreColumnsTests`.)
	//
	// The bug it exists for: a `bcc_state` / `anim_idle_id` is the mode over the type's sightings, so a type whose one
	// captured representative happened to be mid-emote ships that emote as its permanent pose. The Citizen shipped a
	// state its own sightings outnumbered 57 to 1.
	//
	// ⭐ **Why it is a REPORT and not a test.** It compares importer OUTPUT (`faolan.db`) against importer INPUT (the
	// dumps), and as a test it read the input through `fixtures.bin`'s corpus half - a frozen, derived slice. The two
	// populations diverge the moment captures are added: on 2026-08-08 template 14227 (Knight) shipped `anim_idle_id`
	// 42024 from a run over the real dumps while the extract, still at the old capture set, called 42024 an outlier at
	// 12:2 out of a sample of fourteen. The gate was judging the shipped value against evidence it had already
	// outgrown, and the sanctioned fix - regenerate the extract - rebaselines twenty-odd unrelated gates in one
	// commit. Run against the real dumps there is no second population and no such failure mode.
	//
	// It also means the `minimumEvidence` floor the test needed can go: that existed only to stop a thin frozen sample
	// outvoting a fuller live one.
	public static partial class ReportCli
	{
		// How far another value may outnumber the shipped one before it reads as "the representative was caught
		// mid-emote" rather than "the corpus is thin". The Citizen bug was 57x; the worst near-tie is 1.5x.
		private const int PoseOutnumberedBy = 4;

		private static async Task<int> NpcPoseCheck(IReadOnlyList<NamedCapture> captures)
		{
			var states = new Dictionary<uint, Dictionary<uint, int>>();
			var idles = new Dictionary<uint, Dictionary<uint, int>>();
			var sightings = 0;

			foreach (var capture in captures)
			{
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					if (!SpawnBodyReader.TryReadCharacterMessage(p.Body, out var read) || read.Message?.npc_stats is not { } npc || !npc.ShouldSerializeTemplateId())
						continue;

					sightings++;
					if (read.Message.Stats?.ShouldSerializeBccState() == true)
						Tally(states, npc.TemplateId, read.Message.Stats.BccState);
					if (read.Message.Bcc?.ShouldSerializeAnimIdleId() == true)
						Tally(idles, npc.TemplateId, read.Message.Bcc.AnimIdleId);
				}
			}

			await using var db = OpenDb();
			var cores = await db
				.NpcStatCores.AsNoTracking()
				.Where(c => c.BccState != null || c.AnimIdleId != null)
				.Select(c => new { c.NpcTemplateId, c.BccState, c.AnimIdleId })
				.ToListAsync();

			var wrong = new List<string>();
			int checkedStates = 0, checkedIdles = 0;
			foreach (var core in cores)
			{
				if (core.BccState is { } state && states.TryGetValue(core.NpcTemplateId, out var seenStates))
				{
					checkedStates++;
					if (Outlier(state, seenStates) is { } why)
						wrong.Add($"template {core.NpcTemplateId} ships bcc_state {state} ({why}); sightings {Format(seenStates)}");
				}

				if (core.AnimIdleId is { } idle && idles.TryGetValue(core.NpcTemplateId, out var seenIdles))
				{
					checkedIdles++;
					if (Outlier(idle, seenIdles) is { } why)
						wrong.Add($"template {core.NpcTemplateId} ships anim_idle_id {idle} ({why}); sightings {Format(seenIdles)}");
				}
			}

			Console.WriteLine($"npc-pose-check over {captures.Count} capture(s): {sightings} NPC sighting(s), " +
				$"{states.Count} template(s) with a captured bcc_state and {idles.Count} with an anim_idle_id.");
			Console.WriteLine($"  shipped cores checked: {checkedStates} bcc_state, {checkedIdles} anim_idle_id " +
				$"(of {cores.Count} carrying one; the rest have no sighting in this corpus).");

			// ⚠️ A type captured in exactly ONE pose says nothing - the gate is about a value its own sightings
			// contradict, so it needs types seen in more than one.
			var mixed = states.Count(kv => kv.Value.Count > 1);
			Console.WriteLine($"  {mixed} template(s) captured in more than one bcc_state - the population the check has teeth over.");

			if (wrong.Count == 0)
			{
				Console.WriteLine("  ✅ no shipped core carries a pose its own sightings outnumber.");
				return 0;
			}

			Console.WriteLine($"  ⚠️ {wrong.Count} shipped stat core(s) carry a pose the type is only rarely caught in:");
			foreach (var w in wrong)
				Console.WriteLine($"    {w}");

			return 1;

			static string? Outlier(uint shipped, Dictionary<uint, int> tally)
			{
				var mine = tally.GetValueOrDefault(shipped);
				if (mine == 0)
					return "never captured on this type";

				var best = tally.OrderByDescending(kv => kv.Value).First();
				return best.Value >= mine * PoseOutnumberedBy
					? $"outnumbered {best.Value}:{mine} by {best.Key}"
					: null;
			}

			static void Tally(Dictionary<uint, Dictionary<uint, int>> into, uint template, uint value)
			{
				var tally = into.TryGetValue(template, out var t) ? t : into[template] = [];
				tally[value] = tally.GetValueOrDefault(value) + 1;
			}

			static string Format(Dictionary<uint, int> tally)
			{
				return string.Join(", ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"));
			}
		}
	}
}
