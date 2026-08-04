using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	// The group-membership records the GAME server pushes onto the world connection, mirroring what the CS tier does
	// to the team roster: AddToGroupIIR when someone joins, RemoveFromGroupIIR when they leave or are kicked. Both
	// were first captured in login_2-clients_group-trade-friend and were name-only entries in Opcodes.cs until then.
	//
	// So a join produces BOTH: the CS tier's roster dispatches (TeamRosterCodec and friends) and one AddToGroup per
	// member on each member's world connection. The capture sends one record per member - the joiner AND the existing
	// members - to every world connection in the team.
	//
	//   AddToGroupIIR      [u32 iir][ProtoID32 member][u8 0][ProtoID32 team][ProtoID32 leader]   25-byte body
	//   RemoveFromGroupIIR [u32 iir][ProtoID32 member][u8 0][ProtoID32 team]                     17-byte body
	//
	// ONE FAMILY, not two messages that share a folder: both name a member and the team it moved in or out of, and the
	// add form is the remove form plus the leader. So Parse returns the GroupMembershipIIR base and Build dispatches
	// on the concrete record (#530 rule 5).
	//
	// ⚠️ Build returns the body WITHOUT the carrier's [u32 length] - rule 2 - so it goes out through WorldIir.Send,
	// which writes that prefix. It used to emit the prefix itself and go through WorldIir.SendFramed. Getting that
	// pairing wrong is expensive in exactly one direction: an UNFRAMED record through the verbatim sender puts the IIR
	// TYPE where the client reads a length, and it read 0x0DF44B73 as a 234 MB payload and dropped the connection,
	// killing BOTH clients in the team the instant the roster went out.
	public static class GroupMembershipCodec
	{
		public static readonly IirTypes[] HandledTypes = [IirTypes.AddToGroupIIR, IirTypes.RemoveFromGroupIIR];

		public static byte[] Build(GroupMembershipIIR value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32((uint)(value is AddToGroupIIR ? IirTypes.AddToGroupIIR : IirTypes.RemoveFromGroupIIR))
				.WriteUInt32(EntityClass.Character).WriteUInt32(value.MemberId).WriteByte(0).WriteUInt32(EntityClass.Team).WriteUInt32(value.TeamId);

			if (value is AddToGroupIIR add)
				s.WriteUInt32(EntityClass.Character).WriteUInt32(add.LeaderId);

			return s.ToArray();
		}

		public static GroupMembershipIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupMembershipCodec));
			var type = (IirTypes)r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "the member's entity class");
			var memberId = r.ReadU32Be();
			r.ReadU8(); // constant 0
			r.ExpectU32Be(EntityClass.Team, "the team entity class");
			var teamId = r.ReadU32Be();

			GroupMembershipIIR value;
			switch (type)
			{
				case IirTypes.AddToGroupIIR:
				{
					r.ExpectU32Be(EntityClass.Character, "the leader's entity class");
					value = new AddToGroupIIR(memberId, teamId, r.ReadU32Be());
					break;
				}

				case IirTypes.RemoveFromGroupIIR:
				{
					value = new RemoveFromGroupIIR(memberId, teamId);
					break;
				}

				default:
				{
					throw CodecParseException.Unexpected(nameof(GroupMembershipCodec), 0, "AddToGroupIIR or RemoveFromGroupIIR", $"0x{(uint)type:X8}");
				}
			}

			r.EnsureAtEnd();

			return value;
		}
	}

	// What the two forms share: WHO moved, and WHICH team. The add form adds the leader.
	public abstract record GroupMembershipIIR(uint MemberId, uint TeamId);

	public sealed record AddToGroupIIR(uint MemberId, uint TeamId, uint LeaderId) : GroupMembershipIIR(MemberId, TeamId);

	public sealed record RemoveFromGroupIIR(uint MemberId, uint TeamId) : GroupMembershipIIR(MemberId, TeamId);
}
