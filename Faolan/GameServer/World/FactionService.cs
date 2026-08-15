using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// The Khitai REPUTATION engine (Gitea #310): what a kill is worth to a character's standing, and telling the
	// client about it.
	//
	// Every number it applies is the client's own - the payout matrix from `data/FactionRelations.xml` (RDB 1010701)
	// and the bounds/thresholds from the tuning sheet (RDB 1000516, `FactionLadder`). #310 was filed as blocked on
	// Khitai captures; it never was, and the corpus still carries zero faction packets in either direction, so the
	// wire form comes from the client's method table rather than from a dump. See docs/subsystems/factions.md.
	//
	// ⚠️ ONE trigger is wired: a kill. The client also drives standing from SCRY SCRIPTS (`changefactionpoints`,
	// `changefactionrank`) and from a `ApplyFactionRankGameAction` element, i.e. quests - those ride machinery that
	// exists but has no faction consumer yet, and are deliberately not invented here.
	public sealed class FactionService(IFactionRepository database, ILogger logger, FactionService.SendStanding send)
	{
		// The one outbound op this service needs, as a delegate seam rather than a whole listener - the shape
		// CLAUDE.md prescribes for a single shared op (`ClientForCharacter` into GuildService is the precedent).
		// It sends BOTH messages, because the client keeps the standing bar and the rank TITLE in different places
		// and a server that moved only one would leave the other showing whatever it was last told.
		public delegate Task SendStanding(INetworkClient client, uint factionId, int points, uint rank);

		// The encounter tier a plain solo kill pays at. Tiers 1..5 (solo boss, group normal/boss, raid normal/boss)
		// are in the table and are NOT selectable yet: the tier is a property of the ENCOUNTER - how many players
		// engaged it and whether the creature is a boss - and the simulation models neither. Paying tier 0 for
		// everything under-grants a group kill rather than over-granting it, which is the safe direction, and the
		// file's own "proportional to 60" rule means tier 0 is exactly one player's share of the group rate.
		public const int SoloNormalTier = 0;

		/// <summary>
		///     Awards the reputation one kill is worth, and pushes every standing it moved. No-op when the victim
		///     belongs to no faction, which is every base-game NPC - the faction ids are Khitai's.
		/// </summary>
		/// <remarks>
		///     ⭐ A kill moves SEVERAL factions, not one: the payout list costs the victim's own faction and grants the
		///     same amount to its rival. That is the whole mechanic, and it is why this takes the award LIST rather
		///     than a single delta.
		/// </remarks>
		public async Task<IReadOnlyList<(uint FactionId, int Points, uint Rank)>> AwardForKill(INetworkClient killer, uint? victimFactionId, int tier = SoloNormalTier)
		{
			if (victimFactionId is not { } faction || killer.Session.Character is not { } character)
				return [];

			var awards = await database.GetFactionKillAwards(faction, tier);
			if (awards.Count == 0)
				return [];

			var standings = await database.GetCharacterFactionStandings(character.Id);
			var moved = new List<(uint FactionId, int Points, uint Rank)>();

			foreach (var award in awards)
			{
				var before = standings.GetValueOrDefault(award.FactionId);
				var after = FactionLadder.Apply(before, award.Points);

				// A standing already pinned at a bound does not move, and a no-op must not be written OR sent: the
				// client would redraw an unchanged bar, and the row would churn on every kill at the ceiling.
				if (after == before)
					continue;

				await database.SetCharacterFactionStanding(character.Id, award.FactionId, after);

				var rank = (uint)FactionLadder.RankFor(after);
				moved.Add((award.FactionId, after, rank));
				await send(killer, award.FactionId, after, rank);

				if (FactionLadder.RankFor(before) != rank)
					logger.LogInformation("Faction: {Character} reached rank {Rank} with faction {Faction} ({Points} points).",
						character.Name, rank, award.FactionId, after);
			}

			return moved;
		}

		/// <summary>
		///     Pushes every standing a character already holds - the world-entry restore, so the faction window is
		///     populated before the player opens it.
		/// </summary>
		/// <remarks>
		///     ⚠️ Sends only the factions the character has a ROW for. A faction at neutral has no row and needs no
		///     message: zero is what the client shows for one it has never been told about, so seeding sixteen zeroes
		///     would be sixteen packets to say nothing.
		/// </remarks>
		public async Task SendAll(INetworkClient client)
		{
			if (client.Session.Character is not { } character)
				return;

			foreach (var (factionId, points) in await database.GetCharacterFactionStandings(character.Id))
				await send(client, factionId, points, (uint)FactionLadder.RankFor(points));
		}
	}
}
