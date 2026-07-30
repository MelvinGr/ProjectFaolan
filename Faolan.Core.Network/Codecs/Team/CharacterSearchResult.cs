using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	// Byte-exact codec for the 0x2010 DispatchCharacterSearchResult body - the "who" window's result list, the answer
	// to 0x200A CharacterSearch. Decoded from the five captured bodies (CharacterSearchResultTests):
	//
	//   [u32 (count+1)*0x3F1]  { count x entry }  [u8 0x00]
	//   entry = [TeamMemberRecord][u16 0][u8 0x04]
	//
	// It lives beside TeamMemberRecord rather than in a search folder because that record IS the payload: the "who"
	// result reuses the team subsystem's member descriptor verbatim, wrapped in three trailing bytes. Finding that
	// out is what identified the record's LEVEL field - see TeamMemberRecord's class comment - because these bodies
	// carry several characters at once where the team captures only ever carried two, both level 5.
	//
	// The trailing [u8 0x00] closes the list, the same end-marker DispatchTeamSearchResult carries. It must be emitted
	// even for an empty result or the client reads the empty frame and runs off the end (#408).
	public sealed class CharacterSearchResult
	{
		// The CSPlayerInterface opcode this body is the payload of (0x2010).
		public const CSPlayerInterfaceOpcodes Opcode = CSPlayerInterfaceOpcodes.DispatchCharacterSearchResult;

		// Per-entry trailer. The u16 is 0 in every captured entry; the 0x04 is constant across all five bodies and all
		// of their entries, so it is carried rather than named. [V0]
		public const ushort EntryPad = 0x0000;
		public const byte EntryTrailer = 0x04;

		// Closes the record list.
		public const byte ListTrailer = 0x00;

		public List<TeamMemberRecord> Characters { get; set; } = [];

		public byte[] Encode()
		{
			var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(Characters.Count));
			foreach (var c in Characters)
			{
				c.Write(s);
				s.WriteUInt16(EntryPad).WriteByte(EntryTrailer);
			}

			return s.WriteByte(ListTrailer).ToArray();
		}

		public static CharacterSearchResult Decode(byte[] body)
		{
			var s = new FaolanStream(body);
			var result = new CharacterSearchResult();

			var count = ListFrame.CountOf(s.ReadUInt32());
			for (var i = 0; i < count; i++)
			{
				result.Characters.Add(TeamMemberRecord.Read(s));
				s.ReadUInt16(); // EntryPad
				s.ReadByte(); // EntryTrailer
			}

			s.ReadByte(); // ListTrailer
			return result;
		}
	}
}
