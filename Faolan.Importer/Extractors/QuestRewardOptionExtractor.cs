using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// One quest's pick-one-of-N reward offer, exactly as retail sent it.
	// One item of a quest's own reward set: the offer's options (`Optional`), or the journal record's obj1
	// (granted, `Optional = false`) / obj2 (the choose-one). `Quantity` is the record's word; an offer states none.
	public sealed record QuestRewardOffer(uint QuestId, uint Pos, uint ItemTemplateId, ulong Level, bool Optional = true, uint Quantity = 1);

	// The line half of a journal record - obj3/obj4 - which is the client catalog's line reward with one word the
	// catalog lacks: the quantity. Harvested per (line, template) so QuestLineRewards.Quantity can be filled.
	public sealed record QuestLineRewardQuantity(uint QuestLineId, uint ItemTemplateId, bool Optional, uint Quantity, uint WirePos, ulong Level);

	// Harvests the QuestRewardNotifyIIR OFFER - which quest let the player choose between which items.
	//
	// This is harvested rather than read out of the client because the client does not have it. The quest catalog
	// (RDB 1000104) carries rewards on the line (`MainQuest.reward` / `optional_reward`), and the per-quest `Quest`
	// message has no reward field at all - seven fields, all accounted for. For the one offer we have captured that
	// is not a technicality: quest 10928 offered Acolyte's Dagger / Longsword / Staff, its line 386 holds a single
	// non-optional "Exponent's Gloves", and those three template ids appear in no QuestLineRewards row on any line.
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

				// Pos is the index the client answers with, so it is taken from the list order rather than invented.
				found.AddRange(notify.Options.Select((o, i) => new QuestRewardOffer(notify.QuestId, (uint)i, o.ItemTemplateId, o.Level)));
			}

			return found;
		}

		// The second source, and the fuller one: every captured `Quest_c` journal record - the login snapshot's
		// and the live QuestFullUpdate's - carries the quest's own reward set as its obj1 (granted) and obj2
		// (choose-one) lists, plus the line's reward as obj3/obj4. The obj2 list is what the offer sends (quest 10928's
		// three Acolyte weapons, both routes), so a record harvests the whole set where an offer harvests a third of
		// it. Positions: the optional rows first, densely, because on an optional row `Pos` is the index the client
		// answers with; the granted rows follow.
		public static (List<QuestRewardOffer> Own, List<QuestLineRewardQuantity> Line) ExtractFromRecords(IReadOnlyList<CapturedPacket> packets)
		{
			ArgumentNullException.ThrowIfNull(packets);
			var own = new List<QuestRewardOffer>();
			var line = new List<QuestLineRewardQuantity>();

			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true })
					continue;

				if (p.SubOpcode == Faolan.Core.Network.Codecs.FullCharSnapshot.FullPlayerCharacterCodec.Opcode)
				{
					Faolan.Core.Network.Codecs.FullCharSnapshot.FullPlayerCharacterIIR parts;
					try
					{
						parts = Faolan.Core.Network.Codecs.FullCharSnapshot.FullPlayerCharacterCodec.Parse(p.Record);
					}
					catch
					{
						continue; // a snapshot shape the codec cannot split - --report coverage counts it
					}

					foreach (var r in parts.SubClass.Quests?.Records ?? [])
						Record(r, own, line);
				}
				else if (p.SubOpcode == QuestFullUpdateCodec.Opcode && p.TryParse(QuestFullUpdateCodec.Parse, out var full))
				{
					Record(full.Record, own, line);
				}
			}

			return (own, line);
		}

		private static void Record(QuestcRecord r, List<QuestRewardOffer> own, List<QuestLineRewardQuantity> line)
		{
			var pos = 0u;
			foreach (var e in r.Obj2)
				own.Add(new QuestRewardOffer(r.QuestId, pos++, e.TemplateId, e.Level, true, e.Quantity));
			foreach (var e in r.Obj1)
				own.Add(new QuestRewardOffer(r.QuestId, pos++, e.TemplateId, e.Level, false, e.Quantity));
			var linePos = 0u;
			foreach (var e in r.Obj3)
				line.Add(new QuestLineRewardQuantity(r.QuestLineId, e.TemplateId, false, e.Quantity, linePos++, e.Level));
			foreach (var e in r.Obj4)
				line.Add(new QuestLineRewardQuantity(r.QuestLineId, e.TemplateId, true, e.Quantity, linePos++, e.Level));
		}

		// One row per (quest, position): the list is a property of the quest, not of the run, so a quest seen offering
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
