using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Feats;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Feats
{
	// Feat TRAINING + RESET (untrain-all). Feats ride the player spawn's pre-IIR list (see PlayerSpawnScaffoldRows /
	// docs/subsystems/feats.md); this is the live protocol that mutates that set:
	//   - TRAIN: c2s FeatIIR (0x28DA68A0) state 0x04 [feat ids] -> server adds them to CharacterFeat -> s2c FeatIIR
	//     state 0x05 (echoes the list). The client renders the trained feats off the ack; they persist for next spawn.
	//   - RESET (untrain all): retail shows a confirm dialog (DispatchFeatUntrainMessageBox -> FeatUntrainAnswer) and
	//     then RELOADS the world (the "loading screen" the capture is named for) so the fresh spawn carries the cleared
	//     list. We clear CharacterFeat and reload via the change-map re-entry (a teleport to the current spot).
	// Decoded from the hakotep untrain/train captures; #251. Extracted from GameServerListener as a composed
	// collaborator (#200): the world-reload rides a teleport delegate seam (the listener's DoTeleport).
	public sealed class FeatService
	{
		private readonly Func<uint, uint, Vector3?, Task> _teleport;
		private readonly ICharacterProgressionRepository _database;

		// The CATALOG half, for one question: what the character's LEVEL grants, which is the guard on the untrain's
		// spell revoke (#920). A second narrow interface rather than widening `_database` to `IDatabaseRepository` -
		// the question genuinely belongs to the catalog, and the same instance answers both.
		private readonly ICatalogRepository _catalog;
		private readonly IChatServerDispatcher _chat;
		private readonly ILogger _logger;

		// The two halves of the live SPELLBOOK - teach (DispatchLearnSpell 0x200A) and revoke (DispatchForgetSpell
		// 0x200B) - are the listener's ops, the same pair `ILevelUpOutbound` exposes for the ding and `/magic forget`
		// uses (#915). They arrive as delegates rather than a port because this service needs exactly these two, and
		// they are one cluster: a feat that teaches a spell has to be able to take it back (#920).
		private readonly Func<INetworkClient, uint, Task> _learnSpell;
		private readonly Func<INetworkClient, uint, Task> _forgetSpell;

		// #150: an ALTERNATE-ADVANCEMENT perk arrives here as an ordinary feat train carrying a perk id - the client's
		// `TrainAAFeat` goes through the same `FeatHolder` train virtual a class feat does, which is why AA needs no
		// message of its own. What differs is the PRICE: a perk is charged to a token pool, never to the class
		// feat-point budget.
		private readonly AlternateAdvancementService _alternateAdvancement;

		public FeatService(ICharacterProgressionRepository database, ICatalogRepository catalog, ILogger logger, IChatServerDispatcher chat, Func<uint, uint, Vector3?, Task> teleport,
			Func<INetworkClient, uint, Task> learnSpell, Func<INetworkClient, uint, Task> forgetSpell, AlternateAdvancementService alternateAdvancement)
		{
			_alternateAdvancement = alternateAdvancement;
			_database = database;
			_catalog = catalog;
			_logger = logger;
			_chat = chat;
			_teleport = teleport;
			_learnSpell = learnSpell;
			_forgetSpell = forgetSpell;
		}

		// .feat list | .feat train <id> [<id>...] | .feat reset  - the test bootstrap for the feat subsystem (the retail
		// trigger is the client's Feats window with its point economy, which we can't drive from the server). train/reset
		// reload the world so the change shows immediately.
		public async Task FeatCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;

			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var verb = parts.Length > 0
				? parts[0].ToLowerInvariant()
				: "list";

			switch (verb)
			{
				case "list":
				{
					var feats = await _database.GetCharacterFeats(character.Id);
					await _chat.SendSystemMessage(character.Id, feats.Length == 0
						? "No trained feats."
						: $"{feats.Length} feat(s): {string.Join(", ", feats)}");
					return;
				}

				case "train":
				{
					var ids = parts
						.Skip(1)
						.Select(p => uint.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
							? v
							: 0u)
						.Where(v => v != 0)
						.ToArray();
					if (ids.Length == 0)
					{
						await _chat.SendSystemMessage(character.Id, "Usage: .feat train <id> [<id> ...]");
						return;
					}

					var added = await TrainWithinBudget(client, character.Level, ids);
					if (added == RejectedAndExplained)
						return;

					if (added < 0)
					{
						await _chat.SendSystemMessage(character.Id, $"Not enough feat points (budget {FeatRules.PointBudget(character.Level)} at level {character.Level}).");
						return;
					}

					await _chat.SendSystemMessage(character.Id, $"Trained {added} new feat(s); reloading...");
					await _teleport(character.Id, character.MapId, character.Position);
					return;
				}

				case "reset":
				{
					await _chat.SendSystemMessage(character.Id, "Resetting feats; reloading...");
					await ResetFeats(client, character); // GM reset bypasses the confirm dialog
					return;
				}

				default:
				{
					await _chat.SendSystemMessage(character.Id, "Usage: .feat list | .feat train <id> ... | .feat reset");
					return;
				}
			}
		}

		// c2s FeatIIR (0x2000 carrier). rawPayload = the whole 0x2000 sub-payload starting at the [u32 length] prefix;
		// the FeatIIR body (from the sub-opcode) is everything after that prefix.
		public async Task HandleFeatTrain(INetworkClient client, byte[] rawPayload)
		{
			FeatIIR request;
			try
			{
				request = FeatCodec.Parse(rawPayload.AsSpan(4));
			}
			catch (CodecParseException e)
			{
				// The codec is STRICT (#530 rule 4), so a body we cannot account for throws rather than decoding into
				// something plausible. Caught HERE because a malformed c2s frame is the peer's problem, not a reason to
				// tear the session down - but it is logged with the offset the reader stopped at.
				_logger.LogDebug("Unparseable FeatIIR ({Length} bytes): {Reason}", rawPayload.Length, e.Message);
				return;
			}

			if (request.State != FeatCodec.StateTrainRequest)
			{
				_logger.LogDebug("FeatIIR state 0x{State:X2} (not a train request) - ignored.", request.State);
				return;
			}

			var handle = request.CharHandle;
			var featIds = request.FeatIds;

			var character = client.Session.Character;

			var added = await TrainWithinBudget(client, character.Level, featIds);
			if (added == RejectedAndExplained)
				return; // the perk charge already named the pool and the shortfall

			if (added < 0)
			{
				// Over the feat-point budget (safety net; the client normally pre-validates). Don't add or ack.
				await _chat.SendSystemMessage(character.Id, "Not enough feat points to train that.");
				return;
			}

			// Ack: echo the requested id list back (state 0x05), byte-for-byte as retail does. The client renders the
			// trained feats off this; the next spawn regenerates them from the persisted CharacterFeat rows.
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(FeatCodec.Build(handle, FeatCodec.StateAck, featIds))));

			_logger.LogInformation("Character {CharacterId} trained {Added} feat(s) [{Ids}].", character.Id, added, string.Join(",", featIds));
		}

		// c2s FeatUntrainAnswer (0x203E): the confirm-dialog reply. Reset ALL feats only when the player CONFIRMED and
		// the echoed token matches the outstanding request (guards against a stray/replayed answer).
		public async Task HandleFeatUntrainAnswer(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			FeatUntrainAnswer reply;
			try
			{
				reply = FeatUntrainAnswerCodec.Parse(body.ReadRemainingArray());
			}
			catch (CodecParseException e)
			{
				_logger.LogDebug("Unparseable FeatUntrainAnswer: {Reason}", e.Message);
				return;
			}

			var pending = client.Session.PendingFeatUntrainToken;
			client.Session.PendingFeatUntrainToken = null;
			if (reply.Answer != FeatUntrainAnswerCodec.AnswerConfirm || pending == null || reply.Echo.Token != pending)
			{
				_logger.LogDebug("Feat untrain answer {Answer:X2} (token {Token:X8}, pending {Pending:X8}) - not confirming.", reply.Answer, reply.Echo.Token, pending ?? 0);
				return;
			}

			await ResetFeats(client, character);
		}

		// c2s UntrainFeat (0x2041): the client clicked "untrain all feats". Retail answers with a CONFIRM messagebox
		// (DispatchFeatUntrainMessageBox 0x2064) and only resets once the client confirms - so we send the dialog and
		// remember its token; the reset happens in HandleFeatUntrainAnswer. No feats -> nothing to confirm.
		public async Task HandleFeatUntrainRequest(INetworkClient client)
		{
			var character = client.Session.Character;

			var feats = await _database.GetCharacterFeats(character.Id);
			if (feats.Length == 0)
				return;

			var token = character.Id; // a per-request token the client echoes back in the answer (player handle == id)
			client.Session.PendingFeatUntrainToken = token;

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchFeatUntrainMessageBox, FeatUntrainMessageBoxCodec.Build(1, 0, token))); // kind 1 = untrain-all; value 0 = free (no cost model yet)

			_logger.LogInformation("Sent feat untrain-all confirm to character {CharacterId} ({Count} feats).", character.Id, feats.Length);
		}

		// Clear ALL of the character's trained feats and reload the world so the fresh spawn carries the cleared list
		// (the "loading screen" the retail untrain triggers). Shared by the confirm-dialog answer and the .feat GM reset.
		private async Task ResetFeats(INetworkClient client, Character character)
		{
			// Read the set BEFORE clearing it - it is what says which spells to take back (#920).
			var held = await _database.GetCharacterFeats(character.Id);
			var cleared = await _database.ClearCharacterFeats(character.Id);
			_logger.LogInformation("Character {CharacterId} reset {Cleared} feat(s).", character.Id, cleared);
			if (cleared == 0)
				return;

			await RevokeSpellsTaughtBy(client, character, held);
			await _teleport(character.Id, character.MapId, character.Position);
		}

		/// <summary>
		///     Takes back the spells the untrained feats had TAUGHT (#920) - the mirror of
		///     <see cref="GrantSpellsTaughtBy" />, which is what made this a one-way door: a spell a feat handed over
		///     survived the untrain of that feat for ever, because world entry re-grants the whole
		///     <c>CharacterSpells</c> set.
		/// </summary>
		/// <remarks>
		///     ⚠️ <b>A spell the character's LEVEL also grants is kept.</b> Retail's untrain-all refunds spent points;
		///     it does not un-grant what levelling handed over, and the two paths overlap - the tree and the
		///     level ladder can name the same spell. `GetClassSpellsUnlockedBetween(class, 0, level)` is what the ding
		///     path itself teaches from, so asking it is asking the same question the next ding would answer, rather
		///     than a second rule that could drift from it.
		///     <para />
		///     ⛔ The ticket's stated blocker - <i>"ClearCharacterFeats wipes every feat row including the AUTOMATIC
		///     ones, so revoking every feat-taught spell would strip the character's level-earned spells too"</i> -
		///     is answered TWICE over, which is why the revoke is safe off the cleared set:
		///     <list type="number">
		///         <item><description>since #862 an auto-trained feat is never written to <c>CharacterFeat</c> at all
		///         (0 of the 56 rows in the shipped database are autotrained), so "every feat row" and "every
		///         purchased feat" are already the same set;</description></item>
		///         <item><description>and <c>GetSpellsTaughtByFeats</c> excludes
		///         <c>Feat.AutomaticUnlockCategory</c> grants in its own <c>where</c>, so even a stored automatic feat
		///         could not contribute a spell here.</description></item>
		///     </list>
		///     The level check above is therefore not that guard restated - it covers the OTHER overlap, a tree feat
		///     teaching a spell the class's level ladder also hands out.
		/// </remarks>
		private async Task RevokeSpellsTaughtBy(INetworkClient client, Character character, IReadOnlyList<uint> featIds)
		{
			if (featIds.Count == 0)
				return;

			var taught = await _database.GetSpellsTaughtByFeats(featIds);
			if (taught.Length == 0)
				return;

			var byLevel = (await _catalog.GetClassSpellsUnlockedBetween(character.Class, 0, character.Level)).ToHashSet();
			foreach (var spellId in SpellsToRevoke(taught, byLevel))
			{
				if (!await _database.RemoveCharacterSpell(character.Id, spellId))
					continue; // the character did not hold it (a feat can teach a spell it never actually handed over)

				await _forgetSpell(client, spellId);
				_logger.LogInformation("Character {CharacterId} lost spell {SpellId} with the feat that taught it.", character.Id, spellId);
			}
		}

		/// <summary>
		///     Which of the spells the untrained feats taught actually come back: everything the character's LEVEL
		///     does not also grant. A pure function of the two sets, split from its caller so the rule is testable
		///     without a world, a client or a database.
		/// </summary>
		internal static IEnumerable<uint> SpellsToRevoke(IReadOnlyList<uint> taughtByUntrainedFeats, IReadOnlySet<uint> grantedByLevel)
		{
			return taughtByUntrainedFeats.Where(spellId => !grantedByLevel.Contains(spellId));
		}

		// What TrainWithinBudget returns when it has ALREADY told the player why - the perk charge names the pool and
		// the shortfall, which the caller's generic "not enough feat points" would only contradict.
		private const int RejectedAndExplained = -2;

		// Trains `requested` onto the character iff the resulting feat set is legal: within the feat-point budget AND
		// with every PREREQUISITE satisfied (FeatPrereq, the client's own feat-tree edges - #251). Returns the number
		// of feats added, or -1 when the train is rejected.
		private async Task<int> TrainWithinBudget(INetworkClient client, ushort level, IReadOnlyList<uint> requested)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var existing = await _database.GetCharacterFeats(characterId);
			var after = existing.Concat(requested.Where(id => !existing.Contains(id))).ToList();

			// ⭐ #150: split the request into PERKS and ordinary feats before either budget is asked about. They are
			// two currencies - a perk's price is a per-placement `FeatPerkCost` in an AA token pool, an ordinary
			// feat's is one point per rank against the level budget - and charging a perk to the feat-point budget
			// (which is what happened before) both overspends the wrong purse and leaves the tokens untouched.
			var perkPrices = await _database.GetPerkCosts(character.Class, after);
			if (!await ChargePerks(client, requested, perkPrices))
				return RejectedAndExplained;

			after = after.Where(id => !perkPrices.ContainsKey(id)).ToList();

			// #850: the AUTOMATIC feats cost nothing. A character accumulates them by levelling, not by spending, and
			// counting them was enough on its own to reject every train request at level 80 for three classes - their
			// automatic set alone comes to 153, 97 and 89 points against a budget of 79.
			var free = await _database.GetAutotrainedFeatWireIds();
			if (!FeatRules.WithinBudget(level, after, free))
			{
				_logger.LogWarning("Character {CharacterId} feat train rejected: {Spent} points > budget {Budget} (level {Level}).", characterId,
					FeatRules.TotalCost(existing.Concat(requested), free), FeatRules.PointBudget(level), level);
				return -1;
			}

			// Checked against the RESULTING set, not against `requested`: training a feat and its prerequisite in one
			// request is legal, and so is a prerequisite the character already holds. The perks come back in for this:
			// they are outside the POINT budget, not outside the prerequisite graph - an AA tree has tiers like any
			// other.
			var whole = after.Concat(perkPrices.Keys).ToList();
			var prereqs = await _database.GetFeatPrereqs(whole);
			var missing = FeatRules.MissingPrerequisites(whole, prereqs);
			if (missing.Count > 0)
			{
				_logger.LogWarning("Character {CharacterId} feat train rejected: {Count} unmet prerequisite(s), first {Feat} requires {Requires}.",
					characterId, missing.Count, missing[0].Feat, missing[0].Requires);
				return -1;
			}

			var added = await _database.AddCharacterFeats(characterId, requested);
			await GrantSpellsTaughtBy(client, requested);
			return added;
		}

		/// <summary>
		///     Charges the ALTERNATE-ADVANCEMENT perks in a train request to their token pools (#150). False - and
		///     nothing charged - when any one of them is unaffordable, so a mixed request is all-or-nothing.
		/// </summary>
		/// <remarks>
		///     ⚠️ Only NEW perks are charged. A train request that re-names a perk the character already holds is not
		///     a second purchase, and the client sends the whole resulting set often enough that charging per request
		///     would bill a player repeatedly for one perk.
		///     <para />
		///     ⚠️ The affordability of the WHOLE request is checked before anything is spent. Spending as it goes
		///     would leave a player who could afford two of three perks having paid for two and trained none, because
		///     the caller rejects the request outright.
		/// </remarks>
		private async Task<bool> ChargePerks(INetworkClient client, IReadOnlyList<uint> requested, IReadOnlyDictionary<uint, (TokenPool Pool, uint Cost)> prices)
		{
			var character = client.Session.Character;
			var held = await _database.GetCharacterFeats(character.Id);
			var buying = requested.Distinct().Where(id => prices.ContainsKey(id) && !held.Contains(id)).ToArray();
			if (buying.Length == 0)
				return true;

			var owed = new Dictionary<TokenPool, uint>();
			foreach (var id in buying)
			{
				var (pool, cost) = prices[id];
				owed[pool] = owed.GetValueOrDefault(pool) + cost;
			}

			foreach (var (pool, cost) in owed)
			{
				var unspent = await _alternateAdvancement.Unspent(character.Id, pool);
				if (unspent >= cost)
					continue;

				_logger.LogWarning("Character {CharacterId} perk train rejected: {Cost} {Pool} needed, {Unspent} held.", character.Id, cost, pool, unspent);
				await _chat.SendSystemMessage(character.Id, $"Not enough {pool} ({unspent} of {cost}).");

				return false;
			}

			foreach (var (pool, cost) in owed)
				await _alternateAdvancement.Spend(client, pool, cost);

			_logger.LogInformation("Character {CharacterId} bought {Count} perk(s) for {Cost}.", character.Id, buying.Length,
				string.Join(", ", owed.Select(o => $"{o.Value} {o.Key}")));

			return true;
		}

		// ⭐ A feat that TEACHES a spell hands it over when it is trained (#847). The catalog carries two grant
		// mechanisms and the level path only owns one of them: `Feat.AutomaticUnlockCategory` feats teach what a ding
		// gives, and the feat TREE teaches the rest - one named `Spell - <name>` feat per spell, bought with a point.
		// Both captured level-80s hold SOME of their class's tree spells and not others, which is what says the tree
		// half is a purchase rather than a handout; before this it was the only half nothing granted, because the
		// level path had (correctly) stopped offering it.
		//
		// Idempotent by construction: `AddCharacterSpell` reports whether the row was new, and only a new one is
		// announced, so re-training a feat the character already holds re-toasts nothing.
		private async Task GrantSpellsTaughtBy(INetworkClient client, IReadOnlyList<uint> featIds)
		{
			var characterId = client.Session.Character.Id;
			foreach (var spellId in await _database.GetSpellsTaughtByFeats(featIds))
			{
				if (!await _database.AddCharacterSpell(characterId, spellId))
					continue;

				await _learnSpell(client, spellId);
				_logger.LogInformation("Character {CharacterId} learned spell {SpellId} from a trained feat.", characterId, spellId);
			}
		}
	}
}
