using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.GameServer.Economy;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	// The pick-one-of-N quest reward exchange (#815), server side.
	//
	//   completion -> QuestRewardNotifyIIR (the offer)
	//   c2s        -> QuestRewardSelectionIIR (the index)
	//   grant      -> the item + QuestRewardNotifyIIR with no options (the confirm)
	//
	// Decoded from xxx_mage-necromancer_login_play3 on quest 10928, where the offer rode the completion burst and the
	// pick was answered with a DispatchAddItem for the template at that index.
	//
	// ⚠️ The offer is sent for the quest that just completed and NOT re-sent. A player who ignores the window keeps a
	// claimable reward: `TryGrant` only requires the quest to be completed and unrewarded, exactly as the wire allows,
	// because nothing binds the client's answer to the moment of the offer.
	public sealed class QuestRewardService
	{
		private readonly IDatabaseRepository _database;
		private readonly IEconomyOutbound _economy;
		private readonly ILogger _logger;
		private readonly IQuestOutbound _outbound;

		public QuestRewardService(IDatabaseRepository database, IQuestOutbound outbound, IEconomyOutbound economy, ILogger logger)
		{
			_database = database;
			_outbound = outbound;
			_economy = economy;
			_logger = logger;
		}

		/// <summary>
		///     A quest just completed: if it offers a choice of rewards, present it. No-op for the ordinary quest that
		///     offers none, which is almost all of them.
		/// </summary>
		public async Task OfferOnCompletion(INetworkClient client, uint questId)
		{
			var character = client?.Session.Character;
			if (character == null)
				return;

			var options = await _database.GetQuestRewardOptions(questId);
			if (options.Length == 0)
				return;

			var entries = await BuildEntries(options);
			if (entries.Count == 0)
			{
				// Every option's item failed to resolve - say so rather than sending an empty list, which the client
				// would render as a reward window with nothing in it.
				_logger.LogWarning("Quest {Quest}: {Count} reward option(s) but none resolved to an item - no offer sent.", questId, options.Length);
				return;
			}

			await _outbound.SendQuestRewardOffer(client, questId, entries);
			_logger.LogInformation("Quest {Quest} offers {Count} reward(s) to {Char}: {Items}.", questId, entries.Count, character.Name,
				string.Join(", ", entries.Select(e => e.ItemTemplateId)));
		}

		/// <summary>
		///     The player picked reward <paramref name="index" /> for <paramref name="questId" />. Grants the item and
		///     confirms. Returns false when the pick is refused - an unknown quest, one the character has not completed,
		///     one whose reward is already spent, or an index outside the offer.
		/// </summary>
		public async Task<bool> TryGrant(INetworkClient client, uint questId, uint index)
		{
			var character = client?.Session.Character;
			if (character == null)
				return false;

			// The quest must be one this character actually completed. Without this any client could name any quest id.
			var quest = (await _database.GetCharacterQuests(character.Id)).FirstOrDefault(q => q.QuestId == questId);
			if (quest is not { State: QuestState.Completed })
			{
				_logger.LogInformation("Quest reward refused for {Char}: quest {Quest} is {State}.", character.Name, questId, quest?.State.ToString() ?? "not held");
				return false;
			}

			// Idempotence, not paranoia: the selection is an ordinary c2s packet and arrives whenever the client sends
			// it, so the ANSWER is what closes the reward, not the completion.
			if (quest.RewardChosenPos != null)
			{
				_logger.LogInformation("Quest reward refused for {Char}: quest {Quest} was already rewarded (option {Pos}).", character.Name, questId, quest.RewardChosenPos);
				return false;
			}

			var options = await _database.GetQuestRewardOptions(questId);
			var picked = options.FirstOrDefault(o => o.Pos == index);
			if (picked == null)
			{
				_logger.LogWarning("Quest reward refused for {Char}: index {Index} is outside quest {Quest}'s {Count} option(s).", character.Name, index, questId, options.Length);
				return false;
			}

			// Recorded BEFORE the grant: if the item hand-off throws, the reward stays spent rather than becoming
			// re-claimable, which is the safe direction for a duplicate.
			await _database.SetCharacterQuestReward(character.Id, questId, index);

			if (await _economy.GrantItemToBackpack(client, picked.ItemTemplateId) == null)
			{
				_logger.LogWarning("Quest {Quest}: reward item {Item} could not be granted to {Char} (no free slot?).", questId, picked.ItemTemplateId, character.Name);
				return false;
			}

			await _outbound.SendQuestRewardConfirm(client, questId, index);
			_logger.LogInformation("Quest {Quest}: {Char} took reward {Index} (item {Item}).", questId, character.Name, index, picked.ItemTemplateId);
			return true;
		}

		// The wire entries for an offer. The client decrypts each option's template from its OWN RDB using the key we
		// ship, so the key must be the item's real ProtoDecryptionKey or the row renders as "No name" - the same rule
		// the claim list documents. An item we hold no key for is dropped rather than sent nameless.
		private async Task<List<QuestRewardEntry>> BuildEntries(IReadOnlyList<QuestRewardOption> options)
		{
			var entries = new List<QuestRewardEntry>(options.Count);
			foreach (var option in options)
			{
				var item = await _database.GetItem(option.ItemTemplateId);
				if (item == null)
				{
					_logger.LogWarning("Quest reward option {Item} is not in Items - dropped from the offer.", option.ItemTemplateId);
					continue;
				}

				var key = ItemKeyBytes(item);
				if (key == null)
				{
					_logger.LogWarning("Quest reward option {Item} has no ProtoDecryptionKey - dropped rather than offered as \"No name\".", option.ItemTemplateId);
					continue;
				}

				// Pos is renumbered densely over what actually resolved, because the index the client answers with is
				// an index into the list we SEND. A dropped option must not leave a hole that shifts every later pick.
				entries.Add(new QuestRewardEntry(item.Id, option.Level == 0 ? item.Level : option.Level, new byte[QuestRewardEntry.CodeLength], key,
					new byte[QuestRewardEntry.BatchGuidLength]));
			}

			return entries;
		}

		// The item's ProtoDecryptionKey as the on-wire 16-byte token: Key1..4 big-endian, the same layout the claim
		// list ships. Null when the item carries no key - unlike a claim row (which stays valid and merely lists as
		// "No name"), a reward the player cannot read is not worth OFFERING, so the caller drops it instead.
		private static byte[]? ItemKeyBytes(Item item)
		{
			if (item.Key is not { } k || k.IsEmpty)
				return null;

			var key = new byte[QuestRewardEntry.KeyLength];
			key.WriteU32Be(0, k.Key1);
			key.WriteU32Be(4, k.Key2);
			key.WriteU32Be(8, k.Key3);
			key.WriteU32Be(12, k.Key4);
			return key;
		}
	}
}
