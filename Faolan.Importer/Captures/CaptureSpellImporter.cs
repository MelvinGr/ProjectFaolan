using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Harvests spell ids the character actually cast, from ApplySpell packets (0x2000 / 0x6B87E316; spell id at
	// inner[12..16]) and catalogs any not already in the Spell table (id-only; names/params are client-side / from
	// the archive `spelldata`). This is the thin capture-side spell signal - the full spellbook is class/level
	// derived and not in the pcaps (see docs/tooling/importer.md). Takes raw on-wire packet bytes.
	public static class CaptureSpellImporter
	{
		// How many packets after its ding a grant may still be attributed to that level (#376). Real ones land in the
		// SAME packet or ~3-4 later; the window keeps an unrelated later grant (a quest/claim reward) off the last ding.
		private const int GrantWindow = 40;
		private const uint ApplySpellSubOpcode = (uint)GameServer0X2000Opcodes.MagicBuffIIR;
		private const uint GameCharAgentType = 0xA9ECB95D;
		private const uint GameCharInterfaceType = 0x8B87F791;
		private const uint MaxLevel = 80;
		private const uint WorldOpcode = 0x2000;

		// MoveItemToBar / AddShortcut. The client's first one ends the create starting-kit phase (its bar drags
		// follow the grants); any DispatchLearnSpell after it is in-session leveling, not a starter.
		private const ushort AddShortcutOpcode = 0x206E;

		// The DispatchLearnSpell (s2c 0x200A) grant: sender = GameCharAgent, receiver = GameCharInterface (the
		// character), body = [u32 spellId] (#73). The class pair disambiguates the 0x200A opcode collisions on other
		// conversations (CharacterSearch etc.); the receiver-instance == character-id is the s2c-direction filter.
		private const ushort LearnSpellOpcode = 0x200A;

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

			// CharacterSpell.SpellId is a FK into Spells. A learned spell is real and resolves in-client by RDB id
			// whether or not our (incomplete) Spell catalog has a row for it - so a learned id with no catalog row is a
			// gap in Spells, not a fake spell (cf. CaptureCharacterImporter.ImportKnownSpells, the spellbook path).
			// Skip it (logged) rather than fabricate an empty-name stub or FK-fault; the gap is closed by completing the
			// Spell catalog, not here.
			var learnedIds = byCharacter.Values.SelectMany(s => s).ToHashSet();
			var cataloged = (await db.Spells.Where(s => learnedIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
			var uncataloged = learnedIds.Where(id => !cataloged.Contains(id)).ToList();
			if (uncataloged.Count > 0)
				logger?.LogWarning("CaptureSpellImporter: {Count} learned spell ids not in the Spell catalog, skipped: {Ids}. (complete the Spell catalog to seed them)", uncataloged.Count,
					string.Join(", ", uncataloged));

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
					if (!cataloged.Contains(spellId))
						continue; // uncataloged (warned above) - skip so the FK holds

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

		// A create-capture DispatchLearnSpell grant id (s2c 0x200A, GameCharAgent -> GameCharInterface, [u32 spellId]).
		// #376: the (class, spell, LEVEL) map, from capture truth. A NewLevelIIR (0x6D314743) carries the level just
		// reached; every 0x200A grant that follows it in the same capture is a grant AT that level. That is a direct
		// observation of what retail hands out and when - unlike the ellatha/wiki scrape, it cannot be wrong about it.
		//
		// The class comes from the PLAYER's own spawn: body[136] on the pre-IIR frame is the class byte
		// (CaptureCharacterImporter, "verified 10/10"), and a player's spawn handle == its character id
		// (player_handle_equals_character_id), so the spawn whose handle matches the ding's receiver instance IS the
		// dinging character. Matching on the handle matters: NPC spawns carry a class byte too, and it is usually a
		// VALID class enum (Kalanthes reads 24, Pillager 20), so "any spawn with a plausible class" would attribute a
		// player's grants to whatever NPC was streamed first. This also works on LOGIN captures, where
		// CaptureAbilityImporter.DetectClass cannot help (there is no create-editor callback) and where the character
		// is usually absent from our own Characters table (Hoxxling, Blablatest, ... were never imported).
		//
		// Only grants AFTER a ding are attributed, which is what excludes the login re-grant burst (#74 re-grants the
		// whole spellbook on every login - 201 such grants across the capture set, none of them level-attributable).
		// GrantWindow additionally bounds how far a grant may sit from its ding: observed real ones land in the SAME
		// packet or ~3-4 later, so a tight window keeps an unrelated later grant (a quest/claim reward) from being
		// blamed on the last ding. Replace-all is deliberately NOT used - a capture that never dings must not retract
		// what another capture proved.
		public static async Task<(int Access, int Levels)> MarkLevelGrantsAsync(IDatabaseContext db, IReadOnlyList<NamedCapture> captures, ILogger? logger = null)
		{
			var proven = new Dictionary<(uint Class, uint SpellId), byte>();
			foreach (var capture in captures)
				CollectLevelGrants(capture.Packets, proven);

			if (proven.Count == 0)
				return (0, 0);

			var known = (await db.Spells.Select(s => s.Id).ToListAsync()).ToHashSet();
			var existing = (await db.ClassSpells.ToListAsync()).ToDictionary(c => ((uint)c.Class, c.SpellId));
			var access = 0;
			var levels = 0;

			foreach (var ((cls, spellId), level) in proven)
			{
				if (!Enum.IsDefined((Class)cls) || !known.Contains(spellId))
					continue; // an uncataloged grant id is a catalog gap (learned_spells_not_subset_of_catalog), not a fake spell - skip rather than FK-fault

				if (!existing.TryGetValue((cls, spellId), out var row))
				{
					row = new ClassSpell
					{
						Class = (Class)cls,
						SpellId = spellId
					};
					db.ClassSpells.Add(row);
					existing[(cls, spellId)] = row;
					access++;
				}

				// Lowest wins: a spell seen granted at 3 and again at 5 unlocks at 3 (the later sighting is a re-grant
				// or a higher rank's row), mirroring CaptureAbilityImporter's min-level rule.
				if (row.GrantLevel is { } have && have <= level)
					continue;

				row.GrantLevel = level;
				levels++;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureSpellImporter: {Levels} capture-proven grant levels across {Pairs} (class, spell) pairs, {Access} new access rows.", levels, proven.Count, access);
			return (access, levels);
		}

		// Marks each captured class's create-granted spells as its STARTER KIT (ClassSpell.IsStarter), from the 0x200A
		// grants a create capture actually shows. So a level-1 create yields EXACTLY the capture's grant
		// (necro = Ice Strike R1 + Frozen Hatred).
		//
		// This used to write LEVELS instead: it pinned every granted spell to Level 1 and pushed every other level-1
		// access row of that class to 2, so that the level-gated "Level <= charLevel" starter query happened to return
		// the captured set. That conflated two independent facts and corrupted a correct one - retail grants the necro
		// Ice Strike (Rank 1) at create even though its requirement really is level 7 (the RDB and ellatha AGREE on 7;
		// the old comment here blamed an ellatha mis-tag, but the client says the same). The starter kit is a fixed
		// LIST, not a level query, so it is now stated outright and Spells.Level is left to the RDB, which leads on
		// level data (verified 2026-07-16). Idempotent; only classes whose create capture actually reached a grant are
		// touched, and it no longer has to run after the level modules.
		public static async Task<(int Starters, int Cleared)> MarkStarterSpellsAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<byte[]>> createCaptures, ILogger? logger = null)
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

			if (startersByClass.Count == 0)
				return (0, 0);

			// Replace-all per captured class: a spell this class's create capture granted IS a starter, anything else
			// on that class is not (so a re-import after a better capture can retract a stale flag).
			var starters = 0;
			var cleared = 0;
			foreach (var (cls, ids) in startersByClass)
			{
				var rows = await db.ClassSpells.Where(cs => (uint)cs.Class == cls).ToListAsync();
				foreach (var row in rows)
				{
					var isStarter = ids.Contains(row.SpellId);
					if (row.IsStarter == isStarter)
						continue;

					row.IsStarter = isStarter;
					if (isStarter)
						starters++;
					else
						cleared++;
				}
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureSpellImporter: marked {Starters} create-granted starter spells ({Cleared} stale flags cleared) across {Classes} captured classes.", starters, cleared,
				startersByClass.Count);
			return (starters, cleared);
		}

		// Walks ONE capture in packet order, tracking each character's level from its NewLevelIIR dings and
		// attributing the grants that follow. See MarkLevelGrantsAsync for why the handle match is required.
		private static void CollectLevelGrants(IReadOnlyList<CapturedPacket> packets, IDictionary<(uint, uint), byte> proven)
		{
			var classByHandle = new Dictionary<uint, byte>();
			foreach (var p in packets)
			{
				if (p.Opcode != WorldOpcode)
					continue;

				// Only PLAYER spawns parse with the scaffold codec - an NPC's FA015807 body has a different shape - so
				// this filters the NPCs out for free. That matters: an NPC spawn's body[136] is usually a VALID class
				// enum too (Kalanthes reads 24, Pillager 20), so a looser filter would hand a player's grants to
				// whichever NPC streamed first.
				var body = PacketBody(p.Bytes);
				if (body.Length < 138 || !PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _) || scaffold.PreFrame is { Length: 228 })
					continue;

				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read) || string.IsNullOrEmpty(read.Message.Name))
					continue;

				classByHandle[scaffold.Handle] = body[136];
			}

			// instance -> the level its last ding announced, and which packet that ding was
			var levelByInstance = new Dictionary<uint, (byte Level, int At)>();
			for (var i = 0; i < packets.Count; i++)
			{
				var p = packets[i];
				if (TryReadNewLevel(p.Bytes, out var dingInstance, out var newLevel))
				{
					levelByInstance[dingInstance] = (newLevel, i);
					continue;
				}

				if (p.Opcode != LearnSpellOpcode)
					continue;

				var ids = new HashSet<uint>();
				TryReadLearnSpellId(p.Bytes, ids);
				if (ids.Count == 0 || !TryReadReceiverInstance(p.Bytes, out var instance))
					continue;

				if (!levelByInstance.TryGetValue(instance, out var ding) || i - ding.At > GrantWindow)
					continue; // no ding yet (a create kit or the login re-grant burst), or too far from one to blame on it

				if (!classByHandle.TryGetValue(instance, out var cls) || !Enum.IsDefined((Class)cls))
					continue; // never saw this character's own spawn, so its class is unknown

				foreach (var spellId in ids)
				{
					if (!proven.TryGetValue((cls, spellId), out var have) || ding.Level < have)
						proven[(cls, spellId)] = ding.Level;
				}
			}
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

		private static byte[] PacketBody(byte[] packet)
		{
			if (packet.Length < 16)
				return [];

			var headerLen = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + (int)headerLen;
			return bodyStart >= packet.Length
				? []
				: packet[bodyStart..];
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

		// NewLevelIIR_t on the world conversation: [u32 len][u32 6D314743][u32 0000C350][u32 instance][u8 flag]
		// [u32 newLevel][u32 x6] - see NewLevelIirCodec.
		private static bool TryReadNewLevel(byte[] packetBytes, out uint instance, out byte newLevel)
		{
			instance = 0;
			newLevel = 0;
			var body = PacketBody(packetBytes);
			for (var i = 0; i + 25 <= body.Length; i++)
			{
				if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i, 4)) != NewLevelIirCodec.Opcode)
					continue;

				instance = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 8, 4));
				var level = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i + 13, 4));
				if (level == 0 || level > MaxLevel)
					return false;

				newLevel = (byte)level;
				return true;
			}

			return false;
		}

		private static bool TryReadReceiverInstance(byte[] packetBytes, out uint instance)
		{
			instance = 0;
			try
			{
				var packet = new ConanPacket(packetBytes);
				instance = packet.Receiver.Instance;
				return instance != 0;
			}
			catch
			{
				return false;
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
