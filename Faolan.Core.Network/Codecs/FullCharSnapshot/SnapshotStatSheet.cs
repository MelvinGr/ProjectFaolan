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
			CharacterStatId.Level, CharacterStatId.Exp, CharacterStatId.MaxExp, CharacterStatId.ExpCurrentLevelBase
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

			// The character's SEX. The fresh skeleton hardcodes 3 (the captured character's), and the corpus shows the
			// slot taking 0, 2 and 3 - so it is per-character, and the enum's values ARE the wire codes.
			yield return (CharacterStatId.Sex, (uint)character.Sex);

			// The level, from the CHARACTER rather than from whichever baseline it landed on. The fresh skeleton
			// hardcodes 1 (so without this the client blocks every level-gated spell and item), and a captured sheet
			// holds the level the character had when it was imported - stale the moment it dings.
			yield return (CharacterStatId.Level, character.Level);

			// The XP bar, authored as a consistent triple instead of being half-reset. `Exp` is WITHIN-level progress
			// (0..MaxExp, resets on ding), so the current level's base is always 0 and MaxExp is the curve's
			// to-next-level cost. The previous build reset MaxExp and the base to the SKELETON's level-1 values for an
			// imported character and left Exp alone, which its own comment admitted left the bar "internally
			// inconsistent" and was only harmless if the client happened to read XP from the live StatIIR instead.
			yield return (CharacterStatId.Exp, character.Experience);
			yield return (CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));
			yield return (CharacterStatId.ExpCurrentLevelBase, 0);
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
