using System.Collections.Immutable;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The PvP minigame arenas and what each one is played for (#146).
	/// </summary>
	/// <remarks>
	///     Server-side content only — nothing here goes on the wire. The whole minigame message family
	///     (<c>StartMinigame</c>, the score dispatches, the capture-zone messages) has <b>zero</b> occurrences
	///     across the corpus, so a match cannot be run without inventing its lifecycle bodies. What this holds is
	///     the part that is known: which playfields are arenas, which mode each runs, and one arena's full rules.
	///     <para>
	///         ⚠️ <b>The two LDB catalogs are NOT joinable.</b> Category 800 names arenas (Annihilation, Capture the
	///         Skull, Bossonian Marches, Cragsfell, Colosseum, Drunken Brawling) and category 802 names rule sets
	///         (Annihilate Opposing Team, Conquest, Capture The Skull, Drunken Brawling, plus two
	///         <i>Training …</i> variants) — but they are separate id spaces: key 2 is <i>"Capture the Skull"</i> in
	///         800 and <i>"Conquest"</i> in 802. Joining them on the key would produce confident nonsense, so this
	///         type keys on the PLAYFIELD instead, which is the identity both the RDB table and the rules text
	///         agree on.
	///     </para>
	///     <para>
	///         The five arenas are RDB <b>1000512</b>'s whole contents; the mode comes from LDB <b>10015</b>, which
	///         writes out per-arena rules. Only two of the five have a 10015 entry, and the other three are listed
	///         with <see cref="MinigameMode.Unknown" /> rather than guessed — the same rule the group-finder
	///         catalog follows for its landmark-addressed rows.
	///     </para>
	/// </remarks>
	public static class MinigameCatalog
	{
		/// <summary>
		///     Every minigame playfield RDB 1000512 declares, with the mode where the client states one.
		/// </summary>
		public static readonly ImmutableDictionary<uint, MinigameArena> Arenas = new MinigameArena[]
		{
			// ⭐ The one arena whose rules are written out in full - see TotemTorrentRules.
			new(5002, "Totem Torrent", MinigameMode.TeamAnnihilation),
			new(5005, "Hallowed Vaults", MinigameMode.Unknown),
			new(5006, "The Call of Jhebbal Sag", MinigameMode.Unknown),
			new(5012, "Lost Temple", MinigameMode.Unknown),
			// [LDB 10015/5201: "Blood Ravine. Capture the Skull."]
			new(5201, "Blood Ravine", MinigameMode.CaptureTheSkull)
		}.ToImmutableDictionary(a => a.MapId);

		/// <summary>
		///     Totem Torrent's rules, quoted from LDB 10015/500200 and the only complete arena ruleset the client
		///     ships. Kept as named facts rather than prose because each one is a distinct thing a match engine has
		///     to implement, and because "most kills wins" and "kills end the match" are different rules that the
		///     same sentence could be read as.
		/// </summary>
		/// <remarks>
		///     <i>"Team annihilation: the goal of the game is kill all members of the opposing force. Join one of two
		///     teams, red or blue. Players will resurrect as long as their totem is not destroyed. On either side of
		///     the map are two control points. Capture these for your team by standing near them. Once captured,
		///     these increase your totem's defense. There is a heal-zone in the middle of the play area that heals
		///     all characters. If both teams are alive when the time is up, the team with the most kills wins."</i>
		/// </remarks>
		public static class TotemTorrentRules
		{
			/// <summary>Two teams, red and blue.</summary>
			public const int Teams = 2;

			/// <summary>Two control points, one on either side of the map.</summary>
			public const int ControlPoints = 2;

			/// <summary>
			///     A player respawns for as long as their team's totem stands — so the totem is the team's respawn
			///     ANCHOR, and destroying it is what actually ends a side.
			/// </summary>
			public const bool RespawnRequiresYourTotem = true;

			/// <summary>Holding a control point raises your own totem's defense; it is not a scoring tick.</summary>
			public const bool ControlPointsHardenTheTotem = true;

			/// <summary>A zone in the middle of the arena heals every character in it, both teams alike.</summary>
			public const bool HasCentralHealZone = true;

			/// <summary>
			///     The tiebreak, and only the tiebreak: it applies <i>"if both teams are alive when the time is
			///     up"</i>. A side wiped before that loses outright regardless of kills.
			/// </summary>
			public const bool MostKillsWinsOnTime = true;
		}

		/// <summary>The arena a playfield is, or null when it is not one.</summary>
		public static MinigameArena? ArenaFor(uint mapId)
		{
			return Arenas.TryGetValue(mapId, out var arena)
				? arena
				: null;
		}

		/// <summary>Whether a playfield is a PvP minigame arena at all.</summary>
		public static bool IsArena(uint mapId)
		{
			return Arenas.ContainsKey(mapId);
		}
	}

	/// <summary>One minigame arena.</summary>
	/// <param name="MapId">Its playfield, which is how RDB 1000512 and the rules text both identify it.</param>
	/// <param name="Name">The playfield's name, matching our own Maps row.</param>
	/// <param name="Mode">What it is played for, or <see cref="MinigameMode.Unknown" /> where the client says nothing.</param>
	public sealed record MinigameArena(uint MapId, string Name, MinigameMode Mode);

	/// <summary>
	///     The rule sets, named for the client's own LDB 802 strings. Deliberately does NOT enumerate every 802 row
	///     — the <i>Training</i> variants and the arena-named entries have no established mapping to a playfield,
	///     and a member here is a claim that some arena runs it.
	/// </summary>
	public enum MinigameMode
	{
		/// <summary>No 10015 rules entry for this arena — the mode is not stated anywhere we have read.</summary>
		Unknown = 0,

		/// <summary>LDB 802 "Annihilate Opposing Team" — Totem Torrent's mode.</summary>
		TeamAnnihilation,

		/// <summary>LDB 802/800 "Capture The Skull" — Blood Ravine's mode.</summary>
		CaptureTheSkull
	}
}
