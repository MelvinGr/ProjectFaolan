using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Team
{
	// An ID32 as it appears on the RPC wire: `[u32 class][u32 instance]`, the stream form of the protobuf `ProtoID32`.
	// It is the ENTIRE body of several c2s methods that name one entity and nothing else - AcceptTeamInvite (CS
	// 0x2008), KickFromTeam (GameCharAgent 0x2009), StartTrade (0x2065), DeclineTrade (0x2066) - and the head of
	// several longer ones. It is also the whole body of two s2c raid messages, DispatchRaidTeamLeft (0x95) and
	// DispatchRaidNewRaidLeader (0x98), whose single `i_cTeamID` the client reads as exactly this pair (#669).
	//
	// Filed under Team because that is where it was first pulled out of the listener dispatch; it is not team-specific
	// and belongs at the Codecs root once the root files land (#530 wave 6).
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

	// The forms our senders build, as VALUE factories. `Character` is by far the common one - the player-trade
	// endpoints (DispatchRequestTrade / DispatchStartTrade / DispatchPartnerAcceptedTrade) name one character and
	// nothing else, which is what folded `PlayerTradeCodec.BuildCharacterRef` into this codec in #530 wave 7.
	public static class EntityIds
	{
		public static EntityId Character(uint characterId)
		{
			return new EntityId(EntityClass.Character, characterId);
		}

		// A team - and, until a capture says otherwise, a RAID: the client's raid object is a `cTeam` and the raid's
		// own id rides in the same slot a team id does, so it goes out under the team entity class (#669). [V0]
		public static EntityId Team(uint teamId)
		{
			return new EntityId(EntityClass.Team, teamId);
		}
	}
}
