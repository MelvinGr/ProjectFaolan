using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One forbidden term from the client's own name/profanity list, stored as a hash rather than as the word.
	//
	// Where it comes from. RDB 1065100 - a single 19.4 KB record the client ships, framed
	// `[u32 typeId][u32 0][u32 1][u32 count]` then `count` × `[u32 byteLen][UTF-8 bytes]`. It is genuinely
	// multilingual (German, English, Spanish, French, Polish and a large Korean block) and most terms appear twice.
	// `rdb-name-filter` imports it.
	//
	// Why hashed. This repo is meant to go public, so the word list itself must not land in it - and `faolan.db`
	// is committed with `faolan.sql` mirroring its rows as reviewable text, which a plaintext column would fill
	// with slurs. A hash keeps the check exact while keeping the words out. It costs nothing at the point of use:
	// matching a term as a substring of a candidate name works by hashing the name's own substrings and looking
	// each one up, rather than by scanning the terms (see `CharacterNameRules`).
	//
	// The key is the hash — lowercase hex SHA-256 of the lowercased term — so a re-import of the same client is a
	// no-op rather than a renumbering (the model's natural-key rule).
	[PrimaryKey(nameof(TermHash))]
	public class NameFilterTerm
	{
		// SHA-256 of the invariant-lowercased term, as 64 lowercase hex characters.
		public string TermHash { get; set; } = "";

		// The term's length in characters. Stored so a candidate name only has to enumerate substrings of lengths
		// that actually occur — without it every check would hash all 136 substrings of a 16-character name.
		// It says nothing about which word this is.
		public int Length { get; set; }
	}
}
