using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// One member record of the guild roster / joined / update packets (75 bytes + the name):
	//   [u8 0x03][u8 0x03][u32 charId][name s16][u8 rank][u8 online][u32 playfield][u8 level][u32 class]
	//   [u64 lastSeenUnix][48 bytes sparse renown/pvp counters]
	// Playfield is 0 and lastSeen is the last-connection time when offline; both flip when online.
	//
	// ⭐ `lastSeenUnix` is ONE u64, not the `[u32 unknown][u32 lastSeenUnix]` pair this used to carry (#464). The
	// client's `GuildMember_c` reader `FUN_00566740` reads it with `FUN_0048f720` (ReadU64) and passes the result
	// straight to `__ctime64` - it is a 64-bit `time_t`. So the "unknown" word was the HIGH HALF of the stamp, and
	// its being 0 in every captured record is not a fact waiting to be explained: a `time_t` has a zero high word
	// until 2106. #464 filed it as "invariant across the whole corpus - only a client-side CONSUMER can name them",
	// which was right about the method and wrong about there being a field there at all.
	// Byte-exact against the capture's Hoxxling 0x202F record (GuildPacketTests).
	//
	// A SUB-RECORD codec: it is composed into DispatchGuildUpdate (the self pointer AND every roster entry),
	// DispatchGuildCreated (the creator) and DispatchGuildMemberJoined, so it declares no opcode. It moved in from
	// `GuildService` in #530 wave 7 - the three parents used to build it inline through the listener's own stream.
	public static class GuildMemberRecordCodec
	{
		// The two leading bytes every captured record carries: the CoreGuildMember_c streaming version, twice.
		public const byte StreamVersion = 0x03;

		// The trailing counter region. All-zero in every captured record; carried verbatim so a decoded record
		// round-trips whatever retail put there.
		public const int CounterBytes = 48;

		// CANONICAL - the only method here that writes bytes.
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

	// A guild member as the roster packets carry them. `Counters` is the opaque 48-byte tail (see the codec).
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

	// The semantic forms of a GuildMemberRecord, as VALUE factories - a mapper off the DB row, not an encoder.
	public static class GuildMemberRecords
	{
		// A stored character as a roster entry. `playfield` is their live map when online (0 otherwise) and
		// `lastSeenUnix` the last-connection stamp when offline (0 when they are on).
		public static GuildMemberRecord FromCharacter(Character member, bool online, uint playfield, ulong lastSeenUnix)
		{
			ArgumentNullException.ThrowIfNull(member);

			// Level is a byte ON THE WIRE (the row carries a wider one) and the class word is the ConanClass id.
			return new GuildMemberRecord(member.Id, member.Name!, (byte)member.GuildRank!, Convert.ToByte(online), playfield, (byte)member.Level, (uint)member.Class,
				lastSeenUnix, new byte[GuildMemberRecordCodec.CounterBytes]);
		}
	}
}
