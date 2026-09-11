using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     An ID32 as it appears on the RPC wire: <c>[u32 class][u32 instance]</c>, the stream form of the protobuf
	///     <c>ProtoID32</c>.
	/// </summary>
	/// <remarks>
	///     It is the entire body of several c2s methods that name one entity and nothing else - AcceptTeamInvite,
	///     KickFromTeam, StartTrade, DeclineTrade - and the head of several longer ones. It is also the whole body of two
	///     s2c raid messages, <c>DispatchRaidTeamLeft</c> and <c>DispatchRaidNewRaidLeader</c>, whose single
	///     <c>i_cTeamID</c> the client reads as exactly this pair.
	/// </remarks>
	public static class EntityIdCodec
	{
		// The width of an encoded ID32 - what a caller slices off a longer body before handing it here.
		public const int Length = 8;

		public static byte[] Build(EntityId value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static EntityId Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(EntityIdCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static EntityId Read(ref SpanReader r)
		{
			return new EntityId(r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, EntityId v)
		{
			s.WriteUInt32(v.Class).WriteUInt32(v.Id);
		}
	}

	public sealed record EntityId(uint Class, uint Id);

	/// <summary>
	///     The forms the senders build, as value factories. <see cref="Character" /> is by far the common one: the
	///     player-trade endpoints (DispatchRequestTrade / DispatchStartTrade / DispatchPartnerAcceptedTrade) name one
	///     character and nothing else.
	/// </summary>
	public static class EntityIds
	{
		public static EntityId Character(uint characterId)
		{
			return new EntityId(EntityClass.Character, characterId);
		}

		// A team - and, until a capture says otherwise, a raid: the client's raid object is a `cTeam` and the raid's own id
		// rides in the same slot a team id does, so it goes out under the team entity class. [V0]
		public static EntityId Team(uint teamId)
		{
			return new EntityId(EntityClass.Team, teamId);
		}
	}
}
