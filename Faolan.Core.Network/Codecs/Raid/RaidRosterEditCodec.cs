using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Raid
{
	/// <summary>
	///     The two c2s messages that rearrange the raid grid, both <c>[ID32][ID32]</c> and nothing else:
	///     <c>RaidMoveTeamMember(i_cCharacterID, i_cDestinationTeamID)</c> and
	///     <c>RaidSwapCharacters(i_cCharacterID, i_cCharacterID2)</c>.
	/// </summary>
	/// <remarks>
	///     They are the same layout with the verb in the method, which is why this declares <c>HandledMethods</c> rather
	///     than naming one of the two as the method, and why the record's fields are named for the slot rather than for
	///     what the slot holds - the second ID32 is a team on one and a character on the other.
	///     <para>
	///         No capture carries either; the shape is the client's parameter list over the captured ID32 encoding.
	///     </para>
	/// </remarks>
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
