using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Whether a chosen character name is allowed.
	/// </summary>
	/// <remarks>
	///     Two independent rules, and only one of them is retail's own stated number:
	///     <list type="bullet">
	///         <item>
	///             Length. The client says it verbatim - "The length of your character name is invalid. The length of
	///             the name must be between 4 and 16 characters." (LDB 211711[2780]) - and enforces it locally, so a
	///             stock client never sends a name outside it. This is the server-side half of the same rule.
	///         </item>
	///         <item>
	///             Content. The client ships a forbidden-word list (RDB 1065100, imported as <c>NameFilterTerm</c>); a
	///             name that contains one is refused.
	///         </item>
	///     </list>
	///     Retail's own matching rule is not observable - no capture rejects a name, and the client's refusal text
	///     ("The name %s is not valid, please choose another.", LDB 211711[2154]) states no rule. The rule below is
	///     ours, calibrated against an oracle the client supplies: the words it ships in its own NPC and item names
	///     are by construction acceptable English, so a rule that refuses them is too blunt. Refusals over that
	///     2,779-word vocabulary, as the substring floor moves:
	///     <code>
	///     &gt;=3: 36 words refused (assassin, ambassador, occultist, acumen, ...)
	///     &gt;=4: 16      &gt;=5: 5      &gt;=6: 3      &gt;=7: 3
	///     </code>
	///     It plateaus at 6, which is why the floor is 6 and not a rounder number. Below the floor a term must match
	///     the whole name, which is weak - <c>xxassxx</c> passes - but a filter that refuses <c>Assassin</c> is worse
	///     than one that misses a padded three-letter word, and the client's own content is the evidence for which.
	///     The three that survive at 6 are <c>bastardsword</c>, <c>bite</c> and <c>hack</c>, none of them a name.
	///     Reproduce with <c>dotnet run --project Faolan.Importer -- --report name-filter-check</c>.
	///     <para />
	///     The terms are stored hashed, so the match runs the other way round from the obvious one: rather than
	///     scanning terms for a substring of the name, this hashes each of the name's own substrings and asks the set.
	///     A 16-character name has 136 of them, and <c>NameFilterTerm.Length</c> narrows that to the lengths that
	///     occur.
	/// </remarks>
	public static class CharacterNameRules
	{
		public const int MinLength = 4;
		public const int MaxLength = 16;

		/// <summary>
		///     Terms at least this long are matched anywhere in the name; shorter ones must be the whole name. See the
		///     class remarks for the measurement behind the number.
		/// </summary>
		public const int SubstringMinLength = 6;

		public enum Verdict
		{
			Ok,
			TooShort,
			TooLong,
			Forbidden
		}

		/// <summary>The stored form of one forbidden term: lowercase hex SHA-256 of its lowercased text.</summary>
		public static string Hash(string term)
		{
			ArgumentNullException.ThrowIfNull(term);

			return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(term.ToLowerInvariant())));
		}

		/// <summary>
		///     Checks a chosen name against the length rule and the hashed forbidden-term set.
		/// </summary>
		/// <param name="forbiddenLengths">
		///     The set of term lengths present in <paramref name="forbidden" />. Pass an empty set (with an empty
		///     <paramref name="forbidden" />) to run the length rule alone, which is what a server with no imported
		///     list does.
		/// </param>
		public static Verdict Check(string? name, IReadOnlySet<string> forbidden, IReadOnlySet<int> forbiddenLengths)
		{
			ArgumentNullException.ThrowIfNull(forbidden);
			ArgumentNullException.ThrowIfNull(forbiddenLengths);

			var trimmed = (name ?? "").Trim();
			if (trimmed.Length < MinLength)
				return Verdict.TooShort;
			if (trimmed.Length > MaxLength)
				return Verdict.TooLong;

			var lowered = trimmed.ToLowerInvariant();
			if (forbidden.Contains(Hash(lowered)))
				return Verdict.Forbidden;

			foreach (var length in forbiddenLengths)
			{
				if (length < SubstringMinLength || length >= lowered.Length)
					continue;

				// length == lowered.Length is covered by the whole-name test above, so start one shorter.
				for (var start = 0; start + length <= lowered.Length; start++)
				{
					if (forbidden.Contains(Hash(lowered.Substring(start, length))))
						return Verdict.Forbidden;
				}
			}

			return Verdict.Ok;
		}

		/// <summary>
		///     Which of a client list's terms are worth storing: a term longer than a name can be never matches, and
		///     one shorter than three characters matches almost everything. Non-Latin terms are kept - a name arrives
		///     as Latin1 off the wire so they cannot match today, but dropping them here would bake that in.
		/// </summary>
		public static bool IsUsableTerm(string term)
		{
			return !string.IsNullOrWhiteSpace(term)
			       && term.Trim().Length is >= 3 and <= MaxLength
			       && !ContainsSeparator(term.Trim());
		}

		// A term with a space or punctuation in it cannot be a substring of a name, because a name is one token.
		private static bool ContainsSeparator(string term)
		{
			foreach (var c in term)
			{
				if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is UnicodeCategory.OtherPunctuation
					    or UnicodeCategory.DashPunctuation or UnicodeCategory.MathSymbol)
					return true;
			}

			return false;
		}
	}
}
