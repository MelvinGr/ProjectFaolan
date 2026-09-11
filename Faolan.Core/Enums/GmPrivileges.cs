using System;

namespace Faolan.Core.Enums
{
	/// <summary>
	///     The retail client's GM privileges. <c>gm_level</c> is a bitmask, not a level - character stat
	///     <c>0x0D7</c> (<see cref="CharacterStatId.GmLevel" />) and protobuf <c>player_stats.gm_level</c> carry the set
	///     of privileges the account holds, and a GM command is offered when
	///     <c>(gmLevel &amp; command.requiredMask) != 0</c> (a command whose mask is 0 is unrestricted).
	/// </summary>
	/// <remarks>
	///     Both halves are recovered from the client. The names and bit values are registered by
	///     <c>FanaticGMLevelCommand_c</c>'s constructor (Ghidra <c>FUN_006336d0</c>) - the object behind the <c>/gm</c>
	///     command - as a literal name-to-bit table. The AND is the filter in the <c>/help</c> renderer
	///     (<c>FUN_006c86b0</c>), which reads <c>GetStat(0xD7, 0)</c> once and tests it against each registered command's
	///     mask word before listing it.
	///     <para>
	///         The command surface is not tiered, so do not read the mask as a magnitude: at <c>gm_level = 10</c>
	///         (<c>PlayerResurrect | TeleportDynels</c>) <c>/kill</c> is refused not because 10 is too low but because
	///         10 does not contain <see cref="NpcCommands" />, which is what <c>/kill</c> requires.
	///     </para>
	///     Seven bits are used by the shipped roles but never named by the client
	///     (<c>0x00040000, 0x00200000, 0x00800000, 0x08000000, 0x20000000, 0x40000000, 0x80000000</c>); they are
	///     deliberately absent here rather than guessed at. See docs/subsystems/gm-commands.md.
	/// </remarks>
	[Flags]
	public enum GmPrivileges : uint
	{
		None = 0,

		/// <summary>The debug/development command set - most of the client's ~120 GM commands require only this.</summary>
		Developer = 0x00000001,

		PlayerResurrect = 0x00000002,
		TeleportSelf = 0x00000004,
		TeleportDynels = 0x00000008,
		PlayerDamage = 0x00000010,
		GetStats = 0x00000020,
		SetStats = 0x00000040,
		InvisibleToMonsters = 0x00000080,
		ImmuneToAoe = 0x00000100,
		GodMode = 0x00000200,
		Broadcast = 0x00000400,
		Sitecast = 0x00000800,
		AcgSpawn = 0x00001000,
		AssistSummon = 0x00002000,
		ChangeName = 0x00004000,
		ChangeNick = 0x00008000,

		/// <summary>Renders the GM tag over the character - the tag's text is <c>player_stats.gm_ldb_name</c>.</summary>
		ShowGmTag = 0x00010000,

		EffectCommand = 0x00020000,
		NpcCommands = 0x00080000,

		/// <summary>Permits <c>/cloak</c> - the degree of which is <c>player_stats.gm_cloak_level</c>.</summary>
		CanCloak = 0x00100000,

		CanKick = 0x00400000,
		GmChat = 0x01000000,
		NoCriteriaCheck = 0x02000000,
		CityCheck = 0x04000000,
		HideFromCommunityServer = 0x10000000
	}

	/// <summary>
	///     The named privilege sets the client itself ships, registered alongside the individual bits by
	///     <c>FanaticGMLevelCommand_c</c> (so <c>/gm support2</c> names one). Verbatim values - the composition of each is
	///     Funcom's own policy, not ours, which is why they are recorded as roles rather than rebuilt from flags.
	/// </summary>
	public static class GmRoles
	{
		/// <summary>Every bit set, including the seven the client never names.</summary>
		public const uint All = 0xFFFFFFFF;

		public const uint Dev = 0x09011075;
		public const uint Internal = 0x000BC0EF;
		public const uint Volunteers1 = 0x010123A4;

		/// <summary>The client registers this as a straight copy of <see cref="Volunteers1" />.</summary>
		public const uint Volunteers2 = Volunteers1;

		public const uint Support1 = 0x0941F3A6;

		/// <summary>The client registers this as <c>Support1 | 0x00100C48</c>.</summary>
		public const uint Support2 = Support1 | 0x00100C48;

		public const uint Operations = 0x09410C20;
	}
}
