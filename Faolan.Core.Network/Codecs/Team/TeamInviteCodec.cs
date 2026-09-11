using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     s2c <c>DispatchTeamInvite</c> - the member plus the invite message, the note the inviter types into the invite
	///     dialog: <c>[TeamMemberRecord][u16 len][message Latin1]</c>.
	/// </summary>
	/// <remarks>
	///     The same bytes come back the other way: the invited client echoes this body in c2s <c>RequestTeamInvite</c>,
	///     and c2s <c>RequestNewJoinHasHostileInFriends</c> carries the same <c>GroupElement_c</c>. <c>Parse</c> therefore
	///     serves all three; only <c>DispatchTeamInvite</c> is declared, because that is the message this codec is.
	///     <para>
	///         The trailing field is a wire string, not a zero u16: every captured invite carries an empty message, and an
	///         empty wire string is <c>[u16 0]</c>. The c2s side identifies it -
	///         <c>TeamInvite(const ID32&amp; i_cTargetID, const std::string&amp; i_cMessage, bool
	///         i_bCheckCriminalStatus)</c> carries a message the client asks the player for, and the s2c push is what the
	///         target is shown. Passing "" reproduces the captured bytes exactly. [V0 on the field's identity -
	///         structural, since no capture carries a non-empty note.]
	///     </para>
	///     <para>
	///         s2c <c>DispatchRaidInvite</c> is the same body - the client reads it with the same cCharacter reader
	///         followed by the same string - so it is declared here rather than getting a codec of its own.
	///     </para>
	/// </remarks>
	public static class TeamInviteCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods = [CSPlayerInterfaceMethods.DispatchTeamInvite, CSPlayerInterfaceMethods.DispatchRaidInvite];

		public static byte[] Build(TeamInvite value)
		{
			using var s = new FaolanStream();
			TeamMemberRecordCodec.Write(s, value.Member);
			s.WriteString(value.Message);

			return s.ToArray();
		}

		public static byte[] Build(TeamMemberRecord member, string message = "")
		{
			return Build(new TeamInvite(member, message));
		}

		public static TeamInvite Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeamInviteCodec));
			var member = TeamMemberRecordCodec.Read(ref r);
			var message = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new TeamInvite(member, message);
		}
	}

	public sealed record TeamInvite(TeamMemberRecord Member, string Message);
}
