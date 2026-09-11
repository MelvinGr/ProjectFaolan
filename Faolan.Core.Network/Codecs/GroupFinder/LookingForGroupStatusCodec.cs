using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     The LFG status body - <c>[u8 flag][u16 len + Latin1 message]</c> - shared by the two s2c messages that carry
	///     one.
	/// </summary>
	/// <remarks>
	///     <list type="bullet">
	///         <item>
	///             <c>DispatchCharacterLookingForTeamStatus(i_bLookingForTeam, i_cMessage)</c> - a character's own flag,
	///             the mirror of c2s <c>SetLookingForTeamStatus</c>. Captured body <c>[00][0000]</c> (not looking, empty
	///             comment), sent when the group finder forms the team and clears the flag (chat-annotated-4 frame
	///             11943).
	///         </item>
	///         <item>
	///             <c>DispatchTeamLookingForMemberStatus(i_bLookingForMember, i_cMessage)</c> - a team's advert, the
	///             mirror of c2s <c>SetLookingForMemberStatus</c>, and the flag that puts the team in everyone else's
	///             SearchForTeam results.
	///         </item>
	///     </list>
	///     <para>
	///         The second has no captured body; its layout comes from the client's own reader <c>FUN_01340110</c>,
	///         which reads one byte through the 1-byte primitive and then a string through <c>FUN_0048f840</c> before
	///         dispatching - the same two reads, in the same order, as the character one - and its two "stream bad
	///         after 'i_bLookingForMember'/'i_cMessage'" asserts name the fields. One body, one codec, so this declares
	///         <c>HandledMethods</c> rather than a second copy.
	///     </para>
	/// </remarks>
	public static class LookingForGroupStatusCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
			[CSPlayerInterfaceMethods.DispatchCharacterLookingForTeamStatus, CSPlayerInterfaceMethods.DispatchTeamLookingForMemberStatus];

		public static byte[] Build(LookingForGroupStatus value)
		{
			using var s = new FaolanStream();
			s.WriteByte(value.Looking).WriteString(value.Message);

			return s.ToArray();
		}

		public static LookingForGroupStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LookingForGroupStatusCodec));
			var looking = r.ReadU8() != 0;
			var message = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new LookingForGroupStatus(looking, message);
		}
	}

	/// <remarks>
	///     <c>Looking</c> is <c>i_bLookingForTeam</c> on the character message and <c>i_bLookingForMember</c> on the
	///     team one; <c>Message</c> is <c>i_cMessage</c> on both. Named for what the pair is rather than for either
	///     subject, because the record is the body and the body is the same - which side is advertising is the method's
	///     job to say.
	/// </remarks>
	public sealed record LookingForGroupStatus(bool Looking, string Message);
}
