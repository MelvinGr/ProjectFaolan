using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     One member record of the guild roster, joined and update packets (75 bytes plus the name):
	///     <code>
	///     [u8 0x03][u8 0x03][u32 charId][name s16][u8 rank][u8 online][u32 playfield][u8 level][u32 class]
	///     [u64 lastSeenUnix][48 bytes sparse renown/pvp counters]
	///     </code>
	/// </summary>
	/// <remarks>
	///     Playfield is 0 and lastSeen is the last-connection time when offline; both flip when online.
	///     <para>
	///         <c>lastSeenUnix</c> is one u64: the client's <c>GuildMember_c</c> reader <c>FUN_00566740</c> reads it with
	///         <c>FUN_0048f720</c> (ReadU64) and passes the result straight to <c>__ctime64</c>, so it is a 64-bit
	///         <c>time_t</c> whose high word is zero until 2106. Byte-exact against the capture's Hoxxling record
	///         (<c>GuildPacketTests</c>).
	///     </para>
	///     <para>
	///         A sub-record codec: it is composed into <c>DispatchGuildUpdate</c> (the self pointer and every roster
	///         entry), <c>DispatchGuildCreated</c> (the creator) and <c>DispatchGuildMemberJoined</c>, so it declares no
	///         opcode.
	///     </para>
	/// </remarks>
	public static class GuildMemberRecordCodec
	{
		// The two leading bytes every captured record carries: the CoreGuildMember_c streaming version, twice.
		//
		// The second one is a real version and the client switches on it (`FUN_00566740`): cases 0 and 2 read one byte and
		// stop, case 1 reads one byte and raises a flag, and case 3 is the full form - the name, rank, online, playfield,
		// level, class, the u64 time and the counter region below. The 0x03 is therefore what selects everything this codec
		// writes; a record sent under any other version is a different, shorter shape. Retail sends 3 in every captured
		// record.
		public const byte StreamVersion = 0x03;

		// The trailing counter region. All-zero in every captured record; carried verbatim so a decoded record round-trips
		// whatever retail put there.
		public const int CounterBytes = 48;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(GuildMemberRecord value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildMemberRecord Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildMemberRecordCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildMemberRecord Read(ref SpanReader r)
		{
			r.ExpectU8(StreamVersion, "guild member stream version");
			r.ExpectU8(StreamVersion, "guild member stream version (second)");

			return new GuildMemberRecord(r.ReadU32Be(), r.ReadStringU16Length(), r.ReadU8(), r.ReadU8(), r.ReadU32Be(), r.ReadU8(), r.ReadU32Be(), r.ReadU64Be(),
				r.ReadBytes(CounterBytes).ToArray());
		}

		internal static void Write(FaolanStream s, GuildMemberRecord v)
		{
			s
				.WriteByte(StreamVersion)
				.WriteByte(StreamVersion)
				.WriteUInt32(v.CharacterId)
				.WriteString(v.Name)
				.WriteByte(v.Rank)
				.WriteByte(v.Online)
				.WriteUInt32(v.Playfield)
				.WriteByte(v.Level)
				.WriteUInt32(v.Class)
				.WriteUInt64(v.LastSeenUnix)
				.WriteArray(v.Counters);
		}
	}

	/// <summary>
	///     A guild member as the roster packets carry them. <c>Counters</c> is the opaque 48-byte tail.
	/// </summary>
	public sealed record GuildMemberRecord(
		uint CharacterId,
		string Name,
		byte Rank,
		byte Online,
		uint Playfield,
		byte Level,
		uint Class,
		ulong LastSeenUnix,
		byte[] Counters);

	/// <summary>
	///     The semantic forms of a <see cref="GuildMemberRecord" />, as value factories - a mapper off the DB row rather
	///     than an encoder.
	/// </summary>
	public static class GuildMemberRecords
	{
		// A stored character as a roster entry. `playfield` is their live map when online (0 otherwise) and `lastSeenUnix`
		// the last-connection stamp when offline (0 when they are on).
		public static GuildMemberRecord FromCharacter(Character member, bool online, uint playfield, ulong lastSeenUnix)
		{
			ArgumentNullException.ThrowIfNull(member);

			// Level is a byte on the wire (the row carries a wider one) and the class word is the ConanClass id.
			return new GuildMemberRecord(member.Id, member.Name!, (byte)member.GuildRank!, Convert.ToByte(online), playfield, (byte)member.Level, (uint)member.Class,
				lastSeenUnix, new byte[GuildMemberRecordCodec.CounterBytes]);
		}
	}
}
