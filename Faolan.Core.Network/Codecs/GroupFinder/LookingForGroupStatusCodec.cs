using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// The LFG status body - `[u8 flag][u16 len + LATIN1 message]` - shared by the two s2c messages that carry one:
	//
	//   0x2026 DispatchCharacterLookingForTeamStatus(i_bLookingForTeam, i_cMessage)  - a CHARACTER's own flag, the
	//     mirror of c2s SetLookingForTeamStatus. Captured body [00][0000] (not looking, empty comment), sent when the
	//     group finder forms the team - the finder clears the flag (chat-annotated-4 frame 11943).
	//   0x2027 DispatchTeamLookingForMemberStatus(i_bLookingForMember, i_cMessage)   - a TEAM's advert, the mirror of
	//     c2s SetLookingForMemberStatus, and the flag that puts the team in everyone else's SearchForTeam results.
	//
	// The second has no captured body; its layout is the client's own reader `FUN_01340110`, which reads one byte
	// through the 1-byte primitive and then a string through `FUN_0048f840` before dispatching - the same two reads,
	// in the same order, as the character one, and its two "stream bad after 'i_bLookingForMember'/'i_cMessage'"
	// asserts name the fields (#889). One body, one codec: `HandledMethods` rather than a second copy, the same way
	// TeamMemberEventCodec covers the three team events that share a body.
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

	// `Looking` is i_bLookingForTeam on the character message and i_bLookingForMember on the team one; `Message` is
	// i_cMessage on both. Named for what the pair IS rather than for either subject, because the record is the body
	// and the body is the same - which side is advertising is the METHOD's job to say.
	public sealed record LookingForGroupStatus(bool Looking, string Message);
}
