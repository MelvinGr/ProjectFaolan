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
	// Feat training and reset (untrain-all). Feats ride the player spawn's pre-IIR list (see PlayerSpawnScaffoldRows
	// and docs/subsystems/feats.md); this is the live protocol that mutates that set:
	//   - Train: c2s FeatIIR (0x28DA68A0) state 0x04 [feat ids] -> the server adds them to CharacterFeat -> s2c FeatIIR
	//     state 0x05 (echoes the list). The client renders the trained feats off the ack; they persist for next spawn.
	//   - Reset (untrain all): retail shows a confirm dialog (DispatchFeatUntrainMessageBox -> FeatUntrainAnswer) and
	//     then reloads the world (the "loading screen" the capture is named for) so the fresh spawn carries the cleared
	//     list. Here CharacterFeat is cleared and the world reloads via the change-map re-entry, a teleport to the
	//     current spot.
	// Decoded from the hakotep untrain/train captures. The world reload rides a teleport delegate seam.
	public sealed class FeatService
	{
		private readonly Func<uint, uint, Vector3?, Task> _teleport;
		private readonly ICharacterProgressionRepository _database;

		// The catalog half, for one question: what the character's level grants, which is the guard on the untrain's
		// spell revoke. A second narrow interface rather than widening `_database` to `IDatabaseRepository` - the
		// question belongs to the catalog, and the same instance answers both.
		private readonly ICatalogRepository _catalog;
		private readonly IChatServerDispatcher _chat;
		private readonly ILogger _logger;

		// The two halves of the live spellbook - teach (DispatchLearnSpell 0x200A) and revoke (DispatchForgetSpell
		// 0x200B) - the same pair `ILevelUpOutbound` exposes for the ding and `/magic forget` uses. They arrive as
		// delegates rather than a port because this service needs exactly these two, and they are one cluster: a feat
		// that teaches a spell has to be able to take it back.
		private readonly Func<INetworkClient, uint, Task> _learnSpell;
		private readonly Func<INetworkClient, uint, Task> _forgetSpell;

		// An alternate-advancement perk arrives here as an ordinary feat train carrying a perk id - the client's
		// `TrainAAFeat` goes through the same `FeatHolder` train virtual a class feat does, which is why AA needs no
		// message of its own. What differs is the price: a perk is charged to a token pool, never to the class
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
		// trigger is the client's Feats window with its point economy, which the server cannot drive). train and reset
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
				// The codec is strict, so a body it cannot account for throws rather than decoding into something
				// plausible. Caught here because a malformed c2s frame is the peer's problem rather than a reason to
				// tear the session down; it is logged with the offset the reader stopped at.
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
				// Over the feat-point budget (a safety net; the client normally pre-validates). Nothing is added or
				// acked.
				await _chat.SendSystemMessage(character.Id, "Not enough feat points to train that.");
				return;
			}

			// Ack: echo the requested id list back (state 0x05), byte-for-byte as retail does. The client renders the
			// trained feats off this; the next spawn regenerates them from the persisted CharacterFeat rows.
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(FeatCodec.Build(handle, FeatCodec.StateAck, featIds))));

			_logger.LogInformation("Character {CharacterId} trained {Added} feat(s) [{Ids}].", character.Id, added, string.Join(",", featIds));
		}

		// c2s FeatUntrainAnswer (0x203E): the confirm-dialog reply. All feats are reset only when the player confirmed
		// and the echoed token matches the outstanding request, which guards against a stray or replayed answer.
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

		// c2s UntrainFeat (0x2041): the client clicked "untrain all feats". Retail answers with a confirm messagebox
		// (DispatchFeatUntrainMessageBox 0x2064) and only resets once the client confirms, so the dialog goes out here
		// and its token is remembered; the reset happens in HandleFeatUntrainAnswer. No feats means nothing to confirm.
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

		// Clear all of the character's trained feats and reload the world so the fresh spawn carries the cleared list
		// (the "loading screen" the retail untrain triggers). Shared by the confirm-dialog answer and the .feat GM
		// reset.
		private async Task ResetFeats(INetworkClient client, Character character)
		{
			// Read the set before clearing it - it is what says which spells to take back.
			var held = await _database.GetCharacterFeats(character.Id);
			var cleared = await _database.ClearCharacterFeats(character.Id);
			_logger.LogInformation("Character {CharacterId} reset {Cleared} feat(s).", character.Id, cleared);
			if (cleared == 0)
				return;

			await RevokeSpellsTaughtBy(client, character, held);
			await _teleport(character.Id, character.MapId, character.Position);
		}

		/// <summary>
		///     Takes back the spells the untrained feats had taught - the mirror of
		///     <see cref="GrantSpellsTaughtBy" />. Without it an untrain is a one-way door: world entry re-grants the
		///     whole <c>CharacterSpells</c> set, so a spell a feat handed over would survive the untrain for ever.
		/// </summary>
		/// <remarks>
		///     A spell the character's level also grants is kept: retail's untrain-all refunds spent points, it does
		///     not un-grant what levelling handed over, and the two paths overlap because the tree and the level ladder
		///     can name the same spell. <c>GetClassSpellsUnlockedBetween(class, 0, level)</c> is what the ding path
		///     itself teaches from, so asking it asks the same question the next ding would answer rather than a second
		///     rule that could drift from it.
		///     <para />
		///     Revoking off the cleared feat set cannot strip a level-earned spell: an auto-trained feat is never
		///     written to <c>CharacterFeat</c> (0 of the 56 rows in the shipped database are autotrained), and
		///     <c>GetSpellsTaughtByFeats</c> excludes <c>Feat.AutomaticUnlockCategory</c> grants in its own
		///     <c>where</c>, so even a stored automatic feat could not contribute a spell here.
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
		///     Which of the spells the untrained feats taught actually come back: everything the character's level
		///     does not also grant. A pure function of the two sets, split from its caller so the rule is testable
		///     without a world, a client or a database.
		/// </summary>
		internal static IEnumerable<uint> SpellsToRevoke(IReadOnlyList<uint> taughtByUntrainedFeats, IReadOnlySet<uint> grantedByLevel)
		{
			return taughtByUntrainedFeats.Where(spellId => !grantedByLevel.Contains(spellId));
		}

		// What TrainWithinBudget returns when it has already told the player why - the perk charge names the pool and
		// the shortfall, which the caller's generic "not enough feat points" would only contradict.
		private const int RejectedAndExplained = -2;

		// Trains `requested` onto the character only if the resulting feat set is legal: within the feat-point budget
		// and with every prerequisite satisfied (FeatPrereq, the client's own feat-tree edges). Returns the number of
		// feats added, or -1 when the train is rejected.
		private async Task<int> TrainWithinBudget(INetworkClient client, ushort level, IReadOnlyList<uint> requested)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var existing = await _database.GetCharacterFeats(characterId);
			var after = existing.Concat(requested.Where(id => !existing.Contains(id))).ToList();

			// Split the request into perks and ordinary feats before either budget is asked about. They are two
			// currencies - a perk's price is a per-placement `FeatPerkCost` in an AA token pool, an ordinary feat's is
			// one point per rank against the level budget - so charging a perk to the feat-point budget would both
			// overspend the wrong purse and leave the tokens untouched.
			var perkPrices = await _database.GetPerkCosts(character.Class, after);
			if (!await ChargePerks(client, requested, perkPrices))
				return RejectedAndExplained;

			after = after.Where(id => !perkPrices.ContainsKey(id)).ToList();

			// The automatic feats cost nothing: a character accumulates them by levelling rather than by spending.
			// Counting them is enough on its own to reject every train request at level 80 for three classes - their
			// automatic set alone comes to 153, 97 and 89 points against a budget of 79.
			var free = await _database.GetAutotrainedFeatWireIds();
			if (!FeatRules.WithinBudget(level, after, free))
			{
				_logger.LogWarning("Character {CharacterId} feat train rejected: {Spent} points > budget {Budget} (level {Level}).", characterId,
					FeatRules.TotalCost(existing.Concat(requested), free), FeatRules.PointBudget(level), level);
				return -1;
			}

			// Checked against the resulting set rather than against `requested`: training a feat and its prerequisite
			// in one request is legal, and so is a prerequisite the character already holds. The perks come back in
			// for this - they are outside the point budget, not outside the prerequisite graph, and an AA tree has
			// tiers like any other.
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
		///     Charges the alternate-advancement perks in a train request to their token pools. False - and nothing
		///     charged - when any one of them is unaffordable, so a mixed request is all-or-nothing.
		/// </summary>
		/// <remarks>
		///     Only new perks are charged. A train request that re-names a perk the character already holds is not a
		///     second purchase, and the client sends the whole resulting set often enough that charging per request
		///     would bill a player repeatedly for one perk.
		///     <para />
		///     The affordability of the whole request is checked before anything is spent: spending as it goes would
		///     leave a player who could afford two of three perks having paid for two and trained none, because the
		///     caller rejects the request outright.
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

		// A feat that teaches a spell hands it over when it is trained. The catalog carries two grant mechanisms and
		// the level path owns only one of them: `Feat.AutomaticUnlockCategory` feats teach what a ding gives, and the
		// feat tree teaches the rest - one named `Spell - <name>` feat per spell, bought with a point. Both captured
		// level-80s hold some of their class's tree spells and not others, which is what makes the tree half a purchase
		// rather than a handout.
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
