using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildUpdate</c> - the whole guild state, pushed on login for a guilded character and again whenever
	///     the roster changes.
	/// </summary>
	/// <remarks>
	///     Three arguments, each with its own "stream bad after '&lt;arg&gt;'" abort in the client:
	///     <c>i_pcGuild</c> is the CoreGuild header (<see cref="GuildHeaderCodec" />), <c>i_pcMember</c> is the
	///     authenticated member's record, and <c>i_cMemberList</c> is the full roster, count-framed.
	///     <para>
	///         The roster must include every member, the viewing player included: it fills the client's member std::map
	///         (CoreGuild+0x1e4 node pointer / +0x1e8 count). <c>i_pcMember</c> is a separate self-pointer rather than a
	///         map entry, so excluding self leaves the map empty, and the guild-management panel then divides the summed
	///         renown by the map size (FUN_0176d4a0) - an integer divide by zero the instant the panel opens.
	///     </para>
	///     <para>
	///         The list count is a real count frame, not a "self contribution" counter: CoreGuild's list reader
	///         (FUN_0134ed30) asserts it is a multiple of the frame unit. Retail's word for a 173-member roster is the
	///         frame for 174.
	///     </para>
	/// </remarks>
	public static class GuildFullUpdateCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildUpdate;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(GuildFullUpdate value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildFullUpdate Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildFullUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildFullUpdate Read(ref SpanReader r)
		{
			var guild = GuildHeaderCodec.Read(ref r);
			var self = GuildMemberRecordCodec.Read(ref r);

			var count = r.ReadListCount();
			var roster = new List<GuildMemberRecord>(count);
			for (var i = 0; i < count; i++)
				roster.Add(GuildMemberRecordCodec.Read(ref r));

			return new GuildFullUpdate(guild, self, roster);
		}

		internal static void Write(FaolanStream s, GuildFullUpdate v)
		{
			GuildHeaderCodec.Write(s, v.Guild);
			GuildMemberRecordCodec.Write(s, v.Self);

			s.WriteUInt32(ListFrame.ForCount(v.Roster.Count));
			foreach (var member in v.Roster)
				GuildMemberRecordCodec.Write(s, member);
		}
	}

	/// <summary>
	///     DispatchGuildUpdate's three arguments. <c>Roster</c> is the whole membership and <c>Self</c> the authenticated
	///     member.
	/// </summary>
	public sealed record GuildFullUpdate(GuildHeader Guild, GuildMemberRecord Self, IReadOnlyList<GuildMemberRecord> Roster);
}
