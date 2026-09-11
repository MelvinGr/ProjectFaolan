using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     The <c>DispatchGuildSearchResult</c> body - the guild-finder browse list the client requests with
	///     <c>SearchForGuilds</c>.
	/// </summary>
	/// <remarks>
	///     Decoded from the hoxxling login capture (<c>GuildSearchResultTests</c>):
	///     <code>
	///     [list frame][ guild records... ][u8 0x01]
	///     record = [u32 guildId][s16 name][s16 leaderName][u16 memberCount][u32 renown][u8 flag]
	///     </code>
	///     The leading word is the shared count framing (<c>ListFrame</c>), and the trailing 0x01 closes the list - a
	///     "more results" or end marker, constant in the capture. The request's filter is ignored and the realm's guilds
	///     are returned.
	/// </remarks>
	public static class GuildSearchResultCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildSearchResult;

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

	/// <summary>
	///     One guild row. <c>Renown</c> is the guild's total renown; <c>Flag</c> is an unclassified per-guild byte (0 or 1
	///     in the capture, most likely a recruiting flag), emitted as 0 for guilds built here.
	/// </summary>
	public readonly record struct GuildSearchEntry(uint Id, string Name, string LeaderName, ushort MemberCount, uint Renown, byte Flag);
}
