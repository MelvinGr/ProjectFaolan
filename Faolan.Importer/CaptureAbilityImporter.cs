using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
{
	// Harvests the player's starting abilities from the retail 0x2052 ability-grant packets (the real wire format,
	// NOT the C++ 0x4d scheme; see Faolan/GameServer/Packets/Send0x2052_GrantAbilities.cs). Every captured
	// level-1 character - across classes - receives the SAME 7 universal abilities here (3 Normal Attack
	// directions + toggle-combat / switch-weapon / two utility), which is more than the old archive `const_abilitys`
	// listed (only 769094/769099). This is the authoritative source for the starting kit; the body carries the
	// ability's Slot (template word) + Data0..4 identity quad. Names are client-side (resolved by id); the archive
	// supplies the few it has, so we only fill Name when empty.
	public static class CaptureAbilityImporter
	{
		private const ushort AbilityGrantOpcode = 0x2052;

		// The few human-readable names the old archive knows (the rest are client-side / unknown).
		private static readonly Dictionary<uint, string> KnownNames = new Dictionary<uint, string>
		{
			[769094] = "Normal Attack Left",
			[769095] = "Normal Attack Right",
			[769099] = "Normal Attack Front",
			[3316611] = "Toggle Combat",
			[3675584] = "Switch Weapon"
		};

		// rawPackets = ALL captures (catalogs every ability seen on the wire); startingPackets = the CREATE-flow
		// captures only (these define IsStartingAbility - the universal kit every new character gets at creation).
		public static async Task<(int Seen, int Inserted, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, IEnumerable<byte[]> startingPackets, ILogger logger = null)
		{
			var grants = new Dictionary<uint, (uint Slot, uint D0, uint D1, uint D2, uint D3, uint D4)>();
			foreach (var packet in rawPackets)
				TryReadAbilityGrant(packet, grants);

			var startingIds = new Dictionary<uint, (uint, uint, uint, uint, uint, uint)>();
			foreach (var packet in startingPackets)
				TryReadAbilityGrant(packet, startingIds);
			var starters = startingIds.Keys.ToHashSet();

			if (grants.Count == 0)
				return (0, 0, 0);

			var ids = grants.Keys.ToList();
			var existing = await db
				.Abilities
				.Where(a => ids.Contains(a.Id))
				.ToDictionaryAsync(a => a.Id);

			var inserted = 0;
			var updated = 0;
			foreach (var (id, g) in grants)
			{
				if (!existing.TryGetValue(id, out var a))
				{
					a = new Ability
					{
						Id = id,
						Level = 1,
						Name = KnownNames.GetValueOrDefault(id, string.Empty)
					};
					db.Abilities.Add(a);
					existing[id] = a;
					inserted++;
				}
				else
					updated++;

				a.Slot = g.Slot;
				a.Data0 = g.D0;
				a.Data1 = g.D1;
				a.Data2 = g.D2;
				a.Data3 = g.D3;
				a.Data4 = g.D4;
				a.IsStartingAbility = starters.Contains(id);
				if (string.IsNullOrEmpty(a.Name) && KnownNames.TryGetValue(id, out var n))
					a.Name = n;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureAbilityImporter: {Seen} ability grants seen ({Starters} starting), {Inserted} new, {Updated} updated.", grants.Count, starters.Count, inserted, updated);
			return (grants.Count, inserted, updated);
		}

		// 0x2052 body (no length prefix): [slot][id][id][0][1][data0][data1..4][data1..4][constA][constB]x2[1][0][u8].
		private static void TryReadAbilityGrant(byte[] packetBytes, IDictionary<uint, (uint, uint, uint, uint, uint, uint)> grants)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != AbilityGrantOpcode)
					return;

				var body = packet.Body.ReadRemainingArray();
				if (body.Length < 40)
					return;

				uint U(int i)
				{
					return BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i * 4, 4));
				}

				var slot = U(0);
				var id = U(1);
				if (id == 0 || id != U(2))
					return; // id appears twice; guard against mis-parses

				grants[id] = (slot, U(5), U(6), U(7), U(8), U(9));
			}
			catch
			{
				// not a parseable 0x2052 ability grant
			}
		}
	}
}
