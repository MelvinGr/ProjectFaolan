using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     c2s <c>TeamInvite(const ID32&amp; i_cTargetID, const std::string&amp; i_cMessage, bool
	///     i_bCheckCriminalStatus)</c> - the leader, or a solo player, invites someone into a team. The body is 11 bytes
	///     in all four captures: the ID32, an empty message and the flag.
	/// </summary>
	/// <remarks>
	///     <c>i_bCheckCriminalStatus</c> asks the server to refuse the invite if the target is flagged criminal, a PvP
	///     rule nothing here models. It is set in every captured invite, so it is not a rare path - just a no-op until PvP
	///     flagging exists.
	/// </remarks>
	public static class TeamInviteRequestCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.TeamInvite;

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
