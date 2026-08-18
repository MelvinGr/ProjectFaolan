using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.GameServer.Combat;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report npc-aggression`: which hostile-COLOURED templates the #822 rule stops from attacking on sight.
	//
	// Retail colours some civilians hostile without them ever fighting - the Town Crier (14572) is `cCategory = 1`
	// unanimously and never once attacks across a whole session. Reading one flag as both the name colour and the
	// aggro decision is what made him attack, and `NpcWeapon.Initiates` is the split: an ARMED humanoid engages, an
	// unarmed one is a civilian, and anything non-humanoid needs no weapon to mean it.
	//
	// ⭐ A report and not a test, per #729: the population is shipped DATA and it moves with the NPC harvest. What is
	// gated in the suite is the RULE (`NpcAggressionTests`); what is printed here is who it currently selects, so a
	// harvest that pacifies something it should not is visible rather than discovered in-game.
	public static partial class ReportCli
	{
		private static async Task<int> NpcAggression()
		{
			await using var db = OpenDb();

			var hostile = await db.NpcStatCores.AsNoTracking()
				.Where(c => c.Disposition == 1)
				.Select(c => new
				{
					c.NpcTemplateId,
					c.Level
				})
				.ToListAsync();

			var names = await db.NpcTemplates.AsNoTracking().ToDictionaryAsync(t => t.TemplateId, t => t.Name);

			// One spawn per template is enough to answer both halves - the kit and whether it carries morphs - and
			// every spawn of a template carries the same answer for these two.
			var spawns = await db.NpcSpawns.AsNoTracking()
				.Include(s => s.Equipment)
				.ThenInclude(e => e.Item)
				.Include(s => s.Morphs)
				.ToListAsync();
			var byTemplate = spawns.GroupBy(s => s.NpcTemplateId).ToDictionary(g => g.Key, g => g.ToList());

			var pacified = 0;
			var lines = new List<string>();
			foreach (var t in hostile.OrderBy(t => names.GetValueOrDefault(t.NpcTemplateId, "")))
			{
				if (!byTemplate.TryGetValue(t.NpcTemplateId, out var rows))
					continue; // no captured placement - nothing to read a kit off, so the loader never sees it either

				// ANY placement being armed is enough: a template with one armed outfit is an armed template, and
				// treating it otherwise would pacify a mob because one of its captured spawns was caught undressed.
				var humanoid = rows.Any(r => r.Morphs.Count > 0);
				var initiates = rows.Any(r => NpcWeapon.Initiates(r.Equipment, humanoid));
				if (initiates)
					continue;

				pacified++;
				lines.Add($"  {t.NpcTemplateId,-8} level {t.Level,-3} {names.GetValueOrDefault(t.NpcTemplateId, "?")}");
			}

			Console.WriteLine($"=== hostile-coloured templates the #822 rule leaves PASSIVE ===");
			Console.WriteLine($"    {pacified} of {hostile.Count} hostile template(s); the rest still engage on sight.");
			Console.WriteLine();
			foreach (var line in lines)
				Console.WriteLine(line);

			Console.WriteLine();
			Console.WriteLine("  ⚠️ Every name here should read as a CIVILIAN. A mob in this list means the rule has over-reached -");
			Console.WriteLine("     most likely a template whose weapon was never harvested, which reads as unarmed.");
			Console.WriteLine("  ⚠️ These are unprovoked, NOT inert: retaliation is gated on Hostile, not on this, so attacking one");
			Console.WriteLine("     still starts a fight.");
			return 0;
		}
	}
}
