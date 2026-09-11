using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     The 277-byte renown/feat/city-stats block at the tail of the guild full update.
	/// </summary>
	/// <remarks>
	///     The block closes the <c>i_pcGuild</c> argument read by <c>CSPlayerInterface::DispatchGuildUpdate</c> into
	///     <c>CoreGuild_c::Read</c> (FUN_00565900, streaming version 3) for the <c>govForm == 1</c> form (case 1 falling
	///     through case 3, the form guilds created here use). The full reader walk - version, guildId, govForm, cash,
	///     three strings, then this block - consumes the whole argument and lands exactly on the following member record
	///     (tools/capture/decode_guild_renown.py --walk, validated against both full-update snapshots in the guild_invite
	///     capture); the only byte that differs between those snapshots is <c>RenownCounters[1]</c>, the renown total that
	///     ticked. The two embedded lists use the shared count framing (<c>ListFrame</c>).
	///     <para>
	///         A sub-record codec: the block is always composed into a larger guild body, so it declares no opcode. Its
	///         two forms differ in the leading flag and the stat-id list, and therefore in length too - 277 bytes for the
	///         full update, 225 for the create-time form - which is why <c>Parse</c> reads the lists rather than assuming
	///         a size.
	///     </para>
	/// </remarks>
	public static class GuildRenownBlockCodec
	{
		// The full-update form's length. The create-time form, with an empty stat-id list, is 52 bytes shorter.
		public const int Length = 277;

		public static byte[] Build(GuildRenownBlock value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildRenownBlock Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildRenownBlockCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildRenownBlock Read(ref SpanReader r)
		{
			return new GuildRenownBlock
			{
				Flag = r.ReadU8(),
				StatIds = ReadList(ref r),
				BoolA = r.ReadU8(),
				BoolB = r.ReadU8(),
				Group1 = ReadU32s(ref r, 3),
				BoolC = r.ReadU8(),
				BoolD = r.ReadU8(),
				Group2 = ReadU32s(ref r, 2),
				List2 = ReadList(ref r),
				Group3 = ReadU32s(ref r, 6),
				RenownCounters = ReadU64s(ref r, 6),
				Group4 = ReadU32s(ref r, 2),
				Counters64B = ReadU64s(ref r, 8),
				Counters64C = ReadU64s(ref r, 3),
				Group5 = r.ReadU32Be(),
				Counters64D = r.ReadU64Be()
			};
		}

		internal static void Write(FaolanStream s, GuildRenownBlock v)
		{
			s.WriteByte(v.Flag);
			WriteList(s, v.StatIds);
			s.WriteByte(v.BoolA).WriteByte(v.BoolB);
			foreach (var x in v.Group1)
				s.WriteUInt32(x);
			s.WriteByte(v.BoolC).WriteByte(v.BoolD);
			foreach (var x in v.Group2)
				s.WriteUInt32(x);
			WriteList(s, v.List2);
			foreach (var x in v.Group3)
				s.WriteUInt32(x);
			foreach (var x in v.RenownCounters)
				s.WriteUInt64(x);
			foreach (var x in v.Group4)
				s.WriteUInt32(x);
			foreach (var x in v.Counters64B)
				s.WriteUInt64(x);
			foreach (var x in v.Counters64C)
				s.WriteUInt64(x);
			s.WriteUInt32(v.Group5).WriteUInt64(v.Counters64D);
		}

		private static uint[] ReadList(ref SpanReader r)
		{
			return ReadU32s(ref r, r.ReadListCount());
		}

		private static uint[] ReadU32s(ref SpanReader r, int n)
		{
			var result = new uint[n];
			for (var i = 0; i < n; i++)
				result[i] = r.ReadU32Be();

			return result;
		}

		private static ulong[] ReadU64s(ref SpanReader r, int n)
		{
			var result = new ulong[n];
			for (var i = 0; i < n; i++)
				result[i] = r.ReadU64Be();

			return result;
		}

		private static void WriteList(FaolanStream s, uint[] items)
		{
			s.WriteUInt32(ListFrame.ForCount(items.Length));
			foreach (var v in items)
				s.WriteUInt32(v);
		}
	}

	/// <summary>
	///     The block's fields, each mapping to a <c>CoreGuild_c::Read</c> helper call and the <c>param_1</c> offset it
	///     stores into (FUN_0048f900 = ReadU32, FUN_0048f720 = ReadU64, FUN_0048f6d0/FUN_004e0390 = ReadByte,
	///     FUN_004d4c70 = ReadList).
	/// </summary>
	/// <remarks>
	///     Field semantics are only partly known. The stat-id list and <c>RenownCounters</c> are proven -
	///     <c>RenownCounters[1]</c> is the live renown total, the per-guild value <see cref="GuildRenownBlock.ForRenown" />
	///     splices in - while the rest are the captured guild's feat, city and cap values whose meanings the stripped
	///     binary does not name, so neutral structural names are kept. Large values such as 0x01C9C380 (30,000,000,
	///     <c>Group4[1]</c>) and 0x3A1D51C0 (975,000,000, <c>Counters64D</c>) are that guild's own figures, not renown.
	/// </remarks>
	public sealed record GuildRenownBlock
	{
		// The 13 guild stat-slot ids (the first list). Constant across guilds - the structural scaffold a fresh-guild block
		// reproduces, ascending in the usual stat-id stepping.
		public static readonly uint[] StatSlotIds = [0x4FFB0, 0x4FFB1, 0x50014, 0x50078, 0x500DC, 0x50140, 0x501A4, 0x50208, 0x50209, 0x5026C, 0x5026D, 0x502D0, 0x50334];

		public byte BoolA { get; set; } = 1; // ReadByte -> param_1[0x1b]
		public byte BoolB { get; set; } // ReadByte -> param_1[0x6d]
		public byte BoolC { get; set; } // ReadByte -> param_1[0x2a]
		public byte BoolD { get; set; } // ReadByte -> param_1[0xa9]

		public byte Flag { get; set; } = 0x01; // ReadByte -> param_1[4]
		public uint Group5 { get; set; } // ReadU32 -> param_1[0x6a]
		public uint[] Group1 { get; set; } = new uint[3]; // ReadU32 x3 -> param_1[0x20..0x22]
		public uint[] Group2 { get; set; } = new uint[2]; // ReadU32 x2 -> param_1[0x2b..0x2c]
		public uint[] Group3 { get; set; } = new uint[6]; // ReadU32 x6 -> param_1[0x2d,2e,2f,30,32,33]

		public uint[] Group4 { get; set; } = new uint[2]; // ReadU32 x2 -> param_1[0x58..0x59]

		// The second list (count 3 in the capture) - feat/city stat ids. ReadList -> param_1+0x1c.
		public uint[] List2 { get; set; } = new uint[3];
		public uint[] StatIds { get; set; } = (uint[])StatSlotIds.Clone(); // ReadList -> param_1+0x39 (13 stat-slot ids)
		public ulong Counters64D { get; set; } // ReadU64 -> param_1[0x56]
		public ulong[] Counters64B { get; set; } = new ulong[8]; // ReadU64 x8 -> param_1[0x68,0x66,0x60,0x5e,0x5c,0x5a,0x64,0x62]

		public ulong[] Counters64C { get; set; } = new ulong[3]; // ReadU64 x3 -> param_1[0x50,0x52,0x54]

		// The renown counters (the case-3 leading ReadU64 x6 -> param_1[0x3e,0x40,0x42,0x44,0x46,0x48]).
		// RenownCounters[1] is the accumulating renown total - the only field that changed between the two captured
		// snapshots, and the value ForRenown sets. Zeroed for a fresh guild.
		public ulong[] RenownCounters { get; set; } = new ulong[6];

		// The captured "Sepulchra Invictus" block as structured fields; building it reproduces the capture byte-for-byte
		// (GuildRenownBlockTests). It is a cosmetic placeholder: the large values (0x01C9C380 / 0x3A1D51C0, the feat and city
		// stats) are that guild's own, not generic. Open work tracked in Gitea #335.
		public static GuildRenownBlock CapturedPlaceholder()
		{
			return new GuildRenownBlock
			{
				Flag = 0x01,
				BoolA = 1,
				Group1 = [1523, 12, 2070],
				List2 = [0x410, 0x816, 0xBEA],
				Group3 = [57, 0, 84, 84, 0, 0],
				RenownCounters = [111747, 20085677, 1225359, 45185, 13896395, 1058420],
				Group4 = [3, 30000000],
				Counters64B = [0, 0, 0, 0, 0, 17, 0, 0],
				Counters64C = [25111741, 42286280, 26217713],
				Counters64D = 975000000
			};
		}

		// A brand-new guild's full-update block. A fresh guild's full update is its create-time block re-expressed in the
		// full-update form: the same CoreGuild layout and the same fresh-guild state, differing only in the two form fields
		// the full update sets (Flag = 1 rather than 0, and the 13 stat-slot ids populated rather than the empty create-time
		// list). It is therefore derived from ForGuildCreate - byte-exact against the real fresh-guild create capture
		// (GuildCreateRenownBlockTests) - with only those form fields overridden, which is what keeps a just-created guild
		// consistent between its create block and its next full update.
		//
		// A genuine fresh guild has Group4 = [0, 0] and Counters64D = 0x01A93458, so the large values in
		// CapturedPlaceholder are one captured guild's accumulated figures rather than universal caps, and carrying them here
		// would make a freshly created guild render a guild city it never built. The still-opaque slots stay whatever the
		// fresh-guild create capture has, never another guild's data.
		public static GuildRenownBlock ForFreshGuild()
		{
			var b = ForGuildCreate();
			b.Flag = 0x01; // full-update form (create writes 0)
			b.BoolA = 1; // full-update form (create writes 0)
			b.StatIds = (uint[])StatSlotIds.Clone(); // full-update populates the 13 stat-slot ids (create leaves them empty)

			return b;
		}

		// The create-time block that closes DispatchGuildCreated - the same CoreGuild_c::Read case-1-to-3 layout as the
		// full-update renown block (FUN_00565900), but as a fresh guild writes it at creation: Flag = 0 (the full update uses
		// 1), an empty StatIds list (the 13 slot ids are populated later), and zeroed renown. The only non-zero fields are
		// the create-time feat defaults (List2 = 0x410/0x816/0xBEA) and Counters64C[0..2] / Counters64D, whose exact
		// semantics the stripped binary does not name and which are kept as the captured create-time constants, that capture
		// being itself a fresh guild. Encodes to the 225-byte tail (GuildRenownBlockTests).
		public static GuildRenownBlock ForGuildCreate()
		{
			return new GuildRenownBlock
			{
				Flag = 0x00,
				StatIds = [],
				BoolA = 0,
				List2 = [0x410, 0x816, 0xBEA],
				Counters64C = [0x373131310007A128, 0x064AB178, 0x09D5D338],
				Counters64D = 0x01A93458
			};
		}

		// A guild's block built from its stored renown total: the fresh-guild scaffold with the accumulated total spliced
		// into RenownCounters[1], the proven renown-total slot. The other, still-opaque feat and city slots stay zeroed until
		// their stat-id meanings are decoded, so this block carries no other guild's figures.
		public static GuildRenownBlock ForRenown(uint renown)
		{
			var b = ForFreshGuild();
			b.RenownCounters[1] = renown;

			return b;
		}
	}
}
