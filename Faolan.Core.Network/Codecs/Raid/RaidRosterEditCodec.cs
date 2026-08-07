using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Raid
{
	// The two c2s messages that rearrange the raid grid, both `[ID32][ID32]` and nothing else:
	//   RaidMoveTeamMember(const ID32& i_cCharacterID, const ID32& i_cDestinationTeamID)   0x6F
	//   RaidSwapCharacters(const ID32& i_cCharacterID, const ID32& i_cCharacterID2)        0x70
	// They are the same layout with the verb in the method, which is why this declares HandledMethods rather than
	// picking one of the two as *the* method - and why the record's fields are named for the SLOT rather than for
	// what the slot holds (the second ID32 is a team on one and a character on the other).
	//
	// No capture carries either (#669); the shape is the client's parameter list over the captured ID32 encoding.
	public static class RaidRosterEditCodec
	{
		public static readonly CSPlayerAgentMethods[] HandledMethods = [CSPlayerAgentMethods.RaidMoveTeamMember, CSPlayerAgentMethods.RaidSwapCharacters];

		public static byte[] Build(RaidRosterEdit value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			EntityIdCodec.Write(s, value.Subject);
			EntityIdCodec.Write(s, value.Destination);

			return s.ToArray();
		}

		public static RaidRosterEdit Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RaidRosterEditCodec));
			var subject = EntityIdCodec.Read(ref r);
			var destination = EntityIdCodec.Read(ref r);
			r.EnsureAtEnd();

			return new RaidRosterEdit(subject, destination);
		}
	}

	public sealed record RaidRosterEdit(EntityId Subject, EntityId Destination);
}
