using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Faolan.Core.Rdb.Assets
{
	// The client's FACTION RELATIONS table (`data/FactionRelations.xml`, RDB type 1010701 id 1) — the whole Khitai
	// reputation ruleset, in plaintext XML, in the shipped client (#310).
	//
	// #310 filed the faction system as *"Blocked on: expansion (Khitai) content + captures of the faction UI/standing
	// packets to decode the wire form — none today."* Both halves are false: the wire shape was recovered from the
	// client (`FactionsList` = a `ListFrame` over `{factionId, points}` pairs), and the CONTENT is this file plus the
	// LDB. Nothing here needs a capture.
	//
	// ⚠️ 1010701 was looked at once before and set aside — `docs/subsystems/combat.md` notes it was "chased for
	// [NPC disposition] and is player REPUTATION in a different id space". That was the right verdict for that
	// question and exactly the wrong thing to conclude for this one: a table that is not what one ticket wanted is
	// still the table another ticket is blocked on.
	//
	// Grammar — two sections, the same element in both:
	//
	//   <faction_relations>   the STANDARD rate: one entry per faction, `id` = the faction itself
	//     <faction_relation id="10" iconid="4576799">        <- Children of Yag-Kosha
	//       <opposing_faction id="10" points="-120" />       <- kill one of theirs: -120 with them
	//       <opposing_faction id="20" points="120" />        <- and +120 with their rival
	//   <override_relations>  the per-ENCOUNTER-TIER rate, `id` = faction + tier
	//
	// So a relation id is `factionBase + tier`, tier 0..5 = solo normal / solo boss / group normal / group boss /
	// raid normal / raid boss, and the file's own header comment states the design: points are proportional to 60 so
	// a 6-person group killing a 360-point mob earns the same 60 each that a solo kill of a 60-point mob does.
	// The ladder (from the shipped values): 120 · 180 · 720 · 1800 · 1440 · 2400.
	//
	// 16 standard factions ship, 12 of which carry the full 5-tier override set; `Ela Shan / Hand of Glory` (200),
	// `Lord Stoll / Pit Masters` PvE/PvP (310/320) and `Clan Vigdis` (330) carry only the standard rate.
	//
	// ⚠️ The <!-- comment --> above each entry is the only place the faction's NAME appears in this file, so it is
	// parsed rather than ignored — but as PROVENANCE, not as identity. The display names come from `LDB[254]`
	// (`60435760 + 16n` with the article, `61205296 + 16n` bare), and the four rank titles per faction from the same
	// category. See docs/subsystems/factions.md.
	public static class FactionRelationTable
	{
		public const uint RdbType = 1010701;
		public const uint RdbId = 1;

		// A faction's base id is a multiple of 10 and its tier is the remainder — `id=13` is faction 10, tier 3.
		public const int FactionIdStep = 10;

		public static FactionRelations Parse(string xml)
		{
			var doc = XDocument.Parse(xml);
			var relations = new List<FactionRelation>();
			var names = new Dictionary<uint, string>();

			foreach (var section in doc.Descendants("faction_relations").Concat(doc.Descendants("override_relations")))
			foreach (var element in section.Elements("faction_relation"))
			{
				var id = (uint?)element.Attribute("id")
				         ?? throw new InvalidOperationException("a <faction_relation> carries no id");

				var faction = id / FactionIdStep * FactionIdStep;
				var tier = (int)(id - faction);

				// The authoring comment immediately before the element: "Children of Yag-Kosha group boss". Only the
				// STANDARD section's comment is a clean faction name; an override's carries the tier too, so the name
				// is taken from the standard entry and an override never overwrites it.
				if (tier == 0 && element.PreviousNode is XComment comment)
					names[faction] = comment.Value.Trim().Replace(" standard", "", StringComparison.Ordinal).Trim();

				var awards = element.Elements("opposing_faction")
					.Select(o => new FactionAward(
						(uint?)o.Attribute("id") ?? throw new InvalidOperationException($"faction_relation {id} has an <opposing_faction> with no id"),
						(int?)o.Attribute("points") ?? throw new InvalidOperationException($"faction_relation {id} has an <opposing_faction> with no points")))
					.ToArray();

				relations.Add(new FactionRelation(id, faction, tier, (uint?)element.Attribute("iconid") ?? 0, awards));
			}

			return new FactionRelations(relations, names);
		}
	}

	// What killing one NPC of `Faction` at encounter `Tier` is worth, per faction it moves. `IconId` is an RDB
	// resource id and is only present on the standard (tier 0) entry.
	public sealed record FactionRelation(uint Id, uint Faction, int Tier, uint IconId, IReadOnlyList<FactionAward> Awards);

	// One line of a kill's reputation payout: `Points` with faction `FactionId` (negative with the victim's own).
	public sealed record FactionAward(uint FactionId, int Points);

	// `Names` is the authoring comment beside each standard entry — provenance for the id, not the display string.
	public sealed record FactionRelations(IReadOnlyList<FactionRelation> Relations, IReadOnlyDictionary<uint, string> Names)
	{
		/// <summary>The 16 factions that carry a standard rate, in id order.</summary>
		public IEnumerable<FactionRelation> Standard => Relations.Where(r => r.Tier == 0).OrderBy(r => r.Faction);

		/// <summary>Every tier of one faction, in tier order.</summary>
		public IEnumerable<FactionRelation> Tiers(uint faction)
		{
			return Relations.Where(r => r.Faction == faction).OrderBy(r => r.Tier);
		}
	}
}
