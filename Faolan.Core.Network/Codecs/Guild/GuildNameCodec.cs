using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildName (0x2041) - the reply to ClientRequestGuildName: [u32 guildId][name s16].
	//
	// ⚠️ The reply carries a BARE guild id, not the ID32 the REQUEST used - the pair is capture-verified in
	// xxx_2clients_jumping (frame 1120 asks for guild 0x00075E9A as `[0000deb7][00075e9a]`, frame 1131 answers
	// `00075e9a 000b "the chickas"`). [V2: xxx_2clients_jumping.tshark.json frame 1131]
	//
	// Moved in from `GuildService.WriteGuildNameBody` in #530 wave 7; byte-exact in GuildPacketTests.
	public static class GuildNameCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildName;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildName value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildName Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildNameCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildName Read(ref SpanReader r)
		{
			return new GuildName(r.ReadU32Be(), r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, GuildName v)
		{
			s.WriteUInt32(v.GuildId).WriteString(v.Name);
		}
	}

	// DispatchGuildName (0x2041).
	public sealed record GuildName(uint GuildId, string Name);
}
