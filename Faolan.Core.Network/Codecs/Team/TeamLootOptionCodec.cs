using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     The group's loot rules, as <c>[u32 mode][u8 needGreed][u32 threshold]</c> - 9 bytes, matching the captured
	///     <c>00000000 01 00000002</c> (free-for-all, need/greed on, threshold 2).
	/// </summary>
	/// <remarks>
	///     One body, several methods across two interfaces: the client's c2s <c>TeamSetLootOption(int i_eLootMode, bool
	///     i_bUseNeedGreed, int i_nNewLootThreshold)</c> and the s2c <c>DispatchTeamLootOptionChanged</c> echo carry the
	///     same three fields, so the setting round-trips unchanged to every member. <c>RaidSetLootOption</c> and
	///     <c>DispatchRaidLootOptionChanged</c> are the raid twins, field for field, and the client reads the s2c one with
	///     the same three-field reader. The declared methods are the s2c ones - the messages this codec sends - because
	///     the c2s twins are a different interface's method indices and cannot be spelled in the same enum.
	/// </remarks>
	public static class TeamLootOptionCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
			[CSPlayerInterfaceMethods.DispatchTeamLootOptionChanged, CSPlayerInterfaceMethods.DispatchRaidLootOptionChanged];

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(TeamLootOption value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TeamLootOption Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamLootOptionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TeamLootOption Read(ref SpanReader r)
		{
			return new TeamLootOption(r.ReadU32Be(), r.ReadU8() != 0, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, TeamLootOption v)
		{
			s.WriteUInt32(v.Mode).WriteByte(v.UseNeedGreed).WriteUInt32(v.Threshold);
		}
	}

	/// <summary>The loot rules carried by TeamSetLootOption / DispatchTeamLootOptionChanged.</summary>
	public sealed record TeamLootOption(uint Mode, bool UseNeedGreed, uint Threshold);
}
