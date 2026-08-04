using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report combo-swings`: every melee-combo swing in the corpus, resolved against the `Combos` catalog (#139).
	//
	// This is the scan that settles what a combo swing says. A `Combat:Melee` (BehaviorId 0x0F) BehaviorControlCenter
	// record carries a `CommandMessage` in its args, and three of its fields are the combo:
	//
	//     field 7  combo_id           the `Combos` catalog id, verbatim
	//     field 8  combo_attack_num   the 0-based step, ABSENT on the opener (protobuf omits a zero)
	//     field 9  combo_attack_hits  the hit count (never set in the corpus)
	//
	// ⚠️ It exists because those fields were once read as raw bytes - "[08][animByte][2B marker][u32 counter][u32
	// cid = 0x300038XX]" - which put the combo id in the middle of a made-up field and left the conclusion that the
	// combo could not be recovered from packets at all. The "0x300038XX" was `[30 00]` (execution_status = 0) then
	// `[38 <combo_id varint>]`. Re-derive the numbers here rather than trusting that history.
	//
	// `--report-arg <file>` additionally writes the matched packets, whole and in capture order, so they can be
	// installed as a test fixture with `--report fixture-set`.
	public static partial class ReportCli
	{
		private static async Task ComboSwings(IReadOnlyList<NamedCapture> captures, string? fixtureOut)
		{
			await using var db = OpenDb();
			var catalog = await db.Combos.ToDictionaryAsync(c => c.Id, c => c);

			var swings = new List<Swing>();
			var packets = new List<byte[]>();

			foreach (var c in captures)
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;

					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
						continue; // the dump cut the record short - not a grammar failure

					if (bcc.BehaviorId is not ((uint)BehaviorId.CombatMelee or (uint)BehaviorId.CombatRanged or (uint)BehaviorId.CombatUnarmed))
						continue;

					var args = bcc.Args.FromProtobuf<CommandMessage>();
					if (!args.ShouldSerializeComboId())
						continue;

					swings.Add(new Swing(c.Name, p.IsDownload, bcc.Actor, bcc.Seq, bcc.BehaviorId, args, bcc.Tail.Length,
						catalog.GetValueOrDefault(args.ComboId)));
					packets.Add(p.Bytes);
				}

			var unknown = swings.Where(s => s.Combo is null).ToList();
			Console.WriteLine($"{swings.Count} combo swings across {swings.Select(s => s.Capture).Distinct().Count()} of {captures.Count} captures, "
			                  + $"{swings.Select(s => s.Args.ComboId).Distinct().Count()} distinct combo ids, {unknown.Count} not in the catalog");
			Console.WriteLine();

			Console.WriteLine("phases (direction / seq / behaviorId):");
			foreach (var g in swings.GroupBy(s => (s.IsDownload, s.Seq, s.BehaviorId)).OrderBy(g => g.Key.Seq))
				Console.WriteLine($"  {(g.Key.IsDownload ? "s2c" : "c2s")} seq=0x{g.Key.Seq:X4} id=0x{g.Key.BehaviorId:X2} {(BehaviorId)g.Key.BehaviorId,-18} x{g.Count()}");

			// The two populations, and the split is the point: the 69 `action`-marker rows are the NPC combos, and
			// they never appear as a REQUEST - the AI drives them, so there is no keyboard behind them.
			Console.WriteLine();
			Console.WriteLine("population:");
			foreach (var g in swings.Where(s => s.Combo is not null).GroupBy(s => IsNpcCombo(s.Combo!)))
			{
				var name = g.Key ? "NPC combos (Actions=`action`)" : "player combos (key sequences)";
				Console.WriteLine($"  {name,-32} {g.Count(),5} records, c2s {g.Count(s => !s.IsDownload)}, distinct actors {g.Select(s => s.Actor).Distinct().Count()}");
			}

			// `bcc_action_id` is the swing ANIMATION, and per class+weapon it maps one-to-one onto the step's own
			// direction - which is what the old raw-byte read was reaching for with "animByte". Grouping it by the
			// catalog step it lands on shows that directly.
			Console.WriteLine();
			Console.WriteLine("bcc_action_id by the catalog direction it lands on (name prefix = class/weapon family):");
			foreach (var g in swings.Where(s => s.Direction is not null).GroupBy(s => s.Combo!.Name.Split('_')[0]).OrderBy(g => g.Key))
			{
				var cells = g.GroupBy(s => (s.Direction, s.Args.BccActionId)).OrderBy(x => x.Key.Direction).ThenBy(x => x.Key.BccActionId);
				Console.WriteLine($"  {g.Key,-12} {string.Join("  ", cells.Select(x => $"{x.Key.Direction}={x.Key.BccActionId}x{x.Count()}"))}");
			}

			Console.WriteLine();
			Console.WriteLine("per combo (steps from Combo.Actions; `num` is combo_attack_num):");
			foreach (var g in swings.Where(s => s.Combo is not null).GroupBy(s => s.Combo!).OrderBy(g => g.Key.Name))
			{
				var steps = ComboActionParser.TryParse(g.Key.Actions, out var seq) ? seq.Count : 0;
				var nums = string.Join(",", g.Select(s => s.Args.ComboAttackNum).Distinct().Order());
				Console.WriteLine($"  {g.Key.Id,8}  {g.Key.Name,-56} [{g.Key.Actions,-16}] steps={steps} num={{{nums}}} x{g.Count()}");
			}

			if (unknown.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("combo ids NOT in the catalog (each one is either an importer gap or a misread body):");
				foreach (var g in unknown.GroupBy(s => s.Args.ComboId))
					Console.WriteLine($"  {g.Key} x{g.Count()}  first seen in {g.First().Capture}");
			}

			if (string.IsNullOrWhiteSpace(fixtureOut))
				return;

			await using var file = File.Create(fixtureOut);
			foreach (var bytes in packets)
				await file.WriteAsync(bytes);

			Console.WriteLine();
			Console.WriteLine($"wrote {packets.Count} packets ({packets.Sum(b => b.Length)} bytes) to {fixtureOut}");
		}

		// The NPC-combo set: `Actions` is the bare `action` marker. All 69 such rows are named `combo_npc_*`.
		private static bool IsNpcCombo(Combo combo)
		{
			return ComboActionParser.TryParseMarker(combo.Actions, out var trigger) && trigger == ComboTrigger.NpcCombo;
		}

		private sealed record Swing(string Capture, bool IsDownload, uint Actor, ushort Seq, uint BehaviorId, CommandMessage Args, int TailLength, Combo? Combo)
		{
			// The catalog direction this swing's step is, when the combo has a key sequence and the step is inside it.
			public ComboInput? Direction => Combo is not null
			                                && ComboActionParser.TryParse(Combo.Actions, out var seq)
			                                && Args.ComboAttackNum < seq.Count
				? seq[(int)Args.ComboAttackNum]
				: null;
		}
	}
}
