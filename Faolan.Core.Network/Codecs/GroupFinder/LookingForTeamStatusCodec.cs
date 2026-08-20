using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchCharacterLookingForTeamStatus (0x2026): the character's LFT flag plus its comment string - the
	// s2c mirror of c2s SetLookingForTeamStatus(bool i_bLookingForTeam, const std::string& i_cComment), whose
	// signature is the client's own. The captured body is [00][0000] - not looking, empty comment - sent when the
	// group finder forms the team (the finder clears the flag; chat-annotated-4 frame 11943).
	public static class LookingForTeamStatusCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchCharacterLookingForTeamStatus;

		public static byte[] Build(CharacterLookingForTeamStatus value)
		{
			using var s = new FaolanStream();
			s.WriteByte(value.LookingForTeam ? (byte)1 : (byte)0).WriteString(value.Comment);

			return s.ToArray();
		}

		public static CharacterLookingForTeamStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LookingForTeamStatusCodec));
			var lookingForTeam = r.ReadU8() != 0;
			var comment = r.ReadStringU16Length();
			r.EnsureAtEnd();

			return new CharacterLookingForTeamStatus(lookingForTeam, comment);
		}
	}

	public sealed record CharacterLookingForTeamStatus(bool LookingForTeam, string Comment);
}
