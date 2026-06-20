using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
{
	// Harvests the player's starting abilities from the retail 0x2052 ability-grant packets (the real wire format,
	// NOT the C++ 0x4d scheme; see Faolan/GameServer/Packets/Send0x2052_GrantAbilities.cs). The grant body carries
	// the ability's Slot (template word) + Data0..4 identity quad; names are client-side (resolved by id).
	//
	// CLASS-AWARE: each create-flow capture is attributed to its character's class via the ChangeClass editor command
	// it contains (numericValue/2 = class id; same convention as HandleCommandPacket). An ability granted under two
	// or more distinct classes is GENERIC (Class=0, the universal kit - Normal Attack L/R/F + toggle-combat /
	// switch-weapon / utility); one granted under a single class is class-specific (Class=that id). GrantStartingAbilities
	// then grants the generic set plus the character's own class set. Coverage is necessarily incomplete (only the
	// classes/levels present in the captures) - that is expected.
	public static class CaptureAbilityImporter
	{
		private const ushort AbilityGrantOpcode = 0x2052;
		private const uint ChangeClassSubOpcode = 0xA36D3B74; // client->server character-editor script callback

		// The few human-readable names the old archive knows (the rest are client-side / unknown).
		private static readonly Dictionary<uint, string> KnownNames = new Dictionary<uint, string>
		{
			[769094] = "Normal Attack Left",
			[769095] = "Normal Attack Right",
			[769099] = "Normal Attack Front",
			[3316611] = "Toggle Combat",
			[3675584] = "Switch Weapon"
		};

		// rawPackets = ALL captures (catalogs every ability's Slot/Data identity seen on the wire). createCaptures =
		// the create-flow captures, one CapturedPacket list each, used to flag IsStartingAbility AND attribute the
		// per-capture class (from the ChangeClass command in that capture).
		public static async Task<(int Seen, int Inserted, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets,
			IEnumerable<IReadOnlyList<byte[]>> createCaptures, ILogger logger = null)
		{
			var grants = new Dictionary<uint, (uint Slot, uint D0, uint D1, uint D2, uint D3, uint D4)>();
			foreach (var packet in rawPackets)
				TryReadAbilityGrant(packet, grants);

			// Per-create-capture: detect the class, collect its granted ability ids. Build id -> set of classes.
			var idToClasses = new Dictionary<uint, HashSet<uint>>();
			var starters = new HashSet<uint>();
			foreach (var capture in createCaptures)
			{
				var cls = DetectClass(capture);
				var perCapture = new Dictionary<uint, (uint, uint, uint, uint, uint, uint)>();
				foreach (var packet in capture)
					TryReadAbilityGrant(packet, perCapture);

				foreach (var id in perCapture.Keys)
				{
					starters.Add(id);
					if (cls is { } c)
						(idToClasses.TryGetValue(id, out var set) ? set : idToClasses[id] = new HashSet<uint>()).Add(c);
				}
			}

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
				a.Class = starters.Contains(id) ? ClassFor(id) : 0u;
				if (string.IsNullOrEmpty(a.Name) && KnownNames.TryGetValue(id, out var n))
					a.Name = n;
			}

			await db.SaveChangesAsync();
			var generic = starters.Count(s => ClassFor(s) == 0);
			logger?.LogInformation("CaptureAbilityImporter: {Seen} ability grants seen ({Starters} starting: {Generic} generic + {Specific} class-specific), {Inserted} new, {Updated} updated.",
				grants.Count, starters.Count, generic, starters.Count - generic, inserted, updated);
			return (grants.Count, inserted, updated);

			// An ability granted under >= 2 distinct classes (or under none we could attribute) is GENERIC (Class 0);
			// one seen under exactly one class is specific to it.
			uint ClassFor(uint id)
			{
				return idToClasses.TryGetValue(id, out var classes) && classes.Count == 1
					? classes.Single()
					: 0u;
			}
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

		// The character class chosen in this capture, read from its ChangeClass editor callback (client->server
		// 0xA36D3B74, body = [ack u32][instance u32][reserved u8][pbLen u32][protobuf: f3 varint=value, f4 string=token]).
		// The editor sends class as 2x the real id (see HandleCommandPacket), so the class = value/2. Returns null when
		// the capture carries no ChangeClass (e.g. a login capture) so its grants stay generic by default.
		internal static uint? DetectClass(IReadOnlyList<byte[]> capture)
		{
			foreach (var packetBytes in capture)
			{
				try
				{
					if (TryReadChangeClass(packetBytes, out var classId, out _))
						return classId;
				}
				catch
				{
					// not a parseable editor callback
				}
			}

			return null;
		}

		// Mirrors HandleCommandPacket: a 0x2000 IIR whose sub-opcode is 0xA36D3B74 (editor script callback) and whose
		// body is [ack u32][instance u32][reserved u8][pbLen u32][protobuf: f3 varint=value, f4 string=token]. True only
		// for the ChangeClass token, returning value/2 as the class id (the editor sends class as 2x the real id).
		private static bool TryReadChangeClass(byte[] packetBytes, out uint classId, out string token)
		{
			classId = 0;
			token = null;
			var packet = new ConanPacket(packetBytes);
			var s = packet.Body;
			if (s.Length < 21)
				return false;

			s.ReadUInt32(); // IIR length
			if (s.ReadUInt32() != ChangeClassSubOpcode) // sub-opcode
				return false;
			s.ReadUInt32(); // ack
			s.ReadUInt32(); // client instance
			s.ReadByte(); // reserved
			var pbLength = s.ReadUInt32();
			var pbEnd = s.Position + pbLength;

			uint numericValue = 0;
			while (s.Position < pbEnd && s.Position < s.Length)
			{
				var tag = s.ReadByte();
				var field = tag >> 3;
				var wireType = tag & 0x07;
				switch (wireType)
				{
					case 0:
						var v = ReadVarint(s);
						if (field == 3)
							numericValue = (uint)v;
						break;
					case 2:
						// field 4 = token name, field 5 = value string; only field 4 is the command token. (Earlier
						// this captured EVERY length-delimited field, so the trailing field overwrote the token.)
						var str = s.ReadStringByteLength();
						if (field == 4)
							token = str;
						break;
					default:
						return false; // unexpected wire type - not a clean editor callback
				}
			}

			if (token != "ChangeClass")
				return false;
			classId = numericValue / 2;
			return true;
		}

		private static ulong ReadVarint(FaolanStream s)
		{
			ulong value = 0;
			var shift = 0;
			while (true)
			{
				var b = s.ReadByte();
				value |= (ulong)(b & 0x7f) << shift;
				if ((b & 0x80) == 0)
					break;
				shift += 7;
			}

			return value;
		}
	}
}
