using System;

namespace Faolan.Core.Enums
{
	// What a rung of a guild's rank ladder is ALLOWED to do — the bitmask the client ships per rung in RDB 1000518
	// (`GuildRankDataTable`'s `permissions` word; see `Faolan.Core.Constants.GuildRankLadder` and
	// docs/rdb/client-data-tables.md).
	//
	// ⭐ The bit VALUES are the client's own, not ours: the guild-management view builds its permission list by
	// pairing each bit with the LDB token that labels it, and the constant it hands the `GuildPermission` object IS
	// the bit. So `CanWithdrawMoney = 0x40` is read off the same code path that draws the checkbox.
	// [V1: tools/ghidra/DumpGuildRankPermissions.java]
	//
	// ⚠️ The 12 below are the ones the management view RENDERS. The shipped masks also set `0x20` and, on the top
	// rung, everything up to `0x7FFFFFFF` — bits no rung-permission checkbox names, so they are rights the window
	// does not expose rather than padding. Do not read an unnamed bit as unused.
	[Flags]
	public enum GuildPermission : uint
	{
		None = 0,

		DisbandGuild = 0x001,
		InviteOthers = 0x002,
		KickOthers = 0x004,
		Promote = 0x008,
		Demote = 0x010,
		WithdrawMoney = 0x040,
		ChangeGoverningForm = 0x080,
		ChangeMotd = 0x100,
		ChangeName = 0x200,
		ChangeRecruitmentMode = 0x400,
		ChangeVulnerabilityWindow = 0x800
	}
}
