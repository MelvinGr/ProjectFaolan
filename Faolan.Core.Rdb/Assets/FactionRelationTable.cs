using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     The client's faction relations table (<c>data/FactionRelations.xml</c>, RDB type 1010701 id 1): the whole
	///     Khitai reputation ruleset, shipped as plaintext XML. See docs/subsystems/factions.md.
	/// </summary>
	/// <remarks>
	///     Two sections carry the same element:
	///     <code>
	///     &lt;faction_relations&gt;   the standard rate: one entry per faction, id = the faction itself
	///       &lt;faction_relation id="10" iconid="4576799"&gt;    Children of Yag-Kosha
	///         &lt;opposing_faction id="10" points="-120" /&gt;   kill one of theirs: -120 with them
	///         &lt;opposing_faction id="20" points="120" /&gt;    and +120 with their rival
	///     &lt;override_relations&gt;  the per-encounter-tier rate, id = faction + tier
	///     </code>
	///     A relation id is therefore <c>factionBase + tier</c>, where tiers 0 to 5 are solo normal, solo boss, group
	///     normal, group boss, raid normal and raid boss. The file's own header comment states the design: points are
	///     proportional to 60, so a six-person group killing a 360-point mob earns the same 60 each that a solo kill of
	///     a 60-point mob does. The shipped ladder is 120, 180, 720, 1800, 1440, 2400.
	///     <para>
	///         16 standard factions ship, 12 of them with the full five-tier override set. Ela Shan / Hand of Glory
	///         (200), Lord Stoll / Pit Masters PvE and PvP (310/320) and Clan Vigdis (330) carry only the standard
	///         rate.
	///     </para>
	///     <para>
	///         The XML comment above each entry is the only place the faction's name appears in this file, so it is
	///         parsed rather than ignored — but as provenance, not as identity. The display names come from
	///         <c>LDB[254]</c> (<c>60435760 + 16n</c> with the article, <c>61205296 + 16n</c> bare), as do the four
	///         rank titles per faction.
	///     </para>
	/// </remarks>
	public static class FactionRelationTable
	{
		public const uint RdbType = 1010701;
		public const uint RdbId = 1;

		/// <summary>
		///     A faction's base id is a multiple of 10 and its tier is the remainder: id 13 is faction 10, tier 3.
		/// </summary>
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

				// The authoring comment immediately before the element reads like "Children of Yag-Kosha group boss".
				// Only the standard section's comment is a clean faction name - an override's carries the tier too -
				// so the name is taken from the standard entry and an override never overwrites it.
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

	/// <summary>
	///     What killing one NPC of <c>Faction</c> at encounter <c>Tier</c> is worth, per faction it moves.
	///     <c>IconId</c> is an RDB resource id and is only present on the standard (tier 0) entry.
	/// </summary>
	public sealed record FactionRelation(uint Id, uint Faction, int Tier, uint IconId, IReadOnlyList<FactionAward> Awards);

	/// <summary>
	///     One line of a kill's reputation payout: <c>Points</c> with faction <c>FactionId</c>, negative with the
	///     victim's own faction.
	/// </summary>
	public sealed record FactionAward(uint FactionId, int Points);

	/// <summary>
	///     The parsed table. <c>Names</c> holds the authoring comment beside each standard entry, which is provenance
	///     for the id rather than a display string.
	/// </summary>
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
