using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests the player's starting abilities from the retail 0x2052 ability-grant packets (the real wire format,
	// NOT the C++ 0x4d scheme; see Faolan.Core.Network/Codecs/Combat/AbilityGrantBody.cs). The grant body carries
	// the ability's Slot (template word) + Data0..4 identity quad; names are client-side (resolved by id).
	//
	// CLASS-AWARE: each create-flow capture is attributed to its character's class via the ChangeClass editor command
	// it contains (numericValue/2 = class id; same convention as HandleCommandPacket).
	//
	// NB there is no Class=0 "generic" row, whatever this comment used to claim: BOTH the CK_ClassAbility_Class CHECK
	// and GetClassAbilities' `ca.Class == @class` filter reject it, so a class-0 row would be unwritable AND unread.
	// The universal kit is MATERIALISED per class instead - all 12 rows - by DeriveClassAbilitiesFromCreatesAsync.
	// (The same stale "Class 0 = generic" claim is on ClassSpell; GetStartingSpells does honour `cs.Class == 0` there,
	// but the CHECK still forbids writing one, so it is equally dead.)
	public static class CaptureAbilityImporter
	{
		private const uint ChangeClassSubOpcode = (uint)GameServer0X2000Opcodes.ScryScriptIIR; // client->server character-editor script callback
		private const uint ComboBookSubOpcode = (uint)GameServer0X2000Opcodes.ComboBookIIR;
		private const ushort AddSpecialAbilityOpcode = (ushort)GameCharInterfaceOpcodes.DispatchAddSpecialAbility;
		private const ushort IirCarrierOpcode = 0x2000; // ComboBookIIR rides the 0x2000 world-conversation carrier

		// The RDB combo-catalog name-prefix -> class map. Unlike abilities, combos have no per-character grant to derive
		// from (retail never re-emits a ComboBookIIR on login - the combo book rides the 0x2076 world-entry snapshot
		// instead; see ImportCharacterCombosAsync), so class membership comes from the catalog's name prefix (e.g.
		// "Herald_..." = HeraldOfXotli). Hakotep's HoX lvl-24 0x2076 combo book validated that a catalog prefix reliably
		// identifies the class, so these LITERAL class-name prefixes are trusted - and they are exactly the 8 archetypes
		// that HAVE combos (matching the ellatha combos list), so casters (Necro/Demo/Priest/Tempest) correctly get none.
		// Matched case-insensitively (the catalog has both "BearShaman_" and "Bearshaman_"). AMBIGUOUS prefixes are
		// deliberately EXCLUDED (grant too little, not too much) - they map to multiple or uncertain classes and need a
		// fan-out / feat-tree taxonomy + validation: archetype-shared (Soldier_/Rogue_ -> 3 classes each), the Tortage
		// starters (Slave_/Oar_), feat-tree trees (Reaver/Liberator/Halberdier/Lich/Warmonk/Masterthief/SneakAttack),
		// NPC combos (combo_npc_*), racials (Cimmerian/Stygian/Khitan/Aquilonian), weapon-forms and one-off abilities.
		private static readonly (string Prefix, Class Class)[] ClassComboPrefixes =
		[
			("Guardian_", Class.Guardian), ("Conqueror_", Class.Conqueror), ("DarkTemplar_", Class.DarkTemplar), ("Barbarian_", Class.Barbarian), ("Ranger_", Class.Ranger),
			("Assassin_", Class.Assassin), ("BearShaman_", Class.BearShaman), ("Herald_", Class.HeraldOfXotli) // capture-validated: Hakotep HoX lvl-24 0x2076 combo book
		];

		// The few human-readable names the old archive knows (the rest are client-side / unknown). Still the only
		// name source for these ability ids, so it is used at import time (not obsolete).
		private static readonly Dictionary<uint, string> KnownNames = new()
		{
			[769094] = "Normal Attack Left",
			[769095] = "Normal Attack Right",
			[769099] = "Normal Attack Front",
			[3316611] = "Toggle Combat",
			[3675584] = "Switch Weapon",
			[269272] = "Rest"
		};

		// Derives the class<->ability ACCESS (ClassAbility) from the per-character grants (CharacterAbility x Character):
		// a row per (class, ability) that a captured character of that class holds, with the ability's PER-CLASS unlock
		// level = the MIN level of that class's holders (create-flow chars land its universal kit at L1; gameplay-only
		// abilities land at their high char levels -> naturally gated out of a fresh char). The data-driven replacement
		// for IsStartingAbility + the single Ability.Class - true multi-class (Spellweaving lands under every captured
		// caster class, not a wrong "generic"). The ability RDB template carries no class/level, so this is the source.
		public static async Task<(int Inserted, int Updated)> DeriveClassAbilitiesAsync(IDatabaseContext db, ILogger? logger = null)
		{
			var grants = await db
				.CharacterAbilities.Join(db.Characters, ca => ca.CharacterId, c => c.Id, (ca, c) => new
				{
					ca.AbilityId,
					c.Level,
					c.Class
				})
				.ToListAsync();

			var abilityIds = (await db.Abilities.Select(a => a.Id).ToListAsync()).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));

			// Skip characters with an invalid class (0/1): create-flow chars captured BEFORE class selection carry an
			// unset class the importer records as-is - real classes are the 12 defined Class enum values. Without this
			// guard those pollute the access map with a phantom "class 1" holding the universal kit.
			var inserted = 0;
			var updated = 0;
			foreach (var g in grants.Where(x => abilityIds.Contains(x.AbilityId) && Enum.IsDefined(x.Class)).GroupBy(x => (x.Class, x.AbilityId)))
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
					db.ClassAbilities.Add(new ClassAbility
					{
						Class = g.Key.Class,
						AbilityId = g.Key.AbilityId,
						Level = minLevel
					});
					inserted++;
				}
			}

			if (inserted > 0 || updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureAbilityImporter: derived {Inserted} ClassAbility rows ({Updated} levels updated) from per-character grants.", inserted, updated);
			return (inserted, updated);
		}

		// Seeds the UNIVERSAL level-1 ability kit onto every class (ClassAbility), from the create captures: the
		// 0x2052 grants that follow a create. Melvin: "Rest needs to be granted to all classes, same with the
		// directional attacks etc" - these are basic combat/movement/UI abilities, not class kit (#296).
		//
		// The kit is the UNION across all 12 create dumps, granted to all 12 classes - NOT each dump's own subset.
		// That is what the data says: all 12 dumps together yield only **8 distinct** ability ids (Rest, Upper Left /
		// Upper Right / Overhead Attack, Toggle Combat, Switch Weapons, Crouch, Dash). If any were class-specific the
		// union would be bigger, so every class grants a subset of ONE shared kit and the per-class differences are
		// capture noise - a dump that missed a packet - not design. Taking each dump's own subset left Conqueror
		// without Rest (its dump never shows the grant) while Guardian and the necro had it: an artefact, and exactly
		// the kind of hole that then reads as intentional.
		//
		// A dump carries ~14 0x2052 PACKETS but only ~7 DISTINCT ids - the grants repeat - so count ids, not packets;
		// the id-keyed dictionary collapses the duplicates for free. All 8 are already in the Abilities catalog.
		//
		// DetectClass resolves all 12 classes, but only to prove a dump IS a class create (a dump we can't attribute
		// might be some other capture) - the kit itself is class-independent.
		//
		// This exists because DeriveClassAbilitiesAsync below can only see classes we imported a CHARACTER for, and we
		// have exactly four (Assassin/Necro/HoX/Demo). That left the map in a state that was both wrong and
		// unreproducible: DarkTemplar had ZERO abilities despite its create dump carrying 14 grants, while seven other
		// classes held 7-8 rows apiece derived from characters that no longer exist - a from-scratch rebuild would
		// simply have lost them, since that pass only ever inserts/updates. Sourcing from the captures makes the whole
		// table reproducible and is the same trick starter-spells uses for spells: take the class from the capture,
		// not from a character that may never have been imported.
		//
		// Level 1: a create grant IS the level-1 kit by definition. DeriveClassAbilitiesAsync still runs afterwards and
		// contributes the gameplay-only abilities of the classes we do have characters for, at their real levels.
		public static async Task<(int Inserted, int Classes)> DeriveClassAbilitiesFromCreatesAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> createCaptures, ILogger? logger = null)
		{
			var kit = new HashSet<uint>();
			var dumps = 0;
			foreach (var capture in createCaptures)
			{
				if (DetectClass(capture) is not { } cls || !Enum.IsDefined((Class)cls))
					continue; // can't confirm this is a class create

				var grants = new Dictionary<uint, (uint, uint, uint, uint, uint, uint)>();
				foreach (var packet in capture)
					TryReadAddSpecialAbility(packet, grants);

				if (grants.Count == 0)
					continue; // capture never reached the grant burst

				dumps++;
				kit.UnionWith(grants.Keys);
			}

			if (kit.Count == 0)
				return (0, 0);

			var abilityIds = (await db.Abilities.Select(a => a.Id).ToListAsync()).ToHashSet();
			var existing = await db.ClassAbilities.ToDictionaryAsync(x => (x.Class, x.AbilityId));
			var inserted = 0;

			foreach (var cls in Enum.GetValues<Class>())
			foreach (var abilityId in kit)
			{
				if (!abilityIds.Contains(abilityId))
					continue; // a grant for an ability we never cataloged would be a catalog gap, not a fake id. Does not fire today: the kit is 8 distinct ids and all 8 are cataloged.

				if (existing.ContainsKey((cls, abilityId)))
					continue; // already known; DeriveClassAbilitiesAsync owns the level for those

				db.ClassAbilities.Add(new ClassAbility
				{
					Class = cls,
					AbilityId = abilityId,
					Level = 1 // the universal kit is level 1 by definition
				});
				existing[(cls, abilityId)] = null!;
				inserted++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureAbilityImporter: universal kit of {Kit} abilities (from {Dumps} create dumps) -> {Inserted} new class-ability rows across {Classes} classes.", kit.Count,
				dumps, inserted, Enum.GetValues<Class>().Length);
			return (inserted, kit.Count);
		}

		// Derives the class<->combo ACCESS map (ClassCombo) from the combo catalog's class-name prefixes - the combo
		// analogue of DeriveClassAbilitiesAsync (which derives from grants; combos have none). Combo.Level supplies the
		// per-class unlock level. Idempotent.
		public static async Task<(int Inserted, int Updated)> DeriveClassCombosAsync(IDatabaseContext db, ILogger? logger = null)
		{
			var combos = await db
				.Combos.Select(c => new
				{
					c.Id,
					c.Name,
					c.Level
				})
				.ToListAsync();
			var existing = await db.ClassCombos.ToDictionaryAsync(x => (x.Class, x.ComboId));

			var inserted = 0;
			var updated = 0;
			foreach (var combo in combos)
			{
				if (combo.Name == null)
					continue;

				foreach (var (prefix, cls) in ClassComboPrefixes)
				{
					if (!combo.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
						continue;

					if (existing.TryGetValue((cls, combo.Id), out var row))
					{
						if (row.Level != combo.Level)
						{
							row.Level = combo.Level;
							updated++;
						}
					}
					else
					{
						db.ClassCombos.Add(new ClassCombo
						{
							Class = cls,
							ComboId = combo.Id,
							Level = combo.Level
						});
						inserted++;
					}
				}
			}

			if (inserted > 0 || updated > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureAbilityImporter: derived {Inserted} ClassCombo rows ({Updated} levels updated) from validated catalog prefixes.", inserted, updated);
			return (inserted, updated);
		}

		// rawPackets = ALL captures. Catalogs every ability's Id + Slot/Data identity seen on the wire (the Data quad is
		// also its RDB 1000020 TEA key, so RdbAbilityImporter can name it). The class<->ability ACCESS and per-class
		// unlock LEVEL are NOT set here - they are derived from the per-character grants (DeriveClassAbilitiesAsync).
		public static async Task<(int Seen, int Inserted, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var grants = new Dictionary<uint, (uint Slot, uint D0, uint D1, uint D2, uint D3, uint D4)>();
			foreach (var packet in rawPackets)
				TryReadAddSpecialAbility(packet, grants);

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
				a.Key.Key1 = g.D1;
				a.Key.Key2 = g.D2;
				a.Key.Key3 = g.D3;
				a.Key.Key4 = g.D4;
				if (string.IsNullOrEmpty(a.Name) && KnownNames.TryGetValue(id, out var n))
					a.Name = n;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureAbilityImporter: {Seen} ability grants seen, {Inserted} new, {Updated} updated.", grants.Count, inserted, updated);
			return (grants.Count, inserted, updated);
		}

		// Seeds each captured character's granted ABILITY set (CharacterAbility rows) from the 0x2052 grants, keyed by
		// the grant's RpcHeader receiver instance (= character id) - the ability twin of CaptureSpellImporter's learned-
		// spell seeding. Abilities are NOT in the snapshot known-list, so this is the authoritative per-character set
		// (the action bar is only shortcuts). Only characters already in the DB are touched; idempotent.
		public static async Task<(int Characters, int Inserted)> ImportCharacterAbilitiesAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, false);

			var (characters, inserted) = await SeedPerCharacterAsync(db, byCharacter,
				async cid => (await db.CharacterAbilities.Where(a => a.CharacterId == cid).Select(a => a.AbilityId).ToListAsync()).ToHashSet(), (cid, id) =>
					db.CharacterAbilities.Add(new CharacterAbility
					{
						CharacterId = cid,
						AbilityId = id
					}));

			logger?.LogInformation("CaptureAbilityImporter: per-character ability sets for {C} characters, {I} new CharacterAbility rows.", characters, inserted);
			return (characters, inserted);
		}

		// Seeds each captured character's COMBO book (CharacterCombo rows) from the ComboBookIIR grants (0x2000 carrier,
		// sub-opcode 0xA8BBEB7F), keyed by the RpcHeader receiver instance (= character id). Combos are class/level
		// content the client's combo book gains per level; a max-level imported character never dings, so this persisted
		// set is what GrantCharacterCombos re-emits on login. Only characters already in the DB are touched; idempotent.
		public static async Task<(int Characters, int Inserted)> ImportCharacterCombosAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadCharacterGrant(packet, byCharacter, true);

			var (characters, inserted) = await SeedPerCharacterAsync(db, byCharacter,
				async cid => (await db.CharacterCombos.Where(c => c.CharacterId == cid).Select(c => c.ComboId).ToListAsync()).ToHashSet(), (cid, id) => db.CharacterCombos.Add(new CharacterCombo
				{
					CharacterId = cid,
					ComboId = id
				}));

			logger?.LogInformation("CaptureAbilityImporter: combo books for {C} characters, {I} new CharacterCombo rows.", characters, inserted);
			return (characters, inserted);
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

		// Shared per-character seeding: for every (characterId -> id set), skip characters not in the DB and ids already
		// recorded, add the rest via the supplied factory, save once. Returns (characters touched, rows inserted).
		private static async Task<(int Characters, int Inserted)> SeedPerCharacterAsync(IDatabaseContext db, Dictionary<uint, HashSet<uint>> byCharacter, Func<uint, Task<HashSet<uint>>> loadExisting,
			Action<uint, uint> add)
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

		// 0x2052 body (no length prefix): [slot][id][id][0][1][data0][data1..4][data1..4][constA][constB]x2[1][0][u8].
		private static void TryReadAddSpecialAbility(byte[] packetBytes, IDictionary<uint, (uint, uint, uint, uint, uint, uint)> grants)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != AddSpecialAbilityOpcode)
					return;

				var body = packet.Body.ReadRemainingArray();

				// The 0x2052 grant is exactly what AbilityGrantBody builds/parses (slot, id, data0, key1..4) - use its
				// parser (which validates the full 81-byte shape) instead of re-reading the words by hand.
				if (!AbilityGrantBody.TryParseAddSpecialAbilityBody(body, out var ability, out _) || ability.Id == 0)
					return;

				grants[ability.Id] = (ability.Slot, ability.Data0, ability.Key.Key1, ability.Key.Key2, ability.Key.Key3, ability.Key.Key4);
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
					{
						var v = ReadVarint(s);
						if (field == 3)
							numericValue = (uint)v;
						break;
					}

					case 2:
					{
						// field 4 = token name, field 5 = value string; only field 4 is the command token. (Earlier
						// this captured EVERY length-delimited field, so the trailing field overwrote the token.)
						var str = s.ReadStringByteLength();
						if (field == 4)
							token = str;
						break;
					}

					default:
					{
						return false; // unexpected wire type - not a clean editor callback
					}
				}
			}

			if (token != "ChangeClass")
				return false;

			classId = numericValue / 2;
			return true;
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

					id = ComboBookIirCodec.Message.Read(new FaolanStream(body)).ComboId;
				}
				else
				{
					// The same 0x2052 grant AbilityGrantBody parses; take its validated id.
					if (packet.Opcode != AddSpecialAbilityOpcode || !AbilityGrantBody.TryParseAddSpecialAbilityBody(body, out var ability, out _))
						return;

					id = ability.Id;
				}

				if (id == 0)
					return;

				var characterId = packet.Receiver.Instance;
				(byCharacter.TryGetValue(characterId, out var set)
					? set
					: byCharacter[characterId] = []).Add(id);
			}
			catch
			{
				// not a parseable per-character grant
			}
		}
	}
}
