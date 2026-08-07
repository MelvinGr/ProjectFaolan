using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	// s2c DispatchTeamInvite (CSPlayerInterface 0x2001) - the member plus the invite MESSAGE, the note the inviter
	// types into the invite dialog. [TeamMemberRecord][u16 len][message LATIN1].
	//
	// The SAME bytes come back the other way: the invited client echoes this body in c2s RequestTeamInvite
	// (GameCharAgent 0x2091), and c2s RequestNewJoinHasHostileInFriends (0x2092) carries the same `GroupElement_c`.
	// So Parse serves all three; only DispatchTeamInvite is declared, because that is the message this codec IS.
	//
	// That trailing field was modelled as "a zero u16" because every captured invite carries an empty message, and
	// an empty wire string IS `[u16 0]` (wire strings are [u16 len][latin1]). What identifies it is the c2s side:
	// `TeamInvite(const ID32& i_cTargetID, const std::string& i_cMessage, bool i_bCheckCriminalStatus)` carries a
	// message the client asks the player for, the s2c push is what the target is shown, and this is a string slot
	// sitting in exactly the right place with exactly the right empty encoding (#459). Passing "" reproduces the
	// captured bytes exactly, so the goldens are unaffected. [V0 on the field's identity - structural, since no
	// capture carries a non-empty note.]
	//
	// s2c DispatchRaidInvite (0x9B) is the same body - the client reads it with the same cCharacter reader followed
	// by the same string (#669) - so it is declared here rather than getting a codec of its own.
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
