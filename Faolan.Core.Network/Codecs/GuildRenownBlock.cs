using System;
using System.Buffers.Binary;

namespace Faolan.Core.Network.Codecs
{
	// Typed, byte-exact codec for the 277-byte renown/feat/city-stats block at the tail of the 0x203F
	// GuildFullUpdate (Gitea #219). The block is the tail of the i_pcGuild argument read by
	// CSPlayerInterface::DispatchGuildUpdate -> CoreGuild_c::Read (FUN_00565900, streaming version 3) for the
	// govForm==1 form (case 1 falling through case 3 - the form our created guilds use). The FULL reader walk
	// (version, guildId, govForm, cash, 3 strings, then this block) consumes the whole i_pcGuild arg and lands
	// EXACTLY on the following member record (Captures/scripts/decode_guild_renown.py --walk, validated against both
	// 0x203F snapshots in the guild_invite capture); the ONLY byte that differs between snapshots is
	// RenownCounters[1] (param_1[0x40]), the renown total that ticked. The two embedded count-framed lists use the
	// shared 0x3F1 invariant (word=(count+1)*0x3F1).
	//
	// Each field below maps to a CoreGuild_c::Read helper call + the param_1 offset it stores into (FUN_0048f900=
	// ReadU32, FUN_0048f720=ReadU64, FUN_0048f6d0/FUN_004e0390=ReadByte, FUN_004d4c70=ReadList). Field SEMANTICS
	// are only partly known: the stat-id list + RenownCounters are proven (RenownCounters[1] is the live renown
	// total - it is the per-guild value ForRenown splices in); the rest are the captured guild's feat/city/cap
	// values whose meanings the stripped binary doesn't name, so neutral structural names are kept. Caps like
	// 0x01C9C380 (30,000,000, Group4[1]) and 0x3A1D51C0 (975,000,000, Counters64D) are constants, NOT renown.
	public sealed class GuildRenownBlock : ICodec<GuildRenownBlock>
	{
		public const int Length = 277;

		// 0x3F1 list-framing invariant: the first word of a count-framed u32 list is (count+1)*0x3F1.
		private const uint ListMultiplier = 0x3F1;

		// The 13 guild stat-slot ids (the first 0x3F1 list). Constant across guilds - the structural scaffold a
		// fresh-guild block reproduces (ascending, the classic AoC stat-id stepping).
		public static readonly uint[] StatSlotIds =
		[
			0x4FFB0, 0x4FFB1, 0x50014, 0x50078, 0x500DC, 0x50140, 0x501A4,
			0x50208, 0x50209, 0x5026C, 0x5026D, 0x502D0, 0x50334
		];

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

		// The second 0x3F1 list (count 3 in the capture) - feat/city stat ids. ReadList -> param_1+0x1c.
		public uint[] List2 { get; set; } = new uint[3];
		public uint[] StatIds { get; set; } = (uint[])StatSlotIds.Clone(); // ReadList -> param_1+0x39 (13 stat-slot ids)
		public ulong Counters64D { get; set; } // ReadU64 -> param_1[0x56]
		public ulong[] Counters64B { get; set; } = new ulong[8]; // ReadU64 x8 -> param_1[0x68,0x66,0x60,0x5e,0x5c,0x5a,0x64,0x62]

		public ulong[] Counters64C { get; set; } = new ulong[3]; // ReadU64 x3 -> param_1[0x50,0x52,0x54]

		// The renown counters (case-3 leading ReadU64 x6 -> param_1[0x3e,0x40,0x42,0x44,0x46,0x48]).
		// RenownCounters[1] (param_1[0x40]) is the accumulating renown total - the only field that changed between
		// the two captured snapshots, and the value ForRenown sets. Zeroed for a fresh guild.
		public ulong[] RenownCounters { get; set; } = new ulong[6];

		// Serializes to the 277-byte wire block (byte-identical to the captured block when round-tripped).
		public byte[] Encode() => this.ToArray();

		public void Write(FaolanStream s)
		{
			s.WriteByte(Flag);
			WriteList(s, StatIds);
			s.WriteByte(BoolA).WriteByte(BoolB);
			foreach (var v in Group1)
				s.WriteUInt32(v);
			s.WriteByte(BoolC).WriteByte(BoolD);
			foreach (var v in Group2)
				s.WriteUInt32(v);
			WriteList(s, List2);
			foreach (var v in Group3)
				s.WriteUInt32(v);
			foreach (var v in RenownCounters)
				s.WriteUInt64(v);
			foreach (var v in Group4)
				s.WriteUInt32(v);
			foreach (var v in Counters64B)
				s.WriteUInt64(v);
			foreach (var v in Counters64C)
				s.WriteUInt64(v);
			s.WriteUInt32(Group5).WriteUInt64(Counters64D);
		}

		// ICodec stream Read: the block is a fixed 277 bytes; delegate to the span-based Decode (the zero-copy entry
		// point used when the block is a sub-region of the 0x203F guild-update body).
		public static GuildRenownBlock Read(FaolanStream s) => Decode(s.ReadArray(Length));

		// The captured "Sepulchra Invictus" block as structured fields (replaces the old hand-transcribed verbatim
		// blob, which had dropped 3 bytes -> 274 not 277, misaligning the roster). Encode() reproduces the capture
		// byte-for-byte (GuildRenownBlockTests). Used as the cosmetic placeholder until a guild's own renown is
		// wired; the large values (caps 0x01C9C380 / 0x3A1D51C0, the feat/city stats) are this guild's, not generic.
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

		// Decodes a 277-byte block. Throws on a malformed 0x3F1 list word or wrong length (the block is fixed-size
		// for govForm 1; a different govForm would have a different layout/length and is out of scope).
		public static GuildRenownBlock Decode(ReadOnlySpan<byte> block)
		{
			if (block.Length < Length)
				throw new ArgumentException($"renown block must be {Length} bytes, got {block.Length}");

			var o = 0;
			var b = new GuildRenownBlock
			{
				Flag = block[o++],
				StatIds = ReadList(block, ref o),
				BoolA = block[o++],
				BoolB = block[o++],
				Group1 = ReadU32s(block, ref o, 3),
				BoolC = block[o++],
				BoolD = block[o++],
				Group2 = ReadU32s(block, ref o, 2),
				List2 = ReadList(block, ref o),
				Group3 = ReadU32s(block, ref o, 6),
				RenownCounters = ReadU64s(block, ref o, 6),
				Group4 = ReadU32s(block, ref o, 2),
				Counters64B = ReadU64s(block, ref o, 8),
				Counters64C = ReadU64s(block, ref o, 3),
				Group5 = ReadU32(block, ref o),
				Counters64D = ReadU64(block, ref o)
			};

			if (o != Length)
				throw new InvalidOperationException($"renown block decoded {o} bytes, expected {Length}");

			return b;
		}

		// A fresh-guild block: the placeholder scaffold (stat-ids, caps) with the accumulating renown counters
		// zeroed - what a brand-new guild must show. RenownCounters + Counters64C are the proven renown groups
		// (RenownCounters[1] is the total that ticked between snapshots; Counters64C are the large renown
		// sub-totals); the rest (caps, feat/city slots) are kept since their per-guild-vs-constant split needs the
		// stat-id meanings (a follow-up). Cosmetic and in-client unverified - see #219.
		public static GuildRenownBlock ForFreshGuild()
		{
			var b = CapturedPlaceholder();
			Array.Clear(b.RenownCounters);
			Array.Clear(b.Counters64C);
			return b;
		}

		// A guild's block built from its stored renown total: the fresh-guild scaffold with the accumulated total
		// spliced into RenownCounters[1] (the proven renown-total slot - the only field that ticked between the two
		// captured snapshots). The other (still-opaque) feat/city slots stay zeroed until their stat-id meanings are
		// decoded, so this is the accurate block for our created guilds (none carry another guild's feat/city stats).
		public static GuildRenownBlock ForRenown(uint renown)
		{
			var b = ForFreshGuild();
			b.RenownCounters[1] = renown;
			return b;
		}

		private static uint[] ReadList(ReadOnlySpan<byte> b, ref int o)
		{
			var word = ReadU32(b, ref o);
			if (word % ListMultiplier != 0)
				throw new InvalidOperationException($"0x3F1 list word {word:X} not a multiple of {ListMultiplier:X}");

			return ReadU32s(b, ref o, (int)(word / ListMultiplier) - 1);
		}

		private static uint ReadU32(ReadOnlySpan<byte> b, ref int o)
		{
			var v = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(o, 4));
			o += 4;
			return v;
		}

		private static uint[] ReadU32s(ReadOnlySpan<byte> b, ref int o, int n)
		{
			var r = new uint[n];
			for (var i = 0; i < n; i++)
				r[i] = ReadU32(b, ref o);
			return r;
		}

		private static ulong ReadU64(ReadOnlySpan<byte> b, ref int o)
		{
			var v = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(o, 8));
			o += 8;
			return v;
		}

		private static ulong[] ReadU64s(ReadOnlySpan<byte> b, ref int o, int n)
		{
			var r = new ulong[n];
			for (var i = 0; i < n; i++)
				r[i] = ReadU64(b, ref o);
			return r;
		}

		private static void WriteList(FaolanStream s, uint[] items)
		{
			s.WriteUInt32((items.Length + 1) * ListMultiplier);
			foreach (var v in items)
				s.WriteUInt32(v);
		}
	}
}
