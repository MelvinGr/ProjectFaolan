using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// One quest's pick-one-of-N reward offer, exactly as retail sent it (#815).
	public sealed record QuestRewardOffer(uint QuestId, uint Pos, uint ItemTemplateId, ulong Level);

	// Harvests the QuestRewardNotifyIIR OFFER - which quest let the player choose between which items.
	//
	// ⚠️ This is harvested rather than read out of the client because the client does not have it. The quest catalog
	// (RDB 1000104) carries rewards on the LINE (`MainQuest.reward` / `optional_reward`), and the per-quest `Quest`
	// message has no reward field at all - seven fields, all accounted for. For the one offer we have captured that
	// is not a technicality: quest 10928 offered Acolyte's Dagger / Longsword / Staff, its line 386 holds a single
	// non-optional "Exponent's Gloves", and those three template ids appear in NO QuestLineRewards row on ANY line.
	// So where a capture and the catalog disagree the capture wins, which is this repo's standing rule.
	//
	// Only the OFFER form is harvested. The same IIR carries the CONFIRM with an empty option list, which states no
	// options and would otherwise wipe a quest's harvested set on any capture that saw a confirm without its offer.
	public static class QuestRewardOptionExtractor
	{
		public static List<QuestRewardOffer> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			ArgumentNullException.ThrowIfNull(packets);

			var found = new List<QuestRewardOffer>();
			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: QuestRewardNotifyCodec.Opcode })
					continue;

				if (!p.TryParse(QuestRewardNotifyCodec.Parse, out var notify) || notify.Options.Count == 0)
					continue;

				// Pos IS the index the client answers with, so it is taken from the list ORDER rather than invented.
				found.AddRange(notify.Options.Select((o, i) => new QuestRewardOffer(notify.QuestId, (uint)i, o.ItemTemplateId, o.Level)));
			}

			return found;
		}

		// One row per (quest, position): the list is a property of the QUEST, not of the run, so a quest seen offering
		// in two captures is the same offer twice. Unioning the positions means a capture that caught a shorter list
		// cannot shrink a fuller one; where two captures disagree on the item at the same position, the first is kept
		// (no capture in the corpus does, so this is a tie-break rather than a policy).
		public static List<QuestRewardOffer> Merge(IEnumerable<QuestRewardOffer> all)
		{
			ArgumentNullException.ThrowIfNull(all);

			return all.GroupBy(o => o.QuestId)
				.SelectMany(q => q.GroupBy(o => o.Pos)
					.OrderBy(g => g.Key)
					.Select(g => g.First()))
				.ToList();
		}
	}
}
