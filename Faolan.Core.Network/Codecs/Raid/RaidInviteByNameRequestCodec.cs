using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Raid
{
	/// <summary>
	///     c2s <c>RaidInviteCharacterByName(const std::string&amp; i_CharName, const std::string&amp; i_cMessage)</c>:
	///     two wire strings and nothing else. What the raid window's name box sends, and the only raid message that
	///     names a player the server may never have seen.
	/// </summary>
	/// <remarks>
	///     No capture carries it; the shape is the client's parameter list over the captured string encoding
	///     (<c>[u16 len][latin1]</c>).
	/// </remarks>
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
