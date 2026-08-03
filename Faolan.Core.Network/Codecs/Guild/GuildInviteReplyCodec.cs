using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildInviteReplyToInviter (0x2031) - what the INVITER is told once the invitee answers:
	// `[string message][u8 joined]`.
	//
	// The trailing field is one BYTE, not a u32: `CSPlayerInterfaceDispatch_t::DispatchGuildInviteReplyToInviter`
	// (FUN_01340db0) reads `i_cMessage` with the string primitive and then `i_bJoined` with `FUN_00cba1b0(dst, 1)`,
	// widening it to a bool with `!= 0`. The `i_b` prefix says the same thing; the reader is what pins the width.
	//
	// ⚠️ `i_cMessage` is a string whose CONTENT the client decides what to do with, and no capture shows one. We send
	// the invitee's NAME, because that is the only thing the inviter's toast can need that the message must carry -
	// the invite reply names nobody else. [V0]
	public static class GuildInviteReplyCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildInviteReplyToInviter;

		public static byte[] Build(GuildInviteReply value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteString(value.Message).WriteByte((byte)(value.Joined
				? 1
				: 0));

			return s.ToArray();
		}

		public static byte[] Build(string message, bool joined)
		{
			return Build(new GuildInviteReply(message, joined));
		}

		public static GuildInviteReply Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildInviteReplyCodec));
			var value = new GuildInviteReply(r.ReadStringU16Length(), r.ReadU8() != 0);
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildInviteReply(string Message, bool Joined);
}
