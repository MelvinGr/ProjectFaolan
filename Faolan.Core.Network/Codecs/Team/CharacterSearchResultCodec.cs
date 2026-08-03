using System;
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
	// It lives beside TeamMemberRecordCodec rather than in a search folder because that record IS the payload: the
	// "who" result reuses the team subsystem's member descriptor verbatim, wrapped in three trailing bytes. Finding
	// that out is what identified the record's LEVEL field - see TeamMemberRecord's comment - because these bodies
	// carry several characters at once where the team captures only ever carried two, both level 5.
	//
	// The trailing [u8 0x00] closes the list, the same end-marker DispatchTeamSearchResult carries. It must be emitted
	// even for an empty result or the client reads the empty frame and runs off the end (#408).
	public static class CharacterSearchResultCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchCharacterSearchResult;

		// Per-entry trailer. The u16 is 0 in every captured entry; the 0x04 is constant across all five bodies and all
		// of their entries, so it is carried rather than named. [V0]
		private const ushort EntryPad = 0x0000;
		private const byte EntryTrailer = 0x04;

		// Closes the record list.
		private const byte ListTrailer = 0x00;

		public static byte[] Build(CharacterSearchResult value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Characters.Count));
			foreach (var c in value.Characters)
			{
				TeamMemberRecordCodec.Write(s, c);
				s.WriteUInt16(EntryPad).WriteByte(EntryTrailer);
			}

			return s.WriteByte(ListTrailer).ToArray();
		}

		public static CharacterSearchResult Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharacterSearchResultCodec));
			var count = r.ReadListCount();
			var characters = new List<TeamMemberRecord>(count);
			for (var i = 0; i < count; i++)
			{
				characters.Add(TeamMemberRecordCodec.Read(ref r));
				r.ReadU16Be(); // EntryPad
				r.ReadU8(); // EntryTrailer
			}

			r.ReadU8(); // ListTrailer
			r.EnsureAtEnd();

			return new CharacterSearchResult(characters);
		}
	}

	public sealed record CharacterSearchResult(IReadOnlyList<TeamMemberRecord> Characters);
}
