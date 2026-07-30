using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// Byte-exact codec for the 0x205C DispatchGuildSearchResult body - the guild-finder browse list the client
	// requests with 0x2043 SearchForGuilds. Decoded from the hoxxling login capture (GuildSearchResultTests):
	//   [u32 (guildCount+1)*0x3F1][ guild records... ][u8 0x01]
	//   record = [u32 guildId][s16 name][s16 leaderName][u16 memberCount][u32 renown][u8 flag]
	// The leading word is the shared 0x3F1 count-framing (ListFrame). The trailing 0x01 closes the list (a "more
	// results" / end marker - constant in the capture). We ignore the 0x2043 filter and return the realm's guilds.
	public static class GuildSearchResultCodec
	{
		public const ushort Opcode = (ushort)CSPlayerInterfaceOpcodes.DispatchGuildSearchResult;

		private const byte Trailer = 0x01;

		public static byte[] Build(GuildSearchResult value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Guilds.Count));
			foreach (var g in value.Guilds)
			{
				s.WriteUInt32(g.Id).WriteString(g.Name).WriteString(g.LeaderName);
				s.WriteUInt16(g.MemberCount).WriteUInt32(g.Renown).WriteByte(g.Flag);
			}

			return s.WriteByte(Trailer).ToArray();
		}

		public static GuildSearchResult Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildSearchResultCodec));
			var count = r.ReadListCount();
			var guilds = new List<GuildSearchEntry>(count);
			for (var i = 0; i < count; i++)
				guilds.Add(new GuildSearchEntry(r.ReadU32Be(), r.ReadStringU16Length(), r.ReadStringU16Length(), r.ReadU16Be(), r.ReadU32Be(), r.ReadU8()));

			r.ReadU8(); // trailer
			r.EnsureAtEnd();

			return new GuildSearchResult(guilds);
		}
	}

	public sealed record GuildSearchResult(IReadOnlyList<GuildSearchEntry> Guilds);

	// One guild row. Renown is the guild's total renown; flag is an unclassified per-guild byte (0/1 in the
	// capture - likely a recruiting flag), emitted as 0 for our guilds.
	public readonly record struct GuildSearchEntry(uint Id, string Name, string LeaderName, ushort MemberCount, uint Renown, byte Flag);
}
