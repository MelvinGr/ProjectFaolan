using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildCreated (0x2044) - retail's IMMEDIATE create response, which opens the guild window WITH NO RELOG
	// (unlike the 0x203F GuildUpdate, which only takes effect on the next login). Body decoded byte-exact
	// (docs/subsystems/create-guild-decode.md, GuildPacketTests):
	//   [creator member record][guild header]
	// i.e. the 0x203F pair with the arguments the other way round and no roster list. The header's `Form` byte carries
	// the request's guild FORM (the full update fixes it at 0x03) and its renown block is the create-time 225-byte
	// form, which is why one GuildHeaderCodec serves both.
	//
	// Moved in from `GuildService.WriteGuildCreatedBody` in #530 wave 7.
	public static class GuildCreatedCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildCreated;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildCreated value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildCreated Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildCreatedCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildCreated Read(ref SpanReader r)
		{
			return new GuildCreated(GuildMemberRecordCodec.Read(ref r), GuildHeaderCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, GuildCreated v)
		{
			GuildMemberRecordCodec.Write(s, v.Creator);
			GuildHeaderCodec.Write(s, v.Guild);
		}
	}

	// DispatchGuildCreated's two arguments. The creator is online (they just made the guild).
	public sealed record GuildCreated(GuildMemberRecord Creator, GuildHeader Guild);
}
