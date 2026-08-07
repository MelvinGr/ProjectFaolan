using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Raid
{
	// c2s RaidInviteCharacterByName (CSPlayerAgent 0x72) - `RaidInviteCharacterByName(const std::string& i_CharName,
	// const std::string& i_cMessage)`: two wire strings and nothing else. This is what the raid window's name box
	// sends, and it is the only raid message that names a player the server may never have seen.
	//
	// No capture carries it (#669); the shape is the client's parameter list over the captured string encoding
	// (`[u16 len][latin1]`).
	public static class RaidInviteByNameRequestCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.RaidInviteCharacterByName;

		public static byte[] Build(RaidInviteByNameRequest value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();

			return s.WriteString(value.CharacterName).WriteString(value.Message).ToArray();
		}

		public static RaidInviteByNameRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RaidInviteByNameRequestCodec));
			var name = r.ReadStringU16Length();
			var message = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new RaidInviteByNameRequest(name, message);
		}
	}

	public sealed record RaidInviteByNameRequest(string CharacterName, string Message);
}
