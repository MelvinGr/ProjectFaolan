using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report quest-rewards` (#464): the four reward lists inside every captured `Quest_c` journal record - what
	// `QuestcRecord` carried for months as *"16 raw u32s, unvalidated"*.
	//
	// They are not raw. Each 16-word element is one quest REWARD ITEM, and the shape is the ACG item the snapshot's
	// own inventory entries already use:
	//
	//     [u32 instance][u32 questId]
	//     [u32 1000620][u32 templateId][u32 key1..key4]     <- ACG template1 + its per-template TEA key
	//     [u32 1000620][u32 templateId][u32 key1..key4]     <- ACG template2 + its key
	//     [u32 level][u32 quantity]
	//
	// ⭐ **The proof is DECRYPTION, not shape.** This report takes each element's wire key, opens the RDB entry the
	// element names, and decrypts it - the same test that pinned `keyN <-> templateN` for `ProtoAcgItem` (#752). A
	// plausible carve cannot survive that: a wrong key yields a garbage header, not a valid one.
	//
	// ⭐ And it is a CONTENT source. An item enters our catalog when a capture yields its `Data1..4`, and these
	// elements carry 100 template keys the inventory path never sees - two thirds of them for templates `Items` does
	// not hold. Open work tracked in Gitea.
	//
	// Exit code is 0 always - a measurement.
	public static partial class ReportCli
	{
		// The client's item-template catalog types. An element names 1000620 (the RDBH variant); 1000020 is tried as
		// a fallback because the two share the id space and a template can live in either (CharacterModelService
		// does the same).
		private static readonly uint[] RewardTemplateTypes = [1000620, 1000020];

		private static async Task<int> QuestRewards(IReadOnlyList<NamedCapture> captures, string? rdbDir)
		{
			var elements = 0;
			var snapshots = 0;
			var records = 0;
			var perList = new int[4];
			var questIdEcho = 0;
			var mirrored = 0;
			var typeWordIs1000620 = 0;
			var keys = new Dictionary<uint, uint[]>();
			var levels = new Dictionary<uint, int>();
			var quantities = new Dictionary<uint, int>();
			var instances = new HashSet<uint>();
			var levelOfTemplate = new Dictionary<uint, HashSet<uint>>();
			var quantityOfQuestTemplate = new Dictionary<(uint Quest, uint Template), HashSet<uint>>();

			foreach (var capture in captures)
			{
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: FullPlayerCharacterCodec.Opcode })
						continue;

					FullPlayerCharacterIIR parts;
					try
					{
						parts = FullPlayerCharacterCodec.Parse(p.Record);
					}
					catch
					{
						continue; // a snapshot shape the codec cannot split - counted by --report coverage, not here
					}

					snapshots++;
					foreach (var record in parts.SubClass.Quests?.Records ?? [])
					{
						records++;
						var lists = new[] { record.Obj1, record.Obj2, record.Obj3, record.Obj4 };
						for (var i = 0; i < 4; i++)
						foreach (var e in lists[i])
						{
							elements++;
							perList[i]++;
							instances.Add(e.Instance);
							if (e.QuestId == record.QuestId)
								questIdEcho++;

							if (e.TemplateType == RewardTemplateTypes[0] && e.TemplateType2 == RewardTemplateTypes[0])
								typeWordIs1000620++;

							if (e.TemplateType == e.TemplateType2 && e.TemplateId == e.TemplateId2 && e.Key.Equals(e.Key2))
								mirrored++;

							keys[e.TemplateId] = [e.Key.Key1, e.Key.Key2, e.Key.Key3, e.Key.Key4];
							levels[e.Level] = levels.GetValueOrDefault(e.Level) + 1;
							quantities[e.Quantity] = quantities.GetValueOrDefault(e.Quantity) + 1;

							(levelOfTemplate.TryGetValue(e.TemplateId, out var ls) ? ls : levelOfTemplate[e.TemplateId] = []).Add(e.Level);
							var qk = (record.QuestId, e.TemplateId);
							(quantityOfQuestTemplate.TryGetValue(qk, out var qs) ? qs : quantityOfQuestTemplate[qk] = []).Add(e.Quantity);
						}
					}
				}
			}

			Console.WriteLine($"{snapshots} snapshots, {records} journal records, {elements} reward elements "
			                  + $"(per list: {string.Join("/", perList)}), {keys.Count} distinct templates, {instances.Count} client instances\n");
			Console.WriteLine($"  word 1 == the record's own questId : {questIdEcho}/{elements}");
			Console.WriteLine($"  words 2 and 8 == 1000620           : {typeWordIs1000620}/{elements}");
			Console.WriteLine($"  template2 half mirrors template1   : {mirrored}/{elements}");
			Console.WriteLine($"  word 14 : {string.Join(" ", levels.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}x{k.Value}"))}");
			Console.WriteLine($"  word 15 : {string.Join(" ", quantities.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}x{k.Value}"))}");

			// What the two trailing words are ATTACHED to, which is what decides whether they can be named. A word
			// constant per template is a property of the item; one constant per (quest, template) is a property of
			// the reward. Printed rather than asserted - the corpus is thin on both.
			Console.WriteLine($"  word 14 constant per template         : {levelOfTemplate.Count(k => k.Value.Count == 1)}/{levelOfTemplate.Count}");
			Console.WriteLine($"  word 15 constant per (quest,template) : {quantityOfQuestTemplate.Count(k => k.Value.Count == 1)}/{quantityOfQuestTemplate.Count}");

			await ProveKeys(keys, rdbDir);
			return 0;
		}

		// The decisive half: does the element's key actually open the template it names?
		private static async Task ProveKeys(Dictionary<uint, uint[]> keys, string? rdbDir)
		{
			await using var db = OpenDb();
			var known = await db.Items.AsNoTracking().Where(i => keys.Keys.Contains(i.Id)).Select(i => i.Id).ToListAsync();
			Console.WriteLine($"\n  templates already in `Items`: {known.Count} of {keys.Count} - {keys.Count - known.Count} are NOT, "
			                  + "and each of those arrives here with the key that would admit it");

			if (string.IsNullOrEmpty(rdbDir))
			{
				Console.WriteLine("  (no --Content:RdbDir: skipping the decryption proof)");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			int opened = 0, absent = 0, failed = 0;
			var wrongKeyFailures = 0;
			var wrongKey = new uint[] { 1, 2, 3, 4 };

			foreach (var (id, key) in keys.OrderBy(k => k.Key))
			{
				RdbReader.Entry entry = default;
				var found = RewardTemplateTypes.Any(t => rdb.TryGet(t, id, out entry) && entry.File != 0xff && entry.Size > 0);
				if (!found)
				{
					absent++;
					continue;
				}

				if (Decodes(rdb, entry, key, id))
					opened++;
				else
					failed++;

				// The CONTROL. Without it "the key works" is just "the record parses", which a decrypt with any key
				// might also manage - and a 1-in-N chance of a plausible header is exactly how a wrong carve survives.
				if (!Decodes(rdb, entry, wrongKey, id))
					wrongKeyFailures++;
			}

			Console.WriteLine($"  decrypted with the WIRE key : {opened} opened, {failed} failed, {absent} not in the local packs");
			Console.WriteLine($"  control, a bogus key        : {wrongKeyFailures} of {opened + failed} failed to open (a wrong key must NOT work)");
		}

		// A template opened correctly iff its decrypted body declares one of the catalog types and its own id.
		private static bool Decodes(RdbReader rdb, RdbReader.Entry entry, ReadOnlySpan<uint> key, uint id)
		{
			try
			{
				var body = rdb.ReadDecrypted(entry, key);
				return body.Length >= 12 && RewardTemplateTypes.Contains(BitConverter.ToUInt32(body, 0)) && BitConverter.ToUInt32(body, 4) == id;
			}
			catch
			{
				return false;
			}
		}
	}
}
