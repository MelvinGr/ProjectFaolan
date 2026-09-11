using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildCreated</c> - retail's immediate create response, which opens the guild window with no relog,
	///     unlike <c>DispatchGuildUpdate</c>, which only takes effect on the next login. The body is
	///     <c>[creator member record][guild header]</c>.
	/// </summary>
	/// <remarks>
	///     That is the full update's pair with the arguments the other way round and no roster list. The header's
	///     <c>Form</c> byte carries the request's guild form (the full update fixes it at 0x03) and its renown block is
	///     the create-time 225-byte form, which is why one <see cref="GuildHeaderCodec" /> serves both. Byte-exact in
	///     <c>GuildPacketTests</c>; see docs/subsystems/create-guild-decode.md.
	/// </remarks>
	public static class GuildCreatedCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildCreated;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>DispatchGuildCreated's two arguments. The creator is online, having just made the guild.</summary>
	public sealed record GuildCreated(GuildMemberRecord Creator, GuildHeader Guild);
}
