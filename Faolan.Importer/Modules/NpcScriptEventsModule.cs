using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// The scripted events an NPC's dialogue fires (#839) - retail's `SendScryEvent` GameAction, harvested per creature.
	// See NpcScriptEvent for what they are and why they are keyed to the creature rather than to a conversation node.
	//
	// Its own module for the reason `npc-disposition` is: it attaches to NpcTemplates that already exist and nothing
	// else depends on it, so it re-runs against a new corpus on its own.
	public sealed class NpcScriptEventsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "The SendScryEvent scripts an NPC's dialogue fires - `Fight`, `SpawnScavenger`, ... (#839).";
		public string Name => "npc-script-events";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var seen = ctx
				.Captures.SelectMany(c => NpcScriptEventExtractor.Extract(c.Packets))
				.GroupBy(e => (e.TemplateId, e.Event))
				.Select(g => (g.Key.TemplateId, g.Key.Event, Name: g.First().Name, Sightings: g.Count()))
				.OrderBy(e => e.TemplateId)
				.ThenBy(e => e.Event)
				.ToList();

			if (seen.Count == 0)
				return new ImportResult("no SendScryEvent records in these captures - nothing to harvest.");

			// Only for types we actually hold: the FK is to NpcTemplates, and an event on a creature we never imported
			// is a dangling row rather than a fact we can use.
			var known = await ctx.Db.NpcTemplates.Select(t => t.TemplateId).ToHashSetAsync();
			var existing = await ctx.Db.NpcScriptEvents.ToListAsync();
			var have = existing.Select(e => (e.NpcTemplateId, e.Event)).ToHashSet();

			if (ctx.ModeFor(Name) == WriteMode.Overwrite && existing.Count > 0)
			{
				ctx.Db.NpcScriptEvents.RemoveRange(existing);
				have.Clear();
			}

			var added = 0;
			var unknownType = 0;
			foreach (var (template, name, npcName, sightings) in seen)
			{
				if (!known.Contains(template))
				{
					unknownType++;
					continue;
				}

				if (!have.Add((template, name)))
					continue;

				ctx.Db.NpcScriptEvents.Add(new NpcScriptEvent
				{
					NpcTemplateId = template,
					Event = name
				});
				added++;
			}

			// ⭐ The ACTION overlay - the one thing about a scry event that is NOT harvested. Retail states the event
			// STRING and nothing else, so what the server does about it is authored here, and this table IS the
			// plug-in point: a future quest whose dialogue fires `RunToKhemi` or `SpawnBandits` is wired by adding a
			// line below, with no change to the dialogue runtime.
			//
			// BOOTSTRAP-ONLY, like `Chain` in QuestGoalWiring: only an event still sitting at `None` is stamped, so a
			// hand edit (or a re-run after someone mapped one) is never clobbered. An event that is not listed stays
			// `None` and LOGS when it fires - which is the honest state for `SpawnSheep`, `AriasMageLeave`,
			// `ProtectiveSpell` and `knubotconarchdrunk`, none of which we can act on yet.
			var actions = new Dictionary<string, NpcScriptAction>(System.StringComparer.OrdinalIgnoreCase)
			{
				["Fight"] = NpcScriptAction.TurnHostile,          // Saddur turns on the player
				["RunToTortage"] = NpcScriptAction.LeaveWorld,    // Casilda's escort is over - she heads for the gate
				["SpawnScavenger"] = NpcScriptAction.RevealQuestTargets // ...and Scavenger Tuut enters the world
			};

			var mapped = 0;
			foreach (var row in await ctx.Db.NpcScriptEvents.ToListAsync())
			{
				if (row.Action != NpcScriptAction.None || !actions.TryGetValue(row.Event, out var action))
					continue;

				row.Action = action;
				mapped++;
			}

			await ctx.Db.SaveChangesAsync();

			// Print the roster: seven distinct strings over the whole corpus, so the whole harvest fits on screen and
			// an unimplemented one is visible rather than buried in a count.
			var actionOf = await ctx.Db.NpcScriptEvents.ToDictionaryAsync(e => (e.NpcTemplateId, e.Event), e => e.Action);
			foreach (var (template, name, npcName, sightings) in seen)
				System.Console.WriteLine($"  {npcName} ({template}) fires \"{name}\" x{sightings} -> {actionOf.GetValueOrDefault((template, name))}");

			var skipped = unknownType > 0
				? $", {unknownType} on a creature type we do not hold"
				: "";
			return new ImportResult($"{added} script event(s) harvested across {seen.Select(e => e.TemplateId).Distinct().Count()} creature(s), {mapped} newly mapped to an action{skipped}.");
		}
	}
}
