using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     The group-membership records the game server pushes onto the world connection, mirroring what the CS tier does
	///     to the team roster: AddToGroupIIR when someone joins, RemoveFromGroupIIR when they leave or are kicked.
	/// </summary>
	/// <remarks>
	///     A join therefore produces both the CS tier's roster dispatches and one AddToGroup per member on each member's
	///     world connection. The capture sends one record per member - the joiner and the existing members - to every
	///     world connection in the team.
	///     <code>
	///     AddToGroupIIR      [u32 iir][ProtoID32 member][u8 0][ProtoID32 team][ProtoID32 leader]   25-byte body
	///     RemoveFromGroupIIR [u32 iir][ProtoID32 member][u8 0][ProtoID32 team]                     17-byte body
	///     </code>
	///     <para>
	///         They are one family rather than two messages sharing a folder: both name a member and the team it moved in
	///         or out of, and the add form is the remove form plus the leader. <c>Parse</c> therefore returns the
	///         <c>GroupMembershipIIR</c> base and <c>Build</c> dispatches on the concrete record.
	///     </para>
	///     <para>
	///         <c>Build</c> returns the body without the carrier's <c>[u32 length]</c>, so it must go out through
	///         <c>WorldIir.Send</c>, which writes that prefix. Sending an unframed record through the verbatim sender puts
	///         the IIR type where the client reads a length, which it takes as a multi-hundred-megabyte payload and drops
	///         the connection on.
	///     </para>
	/// </remarks>
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

	/// <summary>What the two forms share: who moved, and which team. The add form adds the leader.</summary>
	public abstract record GroupMembershipIIR(uint MemberId, uint TeamId);

	public sealed record AddToGroupIIR(uint MemberId, uint TeamId, uint LeaderId) : GroupMembershipIIR(MemberId, TeamId);

	public sealed record RemoveFromGroupIIR(uint MemberId, uint TeamId) : GroupMembershipIIR(MemberId, TeamId);
}
