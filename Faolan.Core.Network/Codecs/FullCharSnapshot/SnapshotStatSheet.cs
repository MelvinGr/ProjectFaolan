using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The one place a login snapshot's stat sheet is authored: a baseline, then the character- and session-derived
	///     slots written over it by stat id.
	/// </summary>
	/// <remarks>
	///     The baseline differs by provenance - an imported character has its real captured sheet, a freshly created one
	///     gets <see cref="SubClassStatBlock.FreshSheet" /> - and nothing else does. <see cref="Overrides" /> is one table
	///     applied to both, so a modelled stat's value reaches the wire regardless of where the character came from, which
	///     is what <c>SnapshotStatSheetAuthoringTests</c> asserts.
	/// </remarks>
	public static class SnapshotStatSheet
	{
		/// <summary>
		///     The stats this file authors from the character or the session, as opposed to carrying from whichever baseline
		///     the character landed on.
		/// </summary>
		/// <remarks>
		///     Declared because one of them - <see cref="CharacterStatId.ExpCurrentLevelBase" /> - is authored to the same
		///     value (0) for every character, so no behavioural diff can discover it. The gate in
		///     <c>SnapshotStatSheetAuthoringTests</c> checks both directions, so this cannot drift from
		///     <see cref="Overrides" /> without failing.
		/// </remarks>
		public static IReadOnlyList<CharacterStatId> Authored { get; } =
		[
			CharacterStatId.PlayfieldToken, CharacterStatId.GmLevel, CharacterStatId.Sex,
			CharacterStatId.Level, CharacterStatId.Exp, CharacterStatId.MaxExp, CharacterStatId.ExpCurrentLevelBase,
			CharacterStatId.Money, CharacterStatId.PvPTotalKills, CharacterStatId.PvPTotalDeaths, CharacterStatId.PlayerId,
			// Authored conditionally - only for the levels SkillPointCurve has measured. Declared here regardless, because the
			// slot varies across captured characters and this is where that is accounted for. Open work tracked in Gitea #507.
			CharacterStatId.SkillPoints,

			// The level-derived rating quad - see CombatRatingCurve.
			CharacterStatId.AttackRating, CharacterStatId.DefenseRating, CharacterStatId.MagicRating, CharacterStatId.MagicalResist,
			// The murder-point pool, derived from the character. Open work tracked in Gitea #503.
			CharacterStatId.MurderPoints,
			// Stat 0x0A9 `HeroicMode`, likewise - authored as an opaque wire number, since the client never reads the stat back
			// and so cannot say what a given value selects.
			CharacterStatId.HeroicMode
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
		///     Every slot the snapshot derives rather than carries. Applied to the captured and the fresh baseline alike, so
		///     which one a character got is not observable from here on.
		/// </summary>
		private static IEnumerable<(CharacterStatId Id, uint Value)> Overrides(Character? character, uint? playfieldToken)
		{
			// Session and map state rather than a character stat: the live zone's minted token, so the client, the world objects
			// and the despawns all scope to the same one by construction. A stale captured token hides the in-client abilities
			// tab outright. Falls back to the fresh/beach value when no live token is given (the golden path).
			yield return (CharacterStatId.PlayfieldToken, playfieldToken ?? SubClassStatBlock.SkeletonValueOf((uint)CharacterStatId.PlayfieldToken));

			// The GM privilege mask from the account type - see GmPrivileges; 0 for a normal account, and for a character whose
			// Account navigation is not loaded (create-editor or broadcast).
			yield return (CharacterStatId.GmLevel, character?.Account?.ClientGmLevel() ?? 0);

			if (character == null)
				yield break;

			// PlayerId is the account's, not the character's. Scanned over the whole capture corpus - 111 captures, 146
			// snapshots - the slot takes three values, and they group by account rather than by character: one covers five
			// different characters, the two two-client captures carry exactly two ids, and one character name appears under two
			// ids in different sessions.
			//
			// It is the account id raw, with no base added: UniverseAgent's AckAuthenticate sends the account both as a
			// 0x2712-prefixed u64 and as a raw u32, and it is the raw one this slot repeats - equal in all 105 captures that
			// carry both. The login tier and this sheet must therefore send the same number.
			yield return (CharacterStatId.PlayerId, character.AccountId);

			// The wallet. The slot is a u32 and Character.Money is a ulong, so a balance past uint.MaxValue saturates: the wire
			// cannot express it, and clamping is visible where silently truncating the low word would look like a robbery.
			yield return (CharacterStatId.Money, character.Money > uint.MaxValue ? uint.MaxValue : (uint)character.Money);

			// The PvP career pair, and only that pair. These two slots sit in a contiguous PvP/minigame block
			// (`CaptureSkullWins`/`Losses` are the next ids along), and retail's live level-80 holder carries 239/521 in them,
			// which is a PvP record rather than the record of a character who levelled on mobs.
			//
			// The PvE counters go nowhere. `NPCKills` (0x298) and `NPCDeaths` (0x299) are the only two ids of that 0x290-0x29F
			// run absent from that same holder's 240 stats - the client names them and retail does not populate them - so
			// `character.NpcKills`/`NpcDeaths` stay server-side bookkeeping.
			yield return (CharacterStatId.PvPTotalKills, character.PvpKills);
			yield return (CharacterStatId.PvPTotalDeaths, character.PvpDeaths);

			// The character's sex. The fresh skeleton hardcodes 3, the captured character's, and the corpus shows the slot taking
			// 0, 2 and 3, so it is per-character and the enum's values are the wire codes.
			yield return (CharacterStatId.Sex, (uint)character.Sex);

			// The level, from the character rather than from whichever baseline it landed on. The fresh skeleton hardcodes 1, so
			// without this the client blocks every level-gated spell and item, and a captured sheet holds the level the character
			// had when it was imported - stale the moment it dings.
			yield return (CharacterStatId.Level, character.Level);

			// The XP bar, authored as a consistent triple. All three stats are retail's cumulative form: `Exp` is a running
			// total, `ExpCurrentLevelBase` the total at which this level began and `MaxExp` the total at which the next one does,
			// so the bar fills (Exp-Base)/(MaxExp-Base). The character row stores within-level progress, so the base is added
			// back here.
			var levelBase = LevelingResolver.CumulativeXpForLevel(character.Level);
			yield return (CharacterStatId.Exp, levelBase + character.Experience);
			yield return (CharacterStatId.MaxExp, LevelingResolver.CumulativeXpForLevel(character.Level + 1u));
			yield return (CharacterStatId.ExpCurrentLevelBase, levelBase);

			// The skill-point pool, which is purely level-derived - so like Level above, a captured sheet holds the value for
			// whatever level the character was imported at and goes stale the moment it dings. Deliberately conditional: only 5
			// of the 80 levels have ever been captured (SkillPointCurve.Measured), and the curve has no formula to interpolate
			// with, so an unmeasured level yields nothing and keeps the baseline's own value rather than having a guess written
			// over it. Open work tracked in Gitea #507.
			if (SkillPointCurve.For(character.Level) is { } skillPoints)
				yield return (CharacterStatId.SkillPoints, skillPoints);

			// The rating quad - AttackRating, DefenseRating, MagicRating, MagicalResist - which unlike the skill points above is
			// a formula, `400 x level - 300`, exact on all 2,100 captured records. All four take the same value, so they are
			// emitted from one number. Level-derived like `Level` itself, so a captured sheet's copy is stale the moment the
			// character dings.
			var rating = CombatRatingCurve.For(character.Level);
			foreach (var stat in CombatRatingCurve.Stats)
				yield return (stat, rating);

			// The PvP murder-point pool: stat 0x449, which the client's own LDB string table names "Murder Points", with the
			// wire value the capture shows moving at a player kill. Carried unconditionally - 0 is the correct value for a
			// character that has never murdered anyone, and it is what every captured character holds.
			// Open work tracked in Gitea #503.
			yield return (CharacterStatId.MurderPoints, character.MurderPoints);

			// Stat 0x0A9, which the client's descriptor registry names `HeroicMode`. One captured character carries 1 and 183
			// carry 0, so it is a per-character value. The number is emitted verbatim and is not interpreted: see
			// Character.HeroicMode - the client stores this stat without ever reading it back, so nothing maps a value to a
			// behaviour.
			yield return (CharacterStatId.HeroicMode, character.HeroicMode);
		}

		// Writes a value at every occurrence of a stat id. A stat can appear in both lists, and twice within StatList#2, and
		// leaving one copy behind is what makes a sheet internally inconsistent.
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
