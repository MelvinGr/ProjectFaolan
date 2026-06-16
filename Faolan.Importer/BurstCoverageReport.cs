using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer.Spawn;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Measures the "construct, don't replay" progress: for every stored MapWorldBurst entity, how much of the world
	// build can already be rebuilt byte-exact from the structured DB tables (NpcSpawns / WorldObjects / the generated
	// player + globals) versus what is still REPLAYED verbatim from the captured burst bytes. The replay path uses the
	// same builders behind a byte-identical safety gate, so "reconstructable" here means exactly "the table reproduces
	// this record and the burst row is redundant". When a map reaches 100% reconstructable its burst rows can be
	// dropped; this report is the gate that tracks the effort and the residual burst-only families.
	public static class BurstCoverageReport
	{
		// Known IIR types that have no table/builder yet, so they can only come from the stored burst (the work that
		// remains). Anything not here and not a spawn/object/player record is reported as an unknown type to chase.
		private static readonly Dictionary<uint, string> BurstOnlyFamilies = new Dictionary<uint, string>
		{
			[0x96C46740] = "MoveTask (no patrol-path table)",
			[0x642CD3D6] = "BehaviorControlCenter/Interact (no table)",
			[0xF508F4C1] = "GameActionList (no table)",
			[0xF98E10B3] = "NPC StatIir (no NPC-stats table)",
			// Transient candidates - likely droppable like Move/combat, but each needs an in-client check first.
			[0xE7F4F8F8] = "SimpleDynelVisualChange (transient? in-client check)",
			[0xD751EBB4] = "StopIIR (transient? in-client check)",
			[0x04B80B24] = "SetPosIIR (transient? in-client check)",
			[0x8D5A8B6B] = "FormatFeedback (transient? in-client check)"
		};

		public static async Task RunAsync(DatabaseContext db)
		{
			var bursts = await db
				.MapWorldBursts
				.Include(b => b.Entities)
				.ToListAsync();

			var npcsByMap = (await db.NpcSpawns.ToListAsync())
				.GroupBy(n => n.MapId)
				.ToDictionary(g => g.Key, g => Index(g, n => (n.PositionX, n.PositionY, n.PositionZ)));
			// World objects are keyed by (position, identity): two props can share an exact position (#222), so the
			// identity (display_name, read from the row's own Data) disambiguates them - matching what the importer
			// dedup and the live world-entry replay do.
			var objectsByMap = (await db.WorldObjects.ToListAsync())
				.GroupBy(o => o.MapId)
				.ToDictionary(g => g.Key, g => IndexObjects(g));

			Console.WriteLine();
			Console.WriteLine("Burst reconstruction coverage (construct-don't-replay gate)");
			Console.WriteLine("  map      total   constructed   burst-only   %built   burst-only breakdown");
			Console.WriteLine("  -------------------------------------------------------------------------------------");

			var grandTotal = 0;
			var grandBuilt = 0;
			var grandResidual = new SortedDictionary<string, int>(StringComparer.Ordinal);

			foreach (var burst in bursts.OrderBy(b => b.MapId))
			{
				npcsByMap.TryGetValue(burst.MapId, out var npcs);
				objectsByMap.TryGetValue(burst.MapId, out var objects);
				npcs ??= new Dictionary<(int, int, int), NpcSpawn>();
				objects ??= new Dictionary<(int, int, int, string), WorldObject>();

				var built = 0;
				var residual = new SortedDictionary<string, int>(StringComparer.Ordinal);

				foreach (var e in burst.Entities)
				{
					if (IsConstructable(e, burst.CapturedInstance, npcs, objects, out var residualFamily))
						built++;
					else
						Bump(residual, residualFamily);
				}

				var total = burst.Entities.Count;
				var burstOnly = total - built;
				var pct = total == 0
					? 100.0
					: 100.0 * built / total;
				var breakdown = string.Join(", ", residual.Select(kv => $"{kv.Value} {kv.Key}"));
				Console.WriteLine($"  {burst.MapId,-7} {total,6} {built,13} {burstOnly,12} {pct,7:0.0}   {breakdown}");

				grandTotal += total;
				grandBuilt += built;
				foreach (var kv in residual)
					Bump(grandResidual, kv.Key, kv.Value);
			}

			var grandResidualCount = grandTotal - grandBuilt;
			var grandPct = grandTotal == 0
				? 100.0
				: 100.0 * grandBuilt / grandTotal;
			Console.WriteLine("  -------------------------------------------------------------------------------------");
			Console.WriteLine($"  ALL     {grandTotal,6} {grandBuilt,13} {grandResidualCount,12} {grandPct,7:0.0}");
			Console.WriteLine();
			Console.WriteLine("  Remaining burst-only records to bring under construction:");
			foreach (var kv in grandResidual.OrderByDescending(kv => kv.Value))
				Console.WriteLine($"    {kv.Value,5}  {kv.Key}");
		}

		private static void Bump(IDictionary<string, int> counts, string key, int by = 1)
		{
			counts.TryGetValue(key, out var n);
			counts[key] = n + by;
		}

		private static Dictionary<(int, int, int), T> Index<T>(IEnumerable<T> rows, Func<T, (float, float, float)> pos)
		{
			var map = new Dictionary<(int, int, int), T>();
			foreach (var row in rows)
			{
				var (x, y, z) = pos(row);
				map.TryAdd(PositionKey(x, y, z), row);
			}

			return map;
		}

		// As Index, but keys world objects by (position, identity) so two props at one position both keep a slot (#222).
		private static Dictionary<(int, int, int, string), WorldObject> IndexObjects(IEnumerable<WorldObject> rows)
		{
			var map = new Dictionary<(int, int, int, string), WorldObject>();
			foreach (var row in rows)
			{
				var (x, y, z) = PositionKey(row.PositionX, row.PositionY, row.PositionZ);
				map.TryAdd((x, y, z, WorldObjectBuilder.ReadIdentity(row)), row);
			}

			return map;
		}

		// True if this record is already reproducible without the burst: a server-generated global/player record, or a
		// world entity whose table row (matched by spawn position) rebuilds byte-identical via the same builder the
		// replay uses. Otherwise it is still burst-only; `residualFamily` names why so the report can tally it.
		private static bool IsConstructable(MapWorldBurstEntity e, uint playerInstance, IReadOnlyDictionary<(int, int, int), NpcSpawn> npcs, IReadOnlyDictionary<(int, int, int, string), WorldObject> objects, out string residualFamily)
		{
			residualFamily = null;

			// The 0x2076 spawn manifest (opcode != 0x2000) - no builder yet.
			if (e.Opcode == 0x2076)
			{
				residualFamily = "0x2076 spawn manifest (no builder)";
				return false;
			}

			// Server-generated globals + player-state are rebuilt live on world entry, never replayed from the burst.
			if (IirMessageTypes.IsServerGenerated(e.IirType, e.Handle, playerInstance))
				return true;

			switch (e.IirType)
			{
				case 0xFA015807: // entity spawn
					// The player's own spawn (handle == the live instance) is fully DB-generated.
					if (e.Handle == playerInstance)
						return true;

					if (!TryReadBodyAndKey(e, out var npcBodyArr, out var npcKey))
					{
						residualFamily = "NPC spawn (malformed body)";
						return false;
					}

					if (npcs.TryGetValue(npcKey, out var npcRow) && NpcSpawnBuilder.TryBuild(npcRow, e.Handle, out var npcBody) && npcBodyArr
						    .AsSpan()
						    .SequenceEqual(npcBody))
						return true;

					residualFamily = npcs.ContainsKey(npcKey)
						? "NPC spawn (cross-session: scaffold not yet lifted to columns)"
						: "NPC spawn (no NpcSpawns row)";
					return false;

				case 0x66AEDD50: // SimpleDynel
				case 0xF096251E: // LootBag
				case 0x33A56FB0: // Door
				case 0x6BD56736: // Destructible
				case 0x1FD6412B: // ClimbingDynel
					if (!TryReadBodyAndKey(e, out var objBodyArr, out var objKey))
					{
						residualFamily = "world object (malformed body)";
						return false;
					}

					var (ox, oy, oz) = objKey;
					if (!objects.TryGetValue((ox, oy, oz, WorldObjectBuilder.ReadIdentity(objBodyArr)), out var objRow))
					{
						residualFamily = "world object (no WorldObjects row)";
						return false;
					}

					if (!WorldObjectBuilder.TryBuildOntoLivePlacement(objRow, objBodyArr, out var objBody))
					{
						residualFamily = "world object (row template unparseable)";
						return false;
					}

					if (objBodyArr
					    .AsSpan()
					    .SequenceEqual(objBody))
						return true;

					residualFamily = "world object (cross-session: identity differs from table row)";
					return false;

				case 0x737A6DF9: // FullChar - the player's own (inventory snapshot) is rebuilt live; an NPC's is not.
					if (e.Handle == playerInstance)
						return true;

					residualFamily = "FullChar (non-player, no table)";
					return false;

				default:
					residualFamily = BurstOnlyFamilies.TryGetValue(e.IirType, out var name)
						? name
						: $"0x{e.IirType:X8} (unknown IIR type)";
					return false;
			}
		}

		private static (int, int, int) PositionKey(float x, float y, float z)
		{
			return ((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), (int)MathF.Round(z * 100f));
		}

		// Rebuilds the entity's full packet, slices out the IIR body (what the builders compare against) and reads the
		// spawn position from it. False if the body is too short to carry a transform.
		private static bool TryReadBodyAndKey(MapWorldBurstEntity e, out byte[] body, out (int, int, int) key)
		{
			body = null;
			key = default;

			var full = IirRecordCodec.Rebuild(e);
			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			if (bodyStart + SpawnPacketBuilder.PosZOffset + 4 > full.Length)
				return false;

			body = full[bodyStart..];
			key = PositionKey(BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.PosZOffset, 4)));
			return true;
		}
	}
}
