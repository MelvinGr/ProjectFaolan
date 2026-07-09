using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests spell ids the character actually cast, from ApplySpell packets (0x2000 / 0x6B87E316; spell id at
	// inner[12..16]) and catalogs any not already in the Spell table (id-only; names/params are client-side / from
	// the archive `spelldata`). This is the thin capture-side spell signal - the full spellbook is class/level
	// derived and not in the pcaps (see docs/tooling/importer-plan.md). Takes raw on-wire packet bytes.
	public static class CaptureSpellImporter
	{
		private const uint ApplySpellSubOpcode = 0x6B87E316;
		private const uint GameCharAgentType = 0xA9ECB95D;
		private const uint GameCharInterfaceType = 0x8B87F791;

		// MoveItemToBar / AddShortcut. The client's first one ends the create starting-kit phase (its bar drags
		// follow the grants); any DispatchLearnSpell after it is in-session leveling, not a starter.
		private const ushort AddShortcutOpcode = 0x206E;

		// The DispatchLearnSpell (s2c 0x200A) grant: sender = GameCharAgent, receiver = GameCharInterface (the
		// character), body = [u32 spellId] (#73). The class pair disambiguates the 0x200A opcode collisions on other
		// conversations (CharacterSearch etc.); the receiver-instance == character-id is the s2c-direction filter.
		private const ushort LearnSpellOpcode = 0x200A;

		// Corrects each captured class's spell unlock LEVELS from the create grants (the level-1 truth). The ellatha
		// scrape (EllathaSpellLevelImporter) tags many ranks with wrong levels - e.g. Ice Strike (Rank 1) at 7, or
		// several non-granted ranks at 1 - so a naive "Level <= charLevel" starter query over-/under-grants a fresh
		// caster. Rule: every spell a create capture GRANTS (0x200A) is level 1; every OTHER access row of that same
		// captured class currently at level 1 is pushed above it. So a level-1 create yields EXACTLY the capture's
		// grant (necro = Ice Strike R1 + Frozen Hatred). Must run AFTER the ellatha/wiki level modules (it overrides
		// them). Idempotent; only classes whose create capture actually reached a grant are touched.
		public static async Task<(int Pinned, int Bumped)> CorrectStarterLevelsAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> createCaptures, ILogger? logger = null)
		{
			var startersByClass = new Dictionary<uint, HashSet<uint>>();
			foreach (var capture in createCaptures)
			{
				if (CaptureAbilityImporter.DetectClass(capture) is not { } cls)
					continue;

				var ids = new HashSet<uint>();
				foreach (var packet in capture)
				{
					if (IsBarDrag(packet))
						break; // create starters are granted BEFORE the client's first bar drag; later 0x200A = leveling

					TryReadLearnSpellId(packet, ids);
				}

				if (ids.Count == 0)
					continue; // no grant reached in this capture - leave the class's levels untouched

				if (!startersByClass.TryGetValue(cls, out var set))
					startersByClass[cls] = set = [];
				set.UnionWith(ids);
			}

			var allStarters = startersByClass.Values.SelectMany(s => s).ToHashSet();
			if (allStarters.Count == 0)
				return (0, 0);

			// 1. Create-granted spells are the level-1 truth.
			var starterRows = await db.Spells.Where(s => allStarters.Contains(s.Id)).ToListAsync();
			foreach (var s in starterRows)
				s.Level = 1;

			// 2. Every OTHER level-1 access row of a captured class is not actually a level-1 spell - push it above 1
			//    (the ellatha scrape mis-tags non-granted ranks). A level-1 create then yields exactly the grant.
			var bumped = 0;
			foreach (var (cls, starters) in startersByClass)
			{
				var classSpellIds = await db.ClassSpells.Where(cs => (uint)cs.Class == cls).Select(cs => cs.SpellId).ToListAsync();
				var l1 = await db.Spells.Where(sp => classSpellIds.Contains(sp.Id) && sp.Level == 1).ToListAsync();
				foreach (var sp in l1)
				{
					if (starters.Contains(sp.Id))
						continue;

					sp.Level = 2;
					bumped++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureSpellImporter: pinned {Pinned} starter spells to level 1, bumped {Bumped} non-starter level-1 rows above it.", starterRows.Count, bumped);
			return (starterRows.Count, bumped);
		}

		public static async Task<(int Seen, int Inserted)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var ids = new HashSet<uint>();
			foreach (var packet in rawPackets)
				TryReadSpellId(packet, ids);

			if (ids.Count == 0)
				return (0, 0);

			var idList = ids.ToList();
			var existing = (await db.Spells.Where(s => idList.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();

			var inserted = 0;
			foreach (var id in ids)
			{
				if (existing.Contains(id))
					continue;

				db.Spells.Add(new Spell
				{
					Id = id,
					Name = string.Empty
				});
				inserted++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureSpellImporter: {Seen} cast spell ids seen, {Inserted} new cataloged.", ids.Count, inserted);
			return (ids.Count, inserted);
		}

		// Harvests each CLASS's spell ACCESS from the create-flow captures into ClassSpell (#74) - the spell analogue of
		// CaptureAbilityImporter's class-aware ability extraction. A freshly-created caster receives its level-1 spells
		// via DispatchLearnSpell (0x200A) on the beach (Necromancer 0x125D9B/0x42CA77, Herald of Xotli 0x3006FF;
		// physical classes get none, correctly). Each create capture is attributed to its class via the same ChangeClass
		// editor command CaptureAbilityImporter uses (value/2). A spell granted under >= 2 distinct classes is GENERIC
		// (Class 0); under exactly one it is class-specific. Upserts on (Class, SpellId), so it is safe to re-run. This
		// is pure membership (the spell's unlock LEVEL lives on Spells.Level); coverage is only the classes present in
		// the captures - a class-aware ellatha scrape fills the full per-level progression.
		public static async Task<(int Spells, int Inserted)> ImportClassSpellsAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> createCaptures, ILogger? logger = null)
		{
			// spellId -> the distinct classes whose create capture granted it (null class = unattributed capture).
			var idToClasses = new Dictionary<uint, HashSet<uint>>();
			foreach (var capture in createCaptures)
			{
				var cls = CaptureAbilityImporter.DetectClass(capture);
				var perCapture = new HashSet<uint>();
				foreach (var packet in capture)
					TryReadLearnSpellId(packet, perCapture);

				foreach (var id in perCapture)
				{
					var set = idToClasses.TryGetValue(id, out var s)
						? s
						: idToClasses[id] = [];
					if (cls is { } c)
						set.Add(c);
				}
			}

			if (idToClasses.Count == 0)
				return (0, 0);

			// Ensure the Spell catalog has a row for every starting spell id (the 0x200A learn grants are NOT cataloged
			// by ImportAsync, which only sees ApplySpell casts) - ClassSpell.SpellId is a FK into Spells. Save the
			// catalog rows FIRST so the FK target exists before the ClassSpell inserts (the dependent only
			// carries the scalar SpellId, so EF won't order the principal ahead of it on its own).
			var cataloged = (await db.Spells.Select(s => s.Id).ToListAsync()).ToHashSet();
			var newCatalog = false;
			foreach (var id in idToClasses.Keys)
			{
				if (cataloged.Add(id))
				{
					db.Spells.Add(new Spell
					{
						Id = id,
						Name = string.Empty
					});
					newCatalog = true;
				}
			}

			if (newCatalog)
				await db.SaveChangesAsync();

			var existing = await db.ClassSpells.ToDictionaryAsync(s => ((uint)s.Class, s.SpellId));
			var inserted = 0;
			foreach (var id in idToClasses.Keys)
			{
				var cls = ClassFor(id);
				if (existing.ContainsKey((cls, id)))
					continue;

				db.ClassSpells.Add(new ClassSpell
				{
					Class = (Class)cls,
					SpellId = id
				});
				inserted++;
			}

			// Saves the new catalog rows (Spells) and the new ClassSpell rows together; EF orders the inserts so the
			// FK target exists first.
			await db.SaveChangesAsync();

			logger?.LogInformation("CaptureSpellImporter: {Spells} class spells harvested ({Inserted} new ClassSpell rows).", idToClasses.Count, inserted);
			return (idToClasses.Count, inserted);

			// A spell seen under exactly one class is specific to it; under >= 2 (or none we could attribute) it is generic.
			uint ClassFor(uint id)
			{
				return idToClasses[id].Count == 1
					? idToClasses[id].Single()
					: 0u;
			}
		}

		// Seeds each captured character's LEARNED spell set (CharacterSpell rows) from the DispatchLearnSpell grants
		// in the captures, so GrantCharacterSpells re-grants them on login (#74). Retail re-sends a caster's whole
		// learned set on every world entry, so a capture's grants ARE that character's spellbook. Attributed to the
		// character via the grant's RpcHeader receiver instance (= character id). Only characters already in the DB
		// are touched (CaptureCharacterImporter materializes them first); idempotent (skips spells already recorded).
		public static async Task<(int Characters, int Inserted)> ImportLearnedSpellsAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, HashSet<uint>>();
			foreach (var packet in rawPackets)
				TryReadLearnSpell(packet, byCharacter);

			if (byCharacter.Count == 0)
				return (0, 0);

			var charactersSeeded = 0;
			var inserted = 0;
			foreach (var (characterId, spellIds) in byCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var existing = (await db.CharacterSpells.Where(s => s.CharacterId == characterId).Select(s => s.SpellId).ToListAsync()).ToHashSet();

				var added = false;
				foreach (var spellId in spellIds)
				{
					if (!existing.Add(spellId))
						continue;

					db.CharacterSpells.Add(new CharacterSpell
					{
						CharacterId = characterId,
						SpellId = spellId
					});
					inserted++;
					added = true;
				}

				if (added)
					charactersSeeded++;
			}

			if (inserted > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureSpellImporter: learned-spell sets for {Characters} characters, {Inserted} new CharacterSpell rows.", charactersSeeded, inserted);
			return (charactersSeeded, inserted);
		}

		// True if the packet is the client's MoveItemToBar (0x206E) - the create starting-kit boundary.
		private static bool IsBarDrag(byte[] packetBytes)
		{
			try
			{
				return new ConanPacket(packetBytes).Opcode == AddShortcutOpcode;
			}
			catch
			{
				return false;
			}
		}

		// A DispatchLearnSpell grant: opcode 0x200A, GameCharAgent -> GameCharInterface, 4-byte [u32 spellId] body.
		// The c2s direction has the classes swapped, so the sender/receiver class pair selects only the s2c grant.
		private static void TryReadLearnSpell(byte[] packetBytes, IDictionary<uint, HashSet<uint>> byCharacter)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != LearnSpellOpcode || packet.Sender.Type != GameCharAgentType || packet.Receiver.Type != GameCharInterfaceType)
					return;

				var body = packet.Body.ReadRemainingArray();
				if (body.Length != 4)
					return;

				var spellId = BinaryPrimitives.ReadUInt32BigEndian(body);
				if (spellId == 0)
					return;

				var characterId = packet.Receiver.Instance;
				(byCharacter.TryGetValue(characterId, out var set)
					? set
					: byCharacter[characterId] = []).Add(spellId);
			}
			catch
			{
				// not a parseable DispatchLearnSpell grant
			}
		}

		// A create-capture DispatchLearnSpell grant id (s2c 0x200A, GameCharAgent -> GameCharInterface, [u32 spellId]).
		private static void TryReadLearnSpellId(byte[] packetBytes, ISet<uint> ids)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != LearnSpellOpcode || packet.Sender.Type != GameCharAgentType || packet.Receiver.Type != GameCharInterfaceType)
					return;

				var body = packet.Body.ReadRemainingArray();
				if (body.Length != 4)
					return;

				var spellId = BinaryPrimitives.ReadUInt32BigEndian(body);
				if (spellId != 0)
					ids.Add(spellId);
			}
			catch
			{
				// not a parseable DispatchLearnSpell grant
			}
		}

		private static void TryReadSpellId(byte[] packetBytes, ISet<uint> ids)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.Body.ReadArrayUInt32Length();
				// inner: [subop4][c350 4][casterHandle4][flag byte][spellId4] -> spell id at offset 13.
				if (inner.Length < 17 || BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(0, 4)) != ApplySpellSubOpcode)
					return;

				var spellId = BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(13, 4));
				if (spellId != 0)
					ids.Add(spellId);
			}
			catch
			{
				// not a parseable ApplySpell packet
			}
		}
	}
}
