using System.Collections.Generic;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// Byte-exact codec for the 0x205C DispatchGuildSearchResult body - the guild-finder browse list the client
	// requests with 0x2043 SearchForGuilds. Decoded from the hoxxling login capture (GuildSearchResultTests):
	//   [u32 (guildCount+1)*0x3F1][ guild records... ][u8 0x01]
	//   record = [u32 guildId][s16 name][s16 leaderName][u16 memberCount][u32 renown][u8 flag]
	// The leading word is the shared 0x3F1 count-framing (see GuildRankings). The trailing 0x01 closes the list
	// (a "more results" / end marker - constant in the capture). We ignore the 0x2043 filter and return the realm's
	// guilds.
	public sealed class GuildSearchResult
	{
		public const byte Trailer = 0x01;

		// The CSPlayerInterface opcode this body is the payload of (0x205C).
		public const CSPlayerInterfaceOpcodes Opcode = CSPlayerInterfaceOpcodes.DispatchGuildSearchResult;

		public List<Entry> Guilds { get; set; } = [];

		public byte[] Encode()
		{
			var s = new FaolanStream();
			s.WriteUInt32(GuildRankings.ListWord(Guilds.Count));
			foreach (var g in Guilds)
			{
				s.WriteUInt32(g.Id).WriteString(g.Name).WriteString(g.LeaderName);
				s.WriteUInt16(g.MemberCount).WriteUInt32(g.Renown).WriteByte(g.Flag);
			}

			s.WriteByte(Trailer);
			return s.ToArray();
		}

		public static GuildSearchResult Decode(byte[] body)
		{
			var s = new FaolanStream(body);
			var result = new GuildSearchResult();

			var count = GuildRankings.ListCount(s.ReadUInt32());
			for (var i = 0; i < count; i++)
			{
				result.Guilds.Add(new Entry(s.ReadUInt32(), s.ReadString(), s.ReadString(), s.ReadUInt16(), s.ReadUInt32(), s.ReadByte()));
			}

			s.ReadByte(); // trailer
			return result;
		}

		// One guild row. Renown is the guild's total renown; flag is an unclassified per-guild byte (0/1 in the
		// capture - likely a recruiting flag), emitted as 0 for our guilds.
		public readonly record struct Entry(uint Id, string Name, string LeaderName, ushort MemberCount, uint Renown, byte Flag);
	}
}
