using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report weapon-set`: can a capture say which WEAPON SET a character had drawn? (Gitea #588 / #400.)
	//
	// `Character.ActiveWeaponSet` is never set by the character importer, so every imported character gets the CLR
	// default (Primary). #588 calls that *"a real data gap on its own — `ActiveWeaponSet` is load-bearing (it decides
	// which weapons the gear aggregate counts) and it is currently a guess for every imported character"*, and names
	// recovering it from the capture as the fix.
	//
	// The route: retail's swap is `BehaviorId.SwapAlternateEquipmentCommand` (46), *accompanied by*
	// `DispatchSetItemStat` — one stat on one container slot. So if a slot stat marks a weapon as DRAWN, it is in
	// those records, and the importer could read it off a login rather than defaulting.
	//
	// ⚠️ This prints the evidence, it does not decide. A stat that appears on a weapon slot is not thereby the
	// drawn-flag — the same message carries the bank's per-slot registration (stat 0x17) — so the report shows the
	// whole (kind, slot, stat, value) alphabet and where the swap records sit in it.
	//
	// ⭐ THE ANSWER, over 6,331 records and 30 swaps: the drawn set IS on the wire, as a stat-0x17 RE-REGISTRATION
	// on the newly-active `Equipped` weapon slot, emitted with the swap. The slots alternate 6 <-> 20, which
	// corroborates #400's "the acting player's newly-active weapon main slot alternates 6<->20" from a second
	// message. `demol80-demonologist_login_gameplay_annotated_2` runs
	// `6->20->8->6->20->21->6->20->8->6->20->21` and ends on a SECONDARY slot — which is exactly the case #588
	// could not decide.
	//
	// ⛔ But it is NOT readable at LOGIN. Every equipped slot carries stat 0x17 as its ordinary per-slot
	// registration (Equipped 16 x119, 18 x113, 6 x112, 5 x108, ...), so a fresh snapshot has no drawn-flag to
	// read: only the CHANGE at a swap identifies the set. So recovering `ActiveWeaponSet` means replaying a
	// capture's swaps, and it only works for a character whose capture contains one — 30 swap records across 132
	// captures, on 6 characters.
	public static partial class ReportCli
	{
		// The weapon slots, from `NpcWeapon` / `CharacterEquipmentSlot`: the melee pair and the ranged pair.
		private static readonly uint[] WeaponSetSlots = [6, 8, 20, 21];

		private static void WeaponSetReport(IReadOnlyList<NamedCapture> captures)
		{
			var byShape = new Dictionary<(ContainerKind Kind, uint Slot, uint Stat), List<uint>>();
			var nearSwap = new List<(string Capture, int Index, SetItemStat Stat)>();
			var swaps = 0;
			var setItemStats = 0;

			foreach (var capture in captures)
			{
				// Packet index of every swap command, so a SetItemStat can be called "near" one.
				var swapAt = new List<int>();
				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;

					if (p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) && bcc.BehaviorId == (uint)BehaviorId.SwapAlternateEquipmentCommand)
						swapAt.Add(i);
				}

				swaps += swapAt.Count;

				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (!p.IsDownload || p.WireOpcode != CapturedPacket.Wire(GameCharInterfaceMethods.DispatchSetItemStat))
						continue;

					SetItemStat stat;
					try
					{
						stat = SetItemStatCodec.Parse(p.Body);
					}
					catch (Core.Extensions.CodecParseException)
					{
						continue; // a body the dump cut short, or a colliding wire opcode - not a grammar failure
					}

					setItemStats++;
					var key = (stat.Container, stat.Slot, stat.StatId);
					if (!byShape.TryGetValue(key, out var values))
						byShape[key] = values = [];

					values.Add(stat.Value);

					// ⭐ The load-bearing window: a SetItemStat within 20 packets of a swap command is the one the
					// swap "is accompanied by", which is what #400's note describes.
					if (swapAt.Any(s => Math.Abs(s - i) <= 20))
						nearSwap.Add((capture.Name, i, stat));
				}
			}

			Console.WriteLine($"{setItemStats:N0} captured DispatchSetItemStat record(s) over {captures.Count} captures; "
			                  + $"{swaps} SwapAlternateEquipmentCommand record(s).");
			Console.WriteLine();

			Console.WriteLine("the whole (containerKind, slot, statId) alphabet, most-seen first:");
			foreach (var ((kind, slot, statId), values) in byShape.OrderByDescending(kv => kv.Value.Count).Take(20))
			{
				var weapon = WeaponSetSlots.Contains(slot) ? " <- a WEAPON slot" : "";
				Console.WriteLine($"  kind={kind,-5} slot={slot,-5} stat=0x{statId:X4}  x{values.Count,-5} values=[{string.Join(",", values.Distinct().Order().Take(6))}]{weapon}");
			}

			Console.WriteLine();
			Console.WriteLine("⭐ the EQUIPPED weapon-slot registrations at a SWAP, in capture order - the newly-active");
			Console.WriteLine("   slot is the one re-registered, so the LAST one is the set the character ended on:");
			foreach (var g in nearSwap.GroupBy(x => x.Capture).OrderBy(g => g.Key, StringComparer.Ordinal))
			{
				var seq = g.OrderBy(x => x.Index)
					.Where(x => x.Stat.Container == ContainerKind.Equipped && WeaponSetSlots.Contains(x.Stat.Slot))
					.ToList();
				if (seq.Count == 0)
					continue;

				var last = seq[^1].Stat.Slot;
				var set = last is 20 or 21 ? "Secondary" : "Primary";
				Console.WriteLine($"  {Trim(g.Key, 58),-58} slots {string.Join("->", seq.Select(x => x.Stat.Slot))}   ends on {set}");
			}

			Console.WriteLine();
			Console.WriteLine(byShape.Keys.Any(k => WeaponSetSlots.Contains(k.Slot))
				? "  A weapon slot DOES carry a slot stat - read the alphabet above before calling any of it the drawn flag."
				: "  ⛔ NO captured DispatchSetItemStat targets a weapon slot at all, so the drawn set is not recoverable\n"
				  + "     from this message and `ActiveWeaponSet` stays a default. See Gitea #588.");
		}
	}
}
