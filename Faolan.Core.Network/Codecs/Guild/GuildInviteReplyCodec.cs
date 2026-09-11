using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildInviteReplyToInviter</c> - what the inviter is told once the invitee answers:
	///     <c>[string message][u8 joined]</c>.
	/// </summary>
	/// <remarks>
	///     The trailing field is one byte rather than a u32:
	///     <c>CSPlayerInterfaceDispatch_t::DispatchGuildInviteReplyToInviter</c> (FUN_01340db0) reads <c>i_cMessage</c>
	///     with the string primitive and then <c>i_bJoined</c> with <c>FUN_00cba1b0(dst, 1)</c>, widening it to a bool.
	///     <para>
	///         <c>i_cMessage</c> is a string whose content the client decides what to do with, and no capture shows one.
	///         The invitee's name is sent, because that is the only thing the inviter's toast can need that the message
	///         must carry - the invite reply names nobody else. [V0]
	///     </para>
	/// </remarks>
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
