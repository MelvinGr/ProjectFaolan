using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Constants
{
	// The guild rank LADDER, per governing form - the names the guild window shows and, more usefully to us, how
	// many rungs each form has. Lifted from the client's own GuildRankDataTable (RDB 1000518): 38 records of
	// [u32 rank][u32 form][u32 limit][u32 permissions][string], the `form` column indexing GuildGoverningForm
	// exactly (values 0..6, seven groups, no gaps). See docs/rdb/client-data-tables.md.
	//
	// ⚠️ THE LADDERS ARE DIFFERENT LENGTHS, which is the whole reason this exists. Stygian has four rungs, Cimmerian
	// and Harem five, the other four six - so "the leader" is rank 3, 4 or 5 depending on the form, and there is no
	// single value that means leader. GuildRank.Leader was a flat 4, proven from the create-guild capture - and that
	// capture's guild was form 6 (its CreateGuild body is `0006 "Faolan" 06 00 00`, i.e. Harem), whose leader index
	// IS 4. One form's ladder had been generalised to all seven.
	//
	// The client carries a permission BITMASK per rung too (strictly nested: 0 subset 30 subset 286 subset 6430
	// subset 7518 subset 0x7FFFFFFF, top rung of every form carrying everything). Not modelled here - we enforce no
	// guild permissions yet - but recorded in the doc so it is not re-derived.
	public static class GuildRankLadder
	{
		// Rung names in rank order, indexed by governing form.
		private static readonly Dictionary<GuildGoverningForm, string[]> Ladders = new Dictionary<GuildGoverningForm, string[]>
		{
			[GuildGoverningForm.Cimmerian] = ["Comrade", "Kinsman", "Elder", "War Chief", "Chieftain"],
			[GuildGoverningForm.Acheron] = ["Blood Initiate", "Sacrificer Apprentice", "Ritualist Acolyte", "Infernal Adept", "Deathbringer Envoy", "Chaos-lord"],
			[GuildGoverningForm.Stygian] = ["Initiate", "Serpent Devotee", "Disciple of Set", "Emissary of Set"],
			[GuildGoverningForm.Aquilonian] = ["Citizen", "Partisan", "Stateman", "Patrician", "Ambassador", "Potentate"],
			[GuildGoverningForm.Nemedian] = ["Aspirant", "Vassal", "Squire", "Knight", "Commandant", "Lord"],
			[GuildGoverningForm.HyborianMilita] = ["Conscript", "Chiliostys", "Phalanx", "Hyparchos", "Taxiarchos", "Strategos"],
			[GuildGoverningForm.Harem] = ["Trollop", "Harlot", "Concubine", "Courtesan", "Madam"]
		};

		// The top rung of `form` - the rank a guild's leader holds. Falls back to the Harem/Cimmerian 4 for a form
		// outside the table, which is what the wire carried before this was per-form.
		public static byte LeaderRank(GuildGoverningForm form)
		{
			return Ladders.TryGetValue(form, out var names)
				? (byte)(names.Length - 1)
				: (byte)GuildRank.Leader;
		}

		// The display name of a rung, or null when the form or the rank is outside the table.
		public static string? NameOf(GuildGoverningForm form, byte rank)
		{
			return Ladders.TryGetValue(form, out var names) && rank < names.Length
				? names[rank]
				: null;
		}

		// How many rungs `form` has.
		public static int RungCount(GuildGoverningForm form)
		{
			return Ladders.TryGetValue(form, out var names)
				? names.Length
				: 0;
		}
	}
}
