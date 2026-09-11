using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Value factory for the four reward lists of a Quest_c journal record: the generator that turns a quest's database
	///     rows into the <see cref="QuestRewardItem" /> elements <see cref="QuestcRecordCodec" /> encodes. Not a codec -
	///     nothing here reads or writes bytes.
	/// </summary>
	/// <remarks>
	///     obj1 and obj2 are this quest's own reward, paid at its completion; obj3 and obj4 are the line's reward,
	///     previewed on every step but the last. That is settled from two independent sides: the client's journal renderer
	///     (FUN_01617260) shows obj1 as the rewards and obj2 as the choose-one when this quest is the last entry of its
	///     line's list, and obj3/obj4 otherwise; and the client's quest catalog (RDB 1000104) states each line's reward and
	///     optional_reward, which over the corpus equal obj3/obj4 on 1,553 of 1,553 records, while on last-step records
	///     obj1/obj2 equal them on 574 - the line reward folding into the last step's own set - and differ on 317.
	///     <c>--report quest-reward-lists</c> re-runs it.
	/// </remarks>
	public sealed record QuestRewardLists(List<QuestRewardItem> Obj1, List<QuestRewardItem> Obj2, List<QuestRewardItem> Obj3, List<QuestRewardItem> Obj4)
	{
		public static QuestRewardLists Empty => new([], [], [], []);

		/// <summary>
		///     The RDB type word every captured element carries on both template halves - the item-template catalog's RDBH
		///     variant, constant on all 7,347 of them.
		/// </summary>
		public const uint TemplateType = 1000620;

		/// <summary>
		///     The lists for one quest, addressed to the live player's <paramref name="instance" />.
		/// </summary>
		/// <remarks>
		///     <paramref name="itemOf" /> resolves a template to its Item row, which supplies the per-template TEA key and
		///     level the element carries. An element whose template has no key is left out rather than sent with a zero key:
		///     the client decrypts each entry from its own RDB with the key we ship, and a wrong one renders as "No name".
		///     <para>
		///         The line half is built for every quest with a catalog line reward, captured or not, since the catalog is
		///         the client's own data and matches the wire wherever the wire was seen. The own half is capture-bound
		///         because the catalog has no per-quest reward field, so a quest nobody watched carries an empty obj1/obj2 -
		///         except on the line's last step, where retail folds the line reward in and so do we.
		///     </para>
		/// </remarks>
		public static QuestRewardLists ForQuest(QuestDefinition def, QuestLine? line, uint instance, Func<uint, Item?> itemOf)
		{
			ArgumentNullException.ThrowIfNull(def);
			ArgumentNullException.ThrowIfNull(itemOf);

			var own = def.Rewards.OrderBy(r => r.Pos).ToList();
			var obj1 = Build(own.Where(r => !r.Optional).Select(r => (r.ItemTemplateId, r.Quantity)), def, instance, itemOf);
			var obj2 = Build(own.Where(r => r.Optional).Select(r => (r.ItemTemplateId, r.Quantity)), def, instance, itemOf);
			var obj3 = new List<QuestRewardItem>();
			var obj4 = new List<QuestRewardItem>();

			if (line != null && line.Rewards.Count > 0)
			{
				// The wire's own order where a capture stated it, the catalog's slot order otherwise.
				var lineRewards = line.Rewards.OrderBy(r => r.WirePos ?? r.Pos).ThenBy(r => r.Pos).ToList();
				var required = lineRewards.Where(r => !r.Optional).Select(r => (r.ItemTemplateId, r.Quantity ?? 1));
				var optional = lineRewards.Where(r => r.Optional).Select(r => (r.ItemTemplateId, r.Quantity ?? 1));
				var isLast = line.LastQuestId == def.QuestId;

				if (!isLast)
				{
					obj3 = Build(required, def, instance, itemOf);
					obj4 = Build(optional, def, instance, itemOf);
				}
				else if (own.Count == 0)
				{
					// The fold: on the line's last step the line reward is the quest's reward, and retail carries it in
					// obj1/obj2 with obj3/obj4 empty (574 captured records). A harvested own set wins over the fold -
					// those are the 317 where the last step pays something of its own.
					obj1 = Build(required, def, instance, itemOf);
					obj2 = Build(optional, def, instance, itemOf);
				}
			}

			return new QuestRewardLists(obj1, obj2, obj3, obj4);
		}

		private static List<QuestRewardItem> Build(IEnumerable<(uint Template, uint Quantity)> rewards, QuestDefinition def, uint instance, Func<uint, Item?> itemOf)
		{
			var list = new List<QuestRewardItem>();
			foreach (var (template, quantity) in rewards)
			{
				var item = itemOf(template);
				if (item?.Key is not { IsEmpty: false } key)
					continue;

				list.Add(new QuestRewardItem
				{
					Instance = instance,
					QuestId = def.QuestId,
					TemplateType = TemplateType,
					TemplateId = template,
					Key = key,
					// The template2 half mirrors template1 on every captured element - the ACG pair's common case.
					TemplateType2 = TemplateType,
					TemplateId2 = template,
					Key2 = key,
					Level = item.Level,
					Quantity = quantity
				});
			}

			return list;
		}
	}
}
