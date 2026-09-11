using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report weapon-set-drawn`: does a captured spawn say which of the two weapon sets is drawn?
	//
	// `Character.ActiveWeaponSet` is never set by the character importer, so every imported character gets the CLR
	// default (Primary) - and it is load-bearing, because it decides which weapons `CharacterEquipment` counts toward
	// the gear aggregate. `docs/rdb/client-data-tables.md` records it as *"not recoverable from the corpus: the swap
	// is observable but a login carries no such event, and the client derives its own display from the equipment
	// container"*.
	//
	// That is a claim about EVENTS, and there is a second place to look: the spawn record's own worn list. A spawn is
	// what every other client renders the character from, and only the drawn set renders - so if the f23 list carried
	// one set at a time it would state the answer without any event at all.
	//
	// The test needs no player identification and no per-capture reasoning: across every captured spawn, does any
	// single record carry a PRIMARY weapon slot (6/8) and a SECONDARY one (20/21) at the same time? If none does, the
	// list is the drawn set and the answer is on the wire. If some do, the list is the whole container and the
	// existing claim stands.
	public static partial class ReportCli
	{
		private static readonly uint[] PrimarySlots = [(uint)CharacterEquipmentSlot.MainWeapon, (uint)CharacterEquipmentSlot.OffHandWeapon];
		private static readonly uint[] SecondarySlots = [(uint)CharacterEquipmentSlot.RangedWeapon, (uint)CharacterEquipmentSlot.Ammo];

		private static void WeaponSetDrawn(IReadOnlyList<NamedCapture> captures, string? nameFilter = null)
		{
			// Per-name breakdown when asked: the whole-corpus answer says what the list is, but the question is
			// usually about one character, and a character whose spawn carries only one set is one the list answers
			// for.
			var perName = new SortedDictionary<string, SortedSet<uint>>(StringComparer.OrdinalIgnoreCase);
			int spawns = 0, armed = 0, primaryOnly = 0, secondaryOnly = 0, both = 0;
			var bothExamples = new List<string>();
			var slotCensus = new SortedDictionary<uint, int>();

			foreach (var c in captures)
			foreach (var p in c.Packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
					continue;

				if (!SpawnBodyReader.TryReadCharacterMessage(p.Body, out var read))
					continue;

				var msg = read.Message;
				if (msg == null)
					continue;

				spawns++;
				var slots = msg.InventoryEntries.Select(e => e.Pos).ToList();
				if (nameFilter != null && msg.Name != null && msg.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
				{
					if (!perName.TryGetValue(msg.Name, out var seen))
						perName[msg.Name] = seen = [];
					foreach (var s in slots.Where(s => PrimarySlots.Contains(s) || SecondarySlots.Contains(s)))
						seen.Add(s);
				}
				foreach (var s in slots)
					slotCensus[s] = slotCensus.GetValueOrDefault(s) + 1;

				var hasPrimary = slots.Any(PrimarySlots.Contains);
				var hasSecondary = slots.Any(SecondarySlots.Contains);
				if (!hasPrimary && !hasSecondary)
					continue;

				armed++;
				if (hasPrimary && hasSecondary)
				{
					both++;
					if (bothExamples.Count < 10)
						bothExamples.Add($"    {c.Name} '{msg.Name}' slots [{string.Join(",", slots.Where(s => PrimarySlots.Contains(s) || SecondarySlots.Contains(s)))}]");
				}
				else if (hasPrimary)
				{
					primaryOnly++;
				}
				else
				{
					secondaryOnly++;
				}
			}

			Console.WriteLine($"=== {spawns} captured spawn(s), {armed} carrying a weapon slot ===");
			Console.WriteLine($"  PRIMARY only   (slots 6/8)    {primaryOnly,6}");
			Console.WriteLine($"  SECONDARY only (slots 20/21)  {secondaryOnly,6}");
			Console.WriteLine($"  BOTH at once                  {both,6}   <- if this is 0, the render list IS the drawn set");
			foreach (var e in bothExamples)
				Console.WriteLine(e);

			if (perName.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"  weapon slots seen on every spawn of a name matching '{nameFilter}':");
				foreach (var (name, seen) in perName)
				{
					var p1 = seen.Any(PrimarySlots.Contains);
					var p2 = seen.Any(SecondarySlots.Contains);
					var verdict = seen.Count == 0
						? "no weapon at all - nothing to choose between"
						: p1 && p2
							? "BOTH sets - the list cannot say which is drawn"
							: p1
								? "primary only - the default is right by construction"
								: "secondary only";
					Console.WriteLine($"    {name,-20} [{string.Join(",", seen)}]  {verdict}");
				}
			}

			Console.WriteLine();
			Console.WriteLine("  every equip slot the spawn lists carry, with how often:");
			foreach (var (slot, count) in slotCensus)
			{
				var name = Enum.IsDefined(typeof(CharacterEquipmentSlot), slot)
					? ((CharacterEquipmentSlot)slot).ToString()
					: "?";
				Console.WriteLine($"    {slot,3} {name,-16} {count,6}");
			}
		}
	}
}
