using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Team
{
	/// <summary>
	///     The <c>DispatchCharacterSearchResult</c> body - the "who" window's result list, the answer to
	///     <c>CharacterSearch</c>. Decoded from the five captured bodies (<c>CharacterSearchResultTests</c>).
	/// </summary>
	/// <remarks>
	///     <code>
	///     [list frame]  { count x entry }  [u8 i_bMoreHits]
	///     entry = [TeamMemberRecord][u16 0][u8 0x04]
	///     </code>
	///     <para>
	///         It lives beside <see cref="TeamMemberRecordCodec" /> rather than in a search folder because that record is
	///         the payload: the "who" result reuses the team subsystem's member descriptor verbatim, wrapped in three
	///         trailing bytes. That is also what identified the record's level field, because these bodies carry several
	///         characters at once where the team captures only ever carried two, both level 5.
	///     </para>
	///     <para>
	///         The trailing byte is <c>i_bMoreHits</c>, the paging flag rather than a list terminator. It is 0 in every
	///         captured body, and the client's own dispatcher settles it, asserting "stream bad after 'i_cResult'" and
	///         then "after 'i_bMoreHits'" as it reads the vector and then one byte. It must be emitted even for an empty
	///         result, or the client reads the empty frame and runs off the end.
	///     </para>
	/// </remarks>
	public static class CharacterSearchResultCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchCharacterSearchResult;

		// Per-entry trailer. The u16 is 0 in every captured entry; the 0x04 is constant across all five bodies and all of
		// their entries, so it is carried rather than named. [V0]
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

	/// <remarks>
	///     <c>MoreHits</c> answers the request's <c>i_nSkipResults</c>: false in every captured body, because every
	///     captured search fitted in one page. Sending false while dropping results at the page cap would tell the client
	///     it had seen everything, so the page and the flag are set together.
	/// </remarks>
	public sealed record CharacterSearchResult(IReadOnlyList<TeamMemberRecord> Characters, bool MoreHits = false);
}
