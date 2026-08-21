using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	// Byte-exact codec for the 0x2010 DispatchCharacterSearchResult body - the "who" window's result list, the answer
	// to 0x200A CharacterSearch. Decoded from the five captured bodies (CharacterSearchResultTests):
	//
	//   [u32 (count+1)*0x3F1]  { count x entry }  [u8 i_bMoreHits]
	//   entry = [TeamMemberRecord][u16 0][u8 0x04]
	//
	// It lives beside TeamMemberRecordCodec rather than in a search folder because that record IS the payload: the
	// "who" result reuses the team subsystem's member descriptor verbatim, wrapped in three trailing bytes. Finding
	// that out is what identified the record's LEVEL field - see TeamMemberRecord's comment - because these bodies
	// carry several characters at once where the team captures only ever carried two, both level 5.
	//
	// The trailing byte is `i_bMoreHits` - the paging flag, not a list terminator. It reads as one because it is 0 in
	// every captured body, and because a search that fits in one page has nothing more to report; the client's own
	// dispatcher settles it, asserting "stream bad after 'i_cResult'" and then "after 'i_bMoreHits'" as it reads the
	// vector and then one byte (#889, found on the DispatchTeamSearchResult twin, whose reply is the same pair). It
	// must be emitted even for an empty result or the client reads the empty frame and runs off the end (#408).
	public static class CharacterSearchResultCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchCharacterSearchResult;

		// Per-entry trailer. The u16 is 0 in every captured entry; the 0x04 is constant across all five bodies and all
		// of their entries, so it is carried rather than named. [V0]
		private const ushort EntryPad = 0x0000;
		private const byte EntryTrailer = 0x04;

		public static byte[] Build(CharacterSearchResult value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Characters.Count));
			foreach (var c in value.Characters)
			{
				TeamMemberRecordCodec.Write(s, c);
				s.WriteUInt16(EntryPad).WriteByte(EntryTrailer);
			}

			return s.WriteByte(value.MoreHits).ToArray();
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

			var moreHits = r.ReadU8() != 0;
			r.EnsureAtEnd();

			return new CharacterSearchResult(characters, moreHits);
		}
	}

	// `MoreHits` answers the request's i_nSkipResults: false in every captured body, because every captured search
	// fitted in one page. Sending false while dropping results at the page cap would tell the client it had seen
	// everything, so the page and the flag are set together.
	public sealed record CharacterSearchResult(IReadOnlyList<TeamMemberRecord> Characters, bool MoreHits = false);
}
