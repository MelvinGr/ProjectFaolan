using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	// c2s TeamInvite (CSPlayerAgent 0x2003) - the leader (or a solo player) invites someone into a team:
	//   TeamInvite(const ID32& i_cTargetID, const std::string& i_cMessage, bool i_bCheckCriminalStatus)
	// The body is 11 bytes in all four captures - the ID32, an EMPTY message and the flag - which is why only the
	// first 8 were read for so long (#459).
	//
	// i_bCheckCriminalStatus asks the server to refuse the invite if the target is flagged CRIMINAL, a PvP rule we
	// model nothing of. It is set in every captured invite, so it is not a rare path - just a no-op until PvP
	// flagging exists.
	public static class TeamInviteRequestCodec
	{
		public const ushort Opcode = (ushort)CSPlayerAgentOpcodes.TeamInvite;

		public static byte[] Build(TeamInviteRequest value)
		{
			using var s = new FaolanStream();
			EntityIdCodec.Write(s, value.Target);

			return s.WriteString(value.Message).WriteByte(value.CheckCriminalStatus ? (byte)1 : (byte)0).ToArray();
		}

		public static TeamInviteRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamInviteRequestCodec));
			var target = EntityIdCodec.Read(ref r);
			var message = r.ReadStringU16Length();
			var checkCriminalStatus = r.ReadU8() != 0;
			r.EnsureAtEnd();

			return new TeamInviteRequest(target, message, checkCriminalStatus);
		}
	}

	public sealed record TeamInviteRequest(EntityId Target, string Message, bool CheckCriminalStatus);
}
