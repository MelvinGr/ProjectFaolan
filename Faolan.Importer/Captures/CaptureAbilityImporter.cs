using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests the player's starting abilities from the retail 0x2052 ability-grant packets (the real wire format,
	// NOT the C++ 0x4d scheme; see Faolan/GameServer/Packets/Send0x2052_GrantAbilities.cs). The grant body carries
	// the ability's Slot (template word) + Data0..4 identity quad; names are client-side (resolved by id).
	//
	// CLASS-AWARE: each create-flow capture is attributed to its character's class via the ChangeClass editor command
	// it contains (numericValue/2 = class id; same convention as HandleCommandPacket). An ability granted under two
	// or more distinct classes is GENERIC (Class=0, the universal kit - Normal Attack L/R/F + toggle-combat /
	// switch-weapon / utility); one granted under a single class is class-specific (Class=that id). GrantCharacterAbilities
	// then grants the generic set plus the character's own class set. Coverage is necessarily incomplete (only the
	// classes/levels present in the captures) - that is expected.
	public static class CaptureAbilityImporter
	{
		private const uint ChangeClassSubOpcode = 0xA36D3B74; // client->server character-editor script callback
		private const ushort AbilityGrantOpcode = 0x2052;
		private const ushort IirCarrierOpcode = 0x2000; // ComboBookIIR rides the 0x2000 world-conversation carrier
		private const uint ComboBookSubOpcode = 0xA8BBEB7F;

		// The few human-readable names the old archive knows (the rest are client-side / unknown).
		[Obsolete]
		private static readonly Dictionary<uint, string> KnownNames = new Dictionary<uint, string>
		{
			[769094] = "Normal Attack Left",
			[769095] = "Normal Attack Right",
			[769099] = "Normal Attack Front",
			[3316611] = "Toggle Combat",
			[3675584] = "Switch Weapon",
			[269272] = "Rest"
		};

		// Abilities whose grant is LEVEL-gated, not part of the L1 create kit (#209/#73). Rest (269272) is absent
		// from every create capture's login grants but arrives as an announced 0x2052 at the first ding (L1->L2:
		// Blablabla frame 9937, Blablatest 1357, Stihetfv 685) and is in every L2+ char's login re-grant - so its
		// unlock level is 2. The create-capture starter scan would otherwise misattribute it as a starting ability
		// (the ding happens inside the create captures). GetStartingAbilities filters Level <= level, so a known
		// level both keeps it out of the L1 kit and re-grants it on every L2+ login, matching all 30+ captures.
		private static readonly Dictionary<uint, byte> KnownLevels = new Dictionary<uint, byte>
		{
			[269272] = 2 // Rest
		};

		// rawPackets = ALL captures. Catalogs every ability's Id + Slot/Data identity seen on the wire (the Data quad is
		// also its RDB 1000020 TEA key, so RdbAbilityImporter can name it). The class<->ability ACCESS and per-class
		// unlock LEVEL are NOT set here - they are derived from the per-character grants (DeriveClassAbilitiesAsync).
		public static async Task<(int Seen, int Inserted, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var grants = new Dictionary<uint, (uint Slot, uint D0, uint D1, uint D2, uint D3, uint D4)>();
			foreach (var packet in rawPackets)
				TryReadAbilityGrant(packet, grants);

			if (grants.Count == 0)
				return (0, 0, 0);

			var ids = grants.Keys.ToList();
			var existing = await db.Abilities.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

			var inserted = 0;
			var updated = 0;
			foreach (var (id, g) in grants)
			{
				if (!existing.TryGetValue(id, out var a))
				{
					a = new Ability
					{
						Id = id,
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
				if (string.IsNullOrEmpty(a.Name) && KnownNames.TryGetValue(id, out var n))
					a.Name = n;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureAbilityImporter: {Seen} ability grants seen, {Inserted} new, {Updated} updated.", grants.Count, inserted, updated);
			return (grants.Count, inserted, updated);
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

		// Seeds each captured character's granted ABILITY set (CharacterAbility rows) from the 0x2052 grants, keyed by
		// the grant's RpcHeader receiver instance (= character id) - the ability twin of CaptureSpellImporter's learned-
		// spell seeding. Abilities are NOT in the snapshot known-list, so this is the authoritative per-character set
		// (the action bar is only shortcuts). Only characters already in the DB are touched; idempotent.
		public static async Task<(int Characters, int Inserted)> ImportCharacterAbilitiesAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, isCombo: false);

			var (characters, inserted) = await SeedPerCharacterAsync(db, byCharacter,
				async cid => (await db.CharacterAbilities.Where(a => a.CharacterId == cid).Select(a => a.AbilityId).ToListAsync()).ToHashSet(),
				(cid, id) => db.CharacterAbilities.Add(new CharacterAbility { CharacterId = cid, AbilityId = id }));

			logger?.LogInformation("CaptureAbilityImporter: per-character ability sets for {C} characters, {I} new CharacterAbility rows.", characters, inserted);
			return (characters, inserted);
		}

		// Seeds each captured character's COMBO book (CharacterCombo rows) from the ComboBookIIR grants (0x2000 carrier,
		// sub-opcode 0xA8BBEB7F), keyed by the RpcHeader receiver instance (= character id). Combos are class/level
		// content the client's combo book gains per level; a max-level imported character never dings, so this persisted
		// set is what GrantCharacterCombos re-emits on login. Only characters already in the DB are touched; idempotent.
		public static async Task<(int Characters, int Inserted)> ImportCharacterCombosAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, isCombo: true);

			var (characters, inserted) = await SeedPerCharacterAsync(db, byCharacter,
				async cid => (await db.CharacterCombos.Where(c => c.CharacterId == cid).Select(c => c.ComboId).ToListAsync()).ToHashSet(),
				(cid, id) => db.CharacterCombos.Add(new CharacterCombo { CharacterId = cid, ComboId = id }));

			logger?.LogInformation("CaptureAbilityImporter: combo books for {C} characters, {I} new CharacterCombo rows.", characters, inserted);
			return (characters, inserted);
		}

		// Derives the class<->ability ACCESS (ClassAbility) from the per-character grants (CharacterAbility x Character):
		// a row per (class, ability) that a captured character of that class holds, with the ability's PER-CLASS unlock
		// level = the MIN level of that class's holders (create-flow chars land its universal kit at L1; gameplay-only
		// abilities land at their high char levels -> naturally gated out of a fresh char). The data-driven replacement
		// for IsStartingAbility + the single Ability.Class - true multi-class (Spellweaving lands under every captured
		// caster class, not a wrong "generic"). The ability RDB template carries no class/level, so this is the source.
		public static async Task<(int Inserted, int Updated)> DeriveClassAbilitiesAsync(IDatabaseContext db, ILogger logger = null)
		{
			var grants = await db.CharacterAbilities
				.Join(db.Characters, ca => ca.CharacterId, c => c.Id, (ca, c) => new { ca.AbilityId, c.Level, c.Class })
				.ToListAsync();

			var abilityIds = (await db.Abilities.Select(a => a.Id).ToListAsync()).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));

			var inserted = 0;
			var updated = 0;
			foreach (var g in grants.Where(x => abilityIds.Contains(x.AbilityId)).GroupBy(x => (x.Class, x.AbilityId)))
			{
				var minLevel = (byte)Math.Clamp(g.Min(x => (int)x.Level), 1, 255);
				if (existing.TryGetValue(g.Key, out var row))
				{
					if (row.Level != minLevel)
					{
						row.Level = minLevel;
						updated++;
					}
				}
				else
				{
					db.ClassAbilities.Add(new ClassAbility { Class = g.Key.Class, AbilityId = g.Key.AbilityId, Level = minLevel });
					inserted++;
				}
			}

			if (inserted > 0 || updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureAbilityImporter: derived {Inserted} ClassAbility rows ({Updated} levels updated) from per-character grants.", inserted, updated);
			return (inserted, updated);
		}

		// Shared per-character seeding: for every (characterId -> id set), skip characters not in the DB and ids already
		// recorded, add the rest via the supplied factory, save once. Returns (characters touched, rows inserted).
		private static async Task<(int Characters, int Inserted)> SeedPerCharacterAsync(IDatabaseContext db, Dictionary<uint, HashSet<uint>> byCharacter,
			Func<uint, Task<HashSet<uint>>> loadExisting, Action<uint, uint> add)
		{
			if (byCharacter.Count == 0)
				return (0, 0);

			var characters = 0;
			var inserted = 0;
			foreach (var (characterId, ids) in byCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var existing = await loadExisting(characterId);
				var added = false;
				foreach (var id in ids)
				{
					if (!existing.Add(id))
						continue;

					add(characterId, id);
					inserted++;
					added = true;
				}

				if (added)
					characters++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			return (characters, inserted);
		}

		// Reads one per-character grant into byCharacter, keyed by the RpcHeader receiver (= character id): an ability
		// grant (0x2052, ability id = body word 1, doubled) or a ComboBookIIR combo (0x2000 carrier, sub 0xA8BBEB7F,
		// comboId at body offset 18 after [len][subop][marker][instance][u16 addflag]).
		private static void TryReadCharacterGrant(byte[] packetBytes, IDictionary<uint, HashSet<uint>> byCharacter, bool isCombo)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				var body = packet.Body.ReadRemainingArray();

				uint id;
				if (isCombo)
				{
					if (packet.Opcode != IirCarrierOpcode || body.Length < 22 || BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) != ComboBookSubOpcode)
						return;
					id = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(18, 4));
				}
				else
				{
					if (packet.Opcode != AbilityGrantOpcode || body.Length < 12)
						return;
					id = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)); // ability id (word 1)
					if (id != BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8, 4)))
						return; // id appears twice; guard against mis-parses
				}

				if (id == 0)
					return;

				var characterId = packet.Receiver.Instance;
				(byCharacter.TryGetValue(characterId, out var set)
					? set
					: byCharacter[characterId] = new HashSet<uint>()).Add(id);
			}
			catch
			{
				// not a parseable per-character grant
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
	}
}
