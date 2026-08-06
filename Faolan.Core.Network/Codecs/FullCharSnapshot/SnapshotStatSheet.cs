using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The ONE place a login snapshot's stat sheet is authored (#447): a baseline, then the character- and
	///     session-derived slots written over it BY STAT ID.
	/// </summary>
	/// <remarks>
	///     The baseline differs by provenance - an imported character has its real captured sheet, a freshly-created one
	///     gets <see cref="SubClassStatBlock.FreshSheet" /> - but <b>nothing else does</b>. That is the point: the
	///     previous build had two divergent authoring paths, each patching a different set of slots, so an override
	///     added to one silently did nothing on the other. Setting the GM Level stat on the static fresh list, for
	///     instance, never reached the wire for a single real (imported) login.
	///     <para>
	///         So <see cref="Overrides" /> is one table applied to both, and the acceptance test for this file is
	///         literally that: a modelled stat's value reaches the wire regardless of where the character came from.
	///     </para>
	/// </remarks>
	public static class SnapshotStatSheet
	{
		/// <summary>
		///     The stats this file authors from the character or the session, as opposed to carrying from whichever
		///     baseline the character landed on. Declared because one of them - <see cref="CharacterStatId.ExpCurrentLevelBase" />
		///     - is authored to the SAME value (0) for every character, so no behavioural diff can discover it; the
		///     gate in SnapshotStatSheetAuthoringTests checks both directions, so this cannot drift from
		///     <see cref="Overrides" /> without failing.
		/// </summary>
		public static IReadOnlyList<CharacterStatId> Authored { get; } =
		[
			CharacterStatId.PlayfieldToken, CharacterStatId.GmLevel, CharacterStatId.Sex,
			CharacterStatId.Level, CharacterStatId.Exp, CharacterStatId.MaxExp, CharacterStatId.ExpCurrentLevelBase,
			CharacterStatId.Money, CharacterStatId.Kills, CharacterStatId.Deaths, CharacterStatId.PlayerId,
			// Authored CONDITIONALLY - only for the levels SkillPointCurve has actually measured (#507). Declared here
			// regardless, because the slot varies across captured characters and this is where that is accounted for.
			CharacterStatId.SkillPoints,
			// The murder-point pool, now derived from the character rather than carried as a fixed 0 (#503).
			CharacterStatId.MurderPoints,
			// Stat 0x0A9 `ChosenGod`, likewise (#553) - authored as an opaque wire number, since the client never
			// reads the stat back and there is no religion system to interpret it as.
			CharacterStatId.ChosenGod
		];

		/// <summary>
		///     The full sheet - <see cref="SubClassStatBlock.SheetSize" /> <c>(statId, value)</c> pairs, StatList#1's
		///     displayed stats followed by StatList#2's single copy - ready for
		///     <see cref="SubClassStatBlock.CreateBlock" />.
		/// </summary>
		/// <param name="captured">The character's imported sheet, or null/empty for the create path.</param>
		public static List<(uint Id, uint Value)> For(Character? character, uint? playfieldToken,
			IReadOnlyList<(CharacterStatId Id, uint Value)>? captured)
		{
			var sheet = captured is { Count: > 0 }
				? captured.Select(s => ((uint)s.Id, s.Value)).ToList()
				: SubClassStatBlock.FreshSheet();

			foreach (var (id, value) in Overrides(character, playfieldToken))
				Set(sheet, (uint)id, value);

			return sheet;
		}

		/// <summary>
		///     Every slot the snapshot derives rather than carries. Applied to the captured and the fresh baseline
		///     alike, so which one a character got is not observable from here on.
		/// </summary>
		private static IEnumerable<(CharacterStatId Id, uint Value)> Overrides(Character? character, uint? playfieldToken)
		{
			// SESSION/MAP state, not a character stat: the LIVE zone's minted token, so the client, the world objects
			// and the despawns all scope to the same one by construction. A stale captured token HIDES the in-client
			// abilities tab outright. Falls back to the fresh/beach value when no live token is given (golden path).
			yield return (CharacterStatId.PlayfieldToken, playfieldToken ?? SubClassStatBlock.SkeletonValueOf((uint)CharacterStatId.PlayfieldToken));

			// The GM privilege MASK from the account type (#449) - see GmPrivileges; 0 for a normal account, and for a
			// character whose Account navigation is not loaded (create-editor / broadcast).
			yield return (CharacterStatId.GmLevel, character?.Account?.Type.ToClientGmLevel() ?? 0);

			if (character == null)
				yield break;

			// PlayerId is the ACCOUNT's, not the character's (#478). Scanned over the whole capture corpus - 111
			// captures, 146 snapshots - the slot takes three values, and they group by account rather than by
			// character: one covers five different characters, the two TWO-CLIENT captures carry exactly two ids,
			// and one character name appears under two ids in different sessions. The static list carried one of
			// those three, so every character we created logged in wearing a stranger's id.
			// ...and it is the account id RAW, with no base added: UniverseAgent's AckAuthenticate sends the account
			// both as a 0x2712-prefixed u64 and as a raw u32, and it is the RAW one this slot repeats - equal in all
			// 105 captures that carry both. The login tier and this sheet must therefore send the same number.
			yield return (CharacterStatId.PlayerId, character.AccountId);

			// The WALLET. GameServerListener.WorldEntry pushes an interim 0x2005 at login with the note that "retail
			// does NOT push a 0x2005 at login (the initial balance presumably rides the FullChar snapshot, location
			// undecoded)" - this is that location. The slot is a u32 and Character.Money is a ulong, so a balance
			// past uint.MaxValue saturates: the wire cannot express it, and clamping is visible where silently
			// truncating the low word would look like a robbery.
			yield return (CharacterStatId.Money, character.Money > uint.MaxValue ? uint.MaxValue : (uint)character.Money);

			// The kill/death counters the sheet shows. They were on the not-modelled list as "not tracked", which was
			// true of the COLUMNS rather than of the events: the server already knew, at the XP award and at the
			// death persist, and was throwing the count away.
			yield return (CharacterStatId.Kills, character.Kills);
			yield return (CharacterStatId.Deaths, character.Deaths);

			// The character's SEX. The fresh skeleton hardcodes 3 (the captured character's), and the corpus shows the
			// slot taking 0, 2 and 3 - so it is per-character, and the enum's values ARE the wire codes.
			yield return (CharacterStatId.Sex, (uint)character.Sex);

			// The level, from the CHARACTER rather than from whichever baseline it landed on. The fresh skeleton
			// hardcodes 1 (so without this the client blocks every level-gated spell and item), and a captured sheet
			// holds the level the character had when it was imported - stale the moment it dings.
			yield return (CharacterStatId.Level, character.Level);

			// The XP bar, authored as a consistent triple instead of being half-reset. All three stats are retail's
			// CUMULATIVE form (#297): `Exp` is a running total, `ExpCurrentLevelBase` the total at which this level
			// began and `MaxExp` the total at which the next one does, so the bar fills (Exp-Base)/(MaxExp-Base).
			// The character row stores WITHIN-level progress, so the base is added back here.
			// (The previous build reset MaxExp and the base to the SKELETON's level-1 values for an imported character
			// and left Exp alone, which its own comment admitted left the bar "internally inconsistent"; the build
			// after that authored a consistent but non-retail base-0 triple.)
			var levelBase = LevelingResolver.CumulativeXpForLevel(character.Level);
			yield return (CharacterStatId.Exp, levelBase + character.Experience);
			yield return (CharacterStatId.MaxExp, LevelingResolver.CumulativeXpForLevel(character.Level + 1u));
			yield return (CharacterStatId.ExpCurrentLevelBase, levelBase);

			// The SKILL-POINT pool (#507), which is purely level-derived - so like Level above, a captured sheet holds
			// the value for whatever level the character was imported at and goes stale the moment it dings.
			// Deliberately conditional: only 5 of the 80 levels have ever been captured (SkillPointCurve.Measured), and
			// the curve has no formula to interpolate with, so an unmeasured level yields NOTHING and keeps the
			// baseline's own value rather than having a guess written over it.
			if (SkillPointCurve.For(character.Level) is { } skillPoints)
				yield return (CharacterStatId.SkillPoints, skillPoints);

			// The PvP murder-point pool (#503). Stat 0x449, which the client's own LDB string table names "Murder
			// Points"; the wire value is what the capture shows moving at a player kill. Carried unconditionally - 0 is
			// the correct value for a character that has never murdered anyone, and it is what every captured character
			// holds, so this replaces the fixed 0 with the same number derived from the column that now tracks it.
			yield return (CharacterStatId.MurderPoints, character.MurderPoints);

			// Stat 0x0A9, the one the client's name table calls `ChosenGod` (#553). One captured character carries 1
			// and 183 carry 0, so the fixed 0 was a per-character value shipped as a constant. The number is emitted
			// VERBATIM and is not interpreted: see Character.ChosenGod - AoC has no player religion system, and the
			// client stores this stat without ever reading it back, so there is nothing to map it to.
			yield return (CharacterStatId.ChosenGod, character.ChosenGod);
		}

		// Writes a value at EVERY occurrence of a stat id. A stat can appear in both lists (and twice within
		// StatList#2), and leaving one copy behind is what makes a sheet internally inconsistent.
		private static void Set(List<(uint Id, uint Value)> sheet, uint statId, uint value)
		{
			for (var i = 0; i < sheet.Count; i++)
			{
				if (sheet[i].Id == statId)
					sheet[i] = (statId, value);
			}
		}
	}
}
