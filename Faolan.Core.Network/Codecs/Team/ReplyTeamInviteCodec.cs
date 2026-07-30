using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	// s2c DispatchReplyTeamInvite (GameCharInterface 0x209F) - the answer to the invited client's RequestTeamInvite:
	// the inviter's record, the (empty) invite message, and one more zero byte. Byte-exact against f683 of
	// login_2-clients_group-trade-friend.
	//
	// ⚠️ The reply's RpcHeader must carry the trailing 0x01 headerData, and its SENDER is the GameCharAgent object -
	// see the RequestTeamInvite case in GameServerListener.Inbound. Without either the client discards the reply and
	// never raises the invite window, which looks exactly like a wrong body and is not one.
	public static class ReplyTeamInviteCodec
	{
		public const ushort Opcode = (ushort)GameCharInterfaceOpcodes.DispatchReplyTeamInvite;

		public static byte[] Build(ReplyTeamInvite value)
		{
			using var s = new FaolanStream();
			TeamMemberRecordCodec.Write(s, value.Member);
			s.WriteString(value.Message).WriteByte(value.Trailer);

			return s.ToArray();
		}

		public static byte[] Build(TeamMemberRecord member)
		{
			return Build(new ReplyTeamInvite(member, "", 0));
		}

		public static ReplyTeamInvite Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ReplyTeamInviteCodec));
			var member = TeamMemberRecordCodec.Read(ref r);
			var message = r.ReadStringU16Length();
			var trailer = r.ReadU8();
			r.EnsureAtEnd();

			return new ReplyTeamInvite(member, message, trailer);
		}
	}

	// Trailer is 0 in the capture; carried rather than named because one capture cannot say what it selects. [V0]
	public sealed record ReplyTeamInvite(TeamMemberRecord Member, string Message, byte Trailer);
}
