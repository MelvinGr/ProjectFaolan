using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Raid
{
	/// <summary>
	///     c2s <c>RaidInviteCharacter(const ID32&amp; i_cCharID, const std::string&amp; i_cMessage)</c>. The team twin
	///     is <see cref="Team.TeamInviteRequestCodec" />, one trailing flag longer: the raid invite has no
	///     <c>i_bCheckCriminalStatus</c>.
	/// </summary>
	/// <remarks>
	///     No capture carries it. The parameter list and the ID32 and string encodings are the client's, both pinned by
	///     the captured team traffic that uses the same primitives.
	/// </remarks>
	public static class RaidInviteRequestCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.RaidInviteCharacter;

		public static byte[] Build(RaidInviteRequest value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			EntityIdCodec.Write(s, value.Target);

			return s.WriteString(value.Message).ToArray();
		}

		public static RaidInviteRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RaidInviteRequestCodec));
			var target = EntityIdCodec.Read(ref r);
			var message = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new RaidInviteRequest(target, message);
		}
	}

	public sealed record RaidInviteRequest(EntityId Target, string Message);
}
