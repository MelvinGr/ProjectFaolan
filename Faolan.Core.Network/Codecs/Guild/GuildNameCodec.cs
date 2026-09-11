using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildName</c> - the reply to <c>ClientRequestGuildName</c>: <c>[u32 guildId][name s16]</c>.
	/// </summary>
	/// <remarks>
	///     The reply carries a bare guild id rather than the ID32 the request used; the pair is capture-verified in
	///     xxx_2clients_jumping (frame 1120 asks for guild 0x00075E9A as <c>[0000deb7][00075e9a]</c>, frame 1131 answers
	///     <c>00075e9a 000b "the chickas"</c>). Byte-exact in <c>GuildPacketTests</c>.
	///     [V2: xxx_2clients_jumping.tshark.json frame 1131]
	/// </remarks>
	public static class GuildNameCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildName;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The DispatchGuildName body.</summary>
	public sealed record GuildName(uint GuildId, string Name);
}
