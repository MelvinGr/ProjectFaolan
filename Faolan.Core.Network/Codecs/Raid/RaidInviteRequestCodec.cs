using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Raid
{
	// c2s RaidInviteCharacter (CSPlayerAgent 0x71) - `RaidInviteCharacter(const ID32& i_cCharID,
	// const std::string& i_cMessage)`. The team twin is TeamInviteRequestCodec, one trailing flag longer: the raid
	// invite has no `i_bCheckCriminalStatus`.
	//
	// No capture carries it (#669) - the parameter list and the ID32 / string encodings are the client's, both of
	// them pinned by the captured team traffic that uses the same primitives.
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
