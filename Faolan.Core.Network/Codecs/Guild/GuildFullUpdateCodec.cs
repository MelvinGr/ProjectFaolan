using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildUpdate (0x203F) - the whole guild state, pushed on login for a guilded character and again whenever
	// the roster changes. Three arguments, each with its own "stream bad after '<arg>'" abort in the client:
	//   i_pcGuild     = the CoreGuild header (GuildHeaderCodec)
	//   i_pcMember    = the authenticated member's record
	//   i_cMemberList = [u32 (count+1)*0x3F1][member records]      the FULL roster, count-framed
	//
	// ⚠️ The roster must include EVERY member, the viewing player included - it fills the client's member std::map
	// (CoreGuild+0x1e4 node ptr / +0x1e8 _Mysize count). `i_pcMember` is a separate self-POINTER, not a map entry, so
	// excluding self left the map empty; the guild-management panel then divides the summed renown by the map size
	// (FUN_0176d4a0), and a 0 count is an integer-divide-by-zero crash the instant the panel opens.
	//
	// ⚠️ The list count is a real 0x3F1 count, not a "self contribution" counter - CoreGuild's list reader
	// (FUN_0134ed30) asserts it is a multiple of 0x3F1. Retail's word for a 173-member roster was 0x2ADCE = 174*0x3F1.
	//
	// Moved in from `GuildService.SendGuildFullUpdate` in #530 wave 7 - the service kept the fan-out and the send.
	public static class GuildFullUpdateCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildUpdate;

		// CANONICAL - the only method here that writes bytes.
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

	// DispatchGuildUpdate's three arguments. `Roster` is the whole membership, `Self` the authenticated member.
	public sealed record GuildFullUpdate(GuildHeader Guild, GuildMemberRecord Self, IReadOnlyList<GuildMemberRecord> Roster);
}
