using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Constants
{
	// The guild rank ladder, per governing form - the names the guild window shows, how many rungs each form has,
	// and what each rung is allowed to do. Lifted from the client's own GuildRankDataTable (RDB 1000518): 38 records
	// of [u32 rank][u32 form][u32 limit][u32 permissions][string], the `form` column indexing GuildGoverningForm
	// exactly (values 0..6, seven groups, no gaps). See docs/rdb/client-data-tables.md.
	//
	// The ladders are different lengths. Stygian has four rungs, Cimmerian and Harem five, the other four six - so
	// "the leader" is rank 3, 4 or 5 depending on the form, and there is no single value that means leader.
	// GuildRank.Leader (4) is only the Harem/Cimmerian leader index (the create-guild capture's CreateGuild body is
	// `0006 "Faolan" 06 00 00`, i.e. form 6, Harem).
	//
	// The permission column is the client's answer to "which rungs may withdraw / kick / promote". Its bits are
	// named by `GuildPermission`, recovered from the guild-management view that renders them, so the masks below
	// are transcribed from the shipped table and not a policy of ours. The shape they give:
	//
	//   - the top two rungs may withdraw from the bank on six forms of seven; on Stygian only the leader may,
	//     because its second rung carries 6430 (which lacks `WithdrawMoney`) rather than 7518;
	//   - invite/kick/promote/demote reach one to two rungs further down (masks 30 / 286 / 6430 all carry them);
	//   - the top rung of every form carries `0x7FFFFFFF`, i.e. everything.
	public static class GuildRankLadder
	{
		// One rung: the name the guild window shows and the permission mask the client ships for it. The `limit`
		// column of the same table (a per-rank member cap) is not carried here - nothing enforces caps, and it is
		// recorded in the doc.
		public readonly record struct Rung(string Name, GuildPermission Permissions);

		private const GuildPermission Everything = (GuildPermission)0x7FFFFFFF;

		// The masks the shipped table actually uses, strictly nested: 0 ⊂ 30 ⊂ 286 ⊂ 6430 ⊂ 7518 ⊂ 0x7FFFFFFF. Named
		// here so a ladder below reads as "which tier of rights", which is what the table is expressing - the raw
		// numbers say nothing at the call site, and two of them differ by a single bit.
		private const GuildPermission Officer = // 30
			GuildPermission.InviteOthers | GuildPermission.KickOthers | GuildPermission.Promote | GuildPermission.Demote;

		private const GuildPermission OfficerMotd = Officer | GuildPermission.ChangeMotd; // 286

		// 6430 and 7518 both carry an unnamed 0x1000; they differ by WithdrawMoney and ChangeRecruitmentMode.
		private const GuildPermission Deputy = OfficerMotd | GuildPermission.ChangeVulnerabilityWindow | (GuildPermission)0x1000; // 6430

		private const GuildPermission DeputyBanker = Deputy | GuildPermission.WithdrawMoney | GuildPermission.ChangeRecruitmentMode; // 7518

		// Rungs in rank order, indexed by governing form.
		private static readonly Dictionary<GuildGoverningForm, Rung[]> Ladders = new Dictionary<GuildGoverningForm, Rung[]>
		{
			[GuildGoverningForm.Cimmerian] =
			[
				new Rung("Comrade", GuildPermission.None), new Rung("Kinsman", GuildPermission.None), new Rung("Elder", GuildPermission.None),
				new Rung("War Chief", DeputyBanker), new Rung("Chieftain", Everything)
			],
			[GuildGoverningForm.Acheron] =
			[
				new Rung("Blood Initiate", GuildPermission.None), new Rung("Sacrificer Apprentice", GuildPermission.None),
				new Rung("Ritualist Acolyte", GuildPermission.None), new Rung("Infernal Adept", Deputy),
				new Rung("Deathbringer Envoy", Everything), new Rung("Chaos-lord", Everything)
			],
			[GuildGoverningForm.Stygian] =
			[
				new Rung("Initiate", GuildPermission.None), new Rung("Serpent Devotee", GuildPermission.None),
				new Rung("Disciple of Set", Deputy), new Rung("Emissary of Set", Everything)
			],
			[GuildGoverningForm.Aquilonian] =
			[
				new Rung("Citizen", GuildPermission.None), new Rung("Partisan", GuildPermission.None), new Rung("Stateman", GuildPermission.None),
				new Rung("Patrician", Officer), new Rung("Ambassador", DeputyBanker), new Rung("Potentate", Everything)
			],
			[GuildGoverningForm.Nemedian] =
			[
				new Rung("Aspirant", GuildPermission.None), new Rung("Vassal", GuildPermission.None), new Rung("Squire", GuildPermission.None),
				new Rung("Knight", OfficerMotd), new Rung("Commandant", DeputyBanker), new Rung("Lord", Everything)
			],
			[GuildGoverningForm.HyborianMilita] =
			[
				new Rung("Conscript", GuildPermission.None), new Rung("Chiliostys", GuildPermission.None), new Rung("Phalanx", GuildPermission.None),
				new Rung("Hyparchos", Officer), new Rung("Taxiarchos", DeputyBanker), new Rung("Strategos", Everything)
			],
			[GuildGoverningForm.Harem] =
			[
				new Rung("Trollop", GuildPermission.None), new Rung("Harlot", GuildPermission.None), new Rung("Concubine", GuildPermission.None),
				new Rung("Courtesan", DeputyBanker), new Rung("Madam", Everything)
			]
		};

		// The top rung of `form` - the rank a guild's leader holds. Falls back to the Harem/Cimmerian 4
		// (GuildRank.Leader) for a form outside the table.
		public static byte LeaderRank(GuildGoverningForm form)
		{
			return Ladders.TryGetValue(form, out var rungs)
				? (byte)(rungs.Length - 1)
				: (byte)GuildRank.Leader;
		}

		// The display name of a rung, or null when the form or the rank is outside the table.
		public static string? NameOf(GuildGoverningForm form, byte rank)
		{
			return Ladders.TryGetValue(form, out var rungs) && rank < rungs.Length
				? rungs[rank].Name
				: null;
		}

		// How many rungs `form` has.
		public static int RungCount(GuildGoverningForm form)
		{
			return Ladders.TryGetValue(form, out var rungs)
				? rungs.Length
				: 0;
		}

		// What `rank` may do on `form`'s ladder. A form or a rank outside the table gets nothing rather than a
		// default set: an unknown rung is not evidence of a right, and the caller is deciding whether to let someone
		// drain a guild bank.
		public static GuildPermission PermissionsOf(GuildGoverningForm form, byte rank)
		{
			return Ladders.TryGetValue(form, out var rungs) && rank < rungs.Length
				? rungs[rank].Permissions
				: GuildPermission.None;
		}

		// Does `rank` on `form`'s ladder hold `permission`?
		public static bool Allows(GuildGoverningForm form, byte rank, GuildPermission permission)
		{
			return (PermissionsOf(form, rank) & permission) == permission;
		}
	}
}
