using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Faolan.Core.Network.Spawn;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Importer.Captures
{
    // Materializes the CAPTURED characters (necrol80, demol80, Strie, ...) as playable DB characters: every
    // player-own FA015807 spawn that parses with the byte-exact scaffold codec yields the character's name,
    // identity (race/sex/level), looks (head/voice/size, stored as CharacterBody in editor 2x units), vitals,
    // position/map, worn equipment (IIR InventoryEntries whose templates are DB items) and the full spawn
    // scaffold rows (PlayerSpawnScaffoldRows.ExtractTo - the same mapping the golden tests use). Characters
    // whose name already exists in the DB are skipped, so re-running the importer never duplicates or
    // overwrites; the biggest captured spawn per name wins (geared late-game spawns carry the most data).
    public static class CaptureCharacterImporter
    {
        private const string ImportAccountName = "captured";
        private const uint CharacterListOpcode = 0x20EF; // PlayerAgent SendCharacterList
        private const uint InventorySnapshotSubOpcode = 0x737A6DF9;
        private const uint MapChangeSubOpcode = 0x5D85BFC7;

        private const uint Opcode2000 = 0x2000;

        // The account imported characters are attached to. 0 = find-or-create the dedicated "captured"
        // account (login validation is stubbed, so logging in as captured/captured works). Imported
        // characters deliberately do NOT join an existing play account: a 20+ entry character list breaks
        // the client's auto-enter flow (observed with the smoke account).
        public static uint AccountId = 0;

        public static async Task<(int Seen, int Added, int Skipped)> ImportAsync(DatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures)
        {
            // Best (largest) parseable player spawn body per character name, with the map active when it
            // spawned and the capture's FullChar snapshot for the same instance (the carried inventory -
            // snapshots are addressed by instance, the spawn provides the name <-> instance link).
            var best = new Dictionary<string, (byte[] Body, uint MapId, uint Handle)>(StringComparer.OrdinalIgnoreCase);
            var charList = new Dictionary<string, CharListEntry>(StringComparer.OrdinalIgnoreCase);
            // Instance -> snapshot bodies, pooled ACROSS captures (instances are globally stable; some
            // snapshots are analyzer-truncated, so the import falls back through the candidates).
            var snapshots = new Dictionary<uint, List<byte[]>>();
            foreach (var capture in captures)
            {
                uint currentMap = 0;
                var spawns = new List<(string Name, byte[] Body, uint MapId, uint Handle)>();
                foreach (var p in capture)
                {
                    if (p is { Opcode: CharacterListOpcode, IsDownload: true })
                    {
                        HarvestCharacterList(p.Bytes, charList);
                        continue;
                    }

                    if (p.Opcode != Opcode2000)
                        continue;

                    var body = GetBody(p.Bytes);
                    if (body.Length < 38)
                        continue;

                    var subop = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4));
                    if (subop == MapChangeSubOpcode)
                    {
                        currentMap = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4));
                        continue;
                    }

                    if (subop == InventorySnapshotSubOpcode)
                    {
                        var instance = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
                        if (!snapshots.TryGetValue(instance, out var list))
                            snapshots[instance] = list = [];
                        list.Add(body);
                        continue;
                    }

                    if (subop != PlayerSpawnScaffoldCodec.SubOpcode)
                        continue;

                    // Only PLAYER spawns parse with the scaffold codec (NPC FA015807 bodies have a different
                    // post-IIR layout). Editor-preview spawns (the fully-constant 228-byte pre-frame) carry a
                    // synthesized "Character<instance>" placeholder name - those are the creator's temp
                    // instances, never importable. Real spawns also include OTHER PLAYERS visible in gameplay
                    // captures; those are filtered out after pooling (only the capturing account's own played
                    // character is imported - see the own-character prune below).
                    if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _) || scaffold.PreFrame is { Length: 228 })
                        continue;
                    if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read) || string.IsNullOrEmpty(read.Message.Name) || IsCreatorTempName(read.Message.Name))
                        continue;

                    spawns.Add((read.Message.Name, body, currentMap, scaffold.Handle));
                }

                foreach (var (name, body, mapId, handle) in spawns)
                {
                    if (!best.TryGetValue(name, out var existing) || body.Length > existing.Body.Length)
                        best[name] = (body, mapId, handle);
                }
            }

            // Keep ONLY the capturing account's own PLAYED character(s); drop other players who merely appeared
            // in a gameplay capture. The own character is identified by being in the account's character list
            // (0x20EF carries only the capturing account's own characters) and/or by the server having sent it a
            // personal FullChar snapshot (0x737A6DF9, only ever delivered for the local player). Other players
            // have neither, so this excludes them. (Pooled across all captures first, so a char listed in one
            // capture but spawned in another still matches.)
            var otherPlayers = 0;
            foreach (var name in best.Keys.ToList())
            {
                if (charList.ContainsKey(name) || snapshots.ContainsKey(best[name].Handle))
                    continue;

                best.Remove(name);
                otherPlayers++;
            }

            if (otherPlayers > 0)
                Console.WriteLine($"Characters: skipped {otherPlayers} other player(s) visible in captures (not the played character).");

            var accountId = AccountId != 0
                ? AccountId
                : await FindOrCreateImportAccount(db);
            var realmId = await FindOrCreateRealm(db);

            var added = 0;
            var skipped = 0;
            foreach (var (name, (body, mapId, handle)) in best.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (await db.Characters.AnyAsync(c => c.Name == name))
                {
                    skipped++;
                    continue;
                }

                PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _);
                PlayerSpawnReader.TryReadCharacterMessage(body, out var read);

                // The class lives in the pre-IIR frame's u16 value island at body offset 135: its LOW byte is
                // the class id (verified 10/10 against the captured character lists, all pre-frame variants;
                // the high byte is an hash group). This covers OTHER players too - the char list (0x20EF)
                // only exists for the capturing account's own characters and serves as the cross-check.
                var classId = charList.GetValueOrDefault(name)?.Class ?? (body.Length > 136
                    ? body[136]
                    : 0u);

                // Skip captures taken BEFORE class selection: their class byte is unset (0/1), not one of the 12
                // real classes. Materializing them creates broken chars (and would violate the Class CHECK constraint).
                if (!Enum.IsDefined((Class)classId))
                {
                    Console.WriteLine($"Characters: skipped '{name}' - invalid/unset class {classId} (captured pre-class-selection).");
                    skipped++;
                    continue;
                }

                await AddCharacter(db, accountId, realmId, name, mapId, classId, scaffold, read.Message, snapshots.GetValueOrDefault(handle), charList.GetValueOrDefault(name));
                added++;
            }

            return (best.Count, added, skipped);
        }

        private static async Task AddCharacter(DatabaseContext db, uint accountId, uint realmId, string name, uint mapId, uint classId, PlayerSpawnScaffold scaffold, ProtoCoreCharacterIIR message, List<byte[]> snapshots,
            CharListEntry listEntry)
        {
            var stats = message.Stats ?? new ProtoCoreCharacterIIR.CharacterStats();

            // The character keeps its ORIGINAL retail id (the spawn handle = the on-wire instance, e.g. Strie
            // 0x02165EFF, demol80 0x110099BD) unless that id is already taken locally - then the DB generates
            // one. Dependents that bake the raw CharacterId (scaffold rows, equipment) are added after the
            // first save settles it.
            var originalId = scaffold.Handle != 0 && !await db.Characters.AnyAsync(c => c.Id == scaffold.Handle)
                ? scaffold.Handle
                : 0;

            var character = new Character
            {
                Id = originalId,
                AccountId = accountId,
                Name = name,
                AuthChallenge = Guid.NewGuid().ToString("N"),
                CreationDate = DateTime.UtcNow,
                Level = (ushort)Math.Max(1, stats.Level),
                Race = (Race)stats.Race,
                Class = (Class)classId,
                // The spawn's sex code is the Sex enum + 2: MALE=2 / FEMALE=3 (anchored on Hoxxling, male,
                // wire 2 + a chosen beardmesh).
                Sex = stats.Sex == 2
                    ? Sex.Male
                    : Sex.Female,
                // The map active when the spawn was sent (MapChange tracking; the transform's "playfield"
                // varint is a session instance id, NOT the map), else the char list's persisted map (when
                // the Map row exists - FK), else the Tortage Beach fallback.
                MapId = await ResolveMapId(db, mapId, listEntry),
                RealmId = realmId
            };

            // The transform's Vec3 payload is a protobuf message (ProtoVec3): tagged fixed32 fields
            // 0D <x> 15 <y> 1D <z>, floats little-endian - NOT 12 raw floats.
            if (scaffold.Vec3 is { Length: >= 15 })
            {
                for (var i = 0; i + 5 <= scaffold.Vec3.Length; i += 5)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(scaffold.Vec3.AsSpan(i + 1, 4));
                    switch (scaffold.Vec3[i])
                    {
                        case 0x0D:
                        {
                            character.PositionX = value;
                            break;
                        }

                        case 0x15:
                        {
                            character.PositionY = value;
                            break;
                        }

                        case 0x1D:
                        {
                            character.PositionZ = value;
                            break;
                        }
                    }
                }
            }

            // The transform's Quat payload is a ProtoQuat: tagged fixed32 fields 0D <x> 15 <y> 1D <z> 25 <w>,
            // floats little-endian (the position Vec3 encoding plus the field-4 W component). Captured here so the
            // player spawn rotation is authored from the DB instead of kept verbatim from the burst.
            if (scaffold.Quat is { Length: >= 20 })
            {
                for (var i = 0; i + 5 <= scaffold.Quat.Length; i += 5)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(scaffold.Quat.AsSpan(i + 1, 4));
                    switch (scaffold.Quat[i])
                    {
                        case 0x0D:
                        {
                            character.RotationX = value;
                            break;
                        }

                        case 0x15:
                        {
                            character.RotationY = value;
                            break;
                        }

                        case 0x1D:
                        {
                            character.RotationZ = value;
                            break;
                        }

                        case 0x25:
                        {
                            character.RotationW = value;
                            break;
                        }
                    }
                }
            }

            // Vitals from the IIR's max side (x100 wire scale); class is not in the spawn, so fall back to the
            // class-default baseline only when the capture carries nothing.
            character.MaxStats = stats.Health > 0
                ? new CharacterMaxStats
                {
                    Health = stats.Health / 100,
                    Stamina = stats.MaxStamina / 100,
                    Mana = stats.MaxMana / 100
                }
                : CharacterMaxStats.CreateDefaults(character.Class, character.Level);

            // The rendered looks, stored in CharacterBody's editor 2x units (the spawn rebuild halves them).
            character.Body = new CharacterBody
            {
                HeadMesh = stats.HeadMesh * 2,
                Voice = (byte)Math.Min(byte.MaxValue, stats.VoiceSet * 2),
                Size = (byte)Math.Min(byte.MaxValue, stats.MonsterScale * 2)
            };

            // First save assigns the DB-generated id; the scaffold rows and equipment bake the raw CharacterId,
            // so they are added after it exists.
            db.Characters.Add(character);
            await db.SaveChangesAsync();

            // The full spawn scaffold (entry table / feat ids / pair lists / pre-frame) - the captured
            // character's appearance + progression scaffold, byte-exact regenerable.
            PlayerSpawnScaffoldRows.ExtractTo(character, scaffold);

            // Worn equipment: every IIR InventoryEntry whose template is a DB item.
            var equipment = new CharacterEquipment { CharacterId = character.Id };
            var worn = 0;
            foreach (var entry in message.InventoryEntries)
            {
                var item = await db.Items.FirstOrDefaultAsync(i => i.Id == entry.Item.Template1);
                if (item == null)
                    continue;

                // entry.Pos is a raw wire slot: the worn slots + vanity map to a column; the bag / out-of-range ids don't.
                var slot = (CharacterEquipmentSlot)entry.Pos;
                if (!Enum.IsDefined(slot))
                    continue;

                equipment[slot] = item;
                worn++;
            }

            db.CharacterEquipment.Add(equipment);

            // Carried inventory (bag/resource/quest containers) from the capture's FullChar snapshot, when one
            // was sent for this character's instance. Items not in the DB are skipped like the worn ones.
            var carried = 0;
            var carriedTotal = 0;
            var knownSpells = 0;
            var statSlots = 0;
            if (snapshots is { Count: > 0 })
            {
                try
                {
                    (carried, carriedTotal) = await ImportCarriedInventory(db, character.Id, snapshots);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Character '{name}': carried-inventory import failed ({e.GetType().Name}: {e.Message}); worn equipment only.");
                }

                // The character's SPELLBOOK is the snapshot's known-spell list (its actual highest-rank spells), NOT
                // the incremental DispatchLearnSpell grants (which only carry a session's re-learns). Scanned straight
                // out of the raw snapshot bytes so it survives a tails-parse failure on a large caster snapshot.
                try
                {
                    knownSpells = await ImportKnownSpells(db, character.Id, snapshots);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Character '{name}': known-spell import failed ({e.GetType().Name}: {e.Message}).");
                }

                // The character's STAT SHEET (level/vitals/attributes/PvP/ratings/...) authored from the snapshot's
                // stat block, replacing the fixed fresh-char skeleton for this character. Scanned from raw bytes.
                try
                {
                    statSlots = await ImportStatBlock(db, character.Id, snapshots);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Character '{name}': stat-block import failed ({e.GetType().Name}: {e.Message}).");
                }
            }

            await db.SaveChangesAsync();

            Console.WriteLine(
                $"Character '{name}': id {character.Id:X8}{(originalId == 0 ? " (generated)" : "")}, level {character.Level} {character.Sex} {character.Race} {character.Class}, map {character.MapId}, {worn}/{message.InventoryEntries.Count} worn, {carried}/{carriedTotal} carried, {knownSpells} spells, {statSlots} stat slots, {character.Attributes.Count} scaffold entries, {character.Skills.Count} skill ids.");
        }

        private static async Task<uint> FindOrCreateImportAccount(DatabaseContext db)
        {
            var existing = await db.Accounts.FirstOrDefaultAsync(a => a.UserName == ImportAccountName);
            if (existing != null)
                return existing.Id;

            var account = new Account
            {
                UserName = ImportAccountName,
                Password = ImportAccountName,
                AuthChallenge = "",
                LastIpAddress = ""
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            Console.WriteLine($"Characters: created import account '{ImportAccountName}' (id {account.Id}).");
            return account.Id;
        }

        // RealmId is required and captures don't carry our local realm ids, so imported characters join the
        // server's (first) realm, creating a default one when the DB has none yet.
        private static async Task<uint> FindOrCreateRealm(DatabaseContext db)
        {
            var realm = await db.Realms.OrderBy(r => r.Id).FirstOrDefaultAsync();
            if (realm == null)
            {
                realm = new Realm
                {
                    Name = "Project Faolan",
                    IpAddress = "127.0.0.1"
                };
                db.Realms.Add(realm);
                await db.SaveChangesAsync();
                Console.WriteLine($"Characters: created default realm '{realm.Name}' (id {realm.Id}).");
            }

            return realm.Id;
        }

        // Returns the packet body INCLUDING its leading u32 length - the scaffold codec's input layout
        // ([u32 bodyLen][u32 FA015807][...]).
        private static byte[] GetBody(byte[] packet)
        {
            if (packet.Length < 16)
                return [];

            var headerLen = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
            var bodyStart = 12 + (int)headerLen;
            return headerLen > packet.Length || bodyStart + 8 > packet.Length
                ? []
                : packet[bodyStart..];
        }

        // Parses a retail PlayerAgent character list (the SendCharacterList layout) into name -> entry;
        // later lists overwrite earlier ones (latest capture state wins).
        private static void HarvestCharacterList(byte[] packet, Dictionary<string, CharListEntry> charList)
        {
            try
            {
                var headerLen = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
                var stream = new FaolanStream(packet[(12 + (int)headerLen)..]);
                stream.ReadUInt32(); // client instance
                var count = (int)(stream.ReadUInt32() / 1009) - 1;
                for (var i = 0; i < count; i++)
                {
                    stream.ReadUInt32(); // character id
                    stream.ReadUInt32(); // player instance
                    stream.ReadUInt32(); // character id again
                    var name = stream.ReadString();
                    stream.ReadUInt32(); // realm
                    stream.ReadUInt32(); // sex
                    stream.ReadString(); // last-connection date
                    stream.ReadUInt32();
                    var mapId = stream.ReadUInt32();
                    var level = stream.ReadUInt32();
                    var @class = stream.ReadUInt32();
                    stream.ReadUInt32();
                    stream.ReadUInt32();
                    stream.ReadUInt32(); // 0x60355308
                    stream.ReadUInt32(); // 2
                    stream.ReadUInt32(); // race
                    stream.ReadString(); // language
                    stream.ReadUInt32();
                    stream.ReadUInt32();
                    stream.ReadString(); // auth challenge
                    stream.ReadUInt32();
                    stream.ReadUInt32();

                    if (!string.IsNullOrEmpty(name))
                        charList[name] = new CharListEntry(@class, level, mapId);
                }
            }
            catch
            {
                // truncated/foreign 0x20EF body - ignore; the class just stays unknown for its characters
            }
        }

        // Parses the snapshot's ProtoFullCharacterIIR root and stores the Bag/Resource/Quest container entries
        // as CharacterInventory rows (Container = the wire kind, Slot = pos, Count = stat 26). The Equipped
        // container is intentionally ignored - the spawn IIR already provided the worn set.
        private static async Task<(int Stored, int Total)> ImportCarriedInventory(DatabaseContext db, uint characterId, IReadOnlyList<byte[]> snapshotBodies)
        {
            // Largest snapshot first; analyzer-truncated ones (the TCP-reassembly gap) fail to split and the
            // next-largest is tried instead.
            ProtoFullCharacterIIR root = null;
            foreach (var snapshotBody in snapshotBodies.OrderByDescending(b => b.Length))
            {
                try
                {
                    var stream = new FaolanStream(snapshotBody);
                    stream.ReadUInt32(); // body length
                    stream.ReadUInt32(); // subop
                    stream.ReadUInt32(); // ack
                    stream.ReadUInt32(); // instance
                    root = FullCharSnapshotReader.ParseInventoryRoot(stream);
                    break;
                }
                catch
                {
                    // truncated - try the next one
                }
            }

            if (root == null)
                return (0, 0);

            var stored = 0;
            var total = 0;
            foreach (var container in root.Inventories)
            {
                if (container.InventoryId.Type == FullCharSnapshotBuilder.EquippedKind)
                    continue;

                foreach (var entry in container.Items)
                {
                    total++;
                    var templateId = entry.Item?.ItemData?.Template1 ?? 0;
                    if (templateId == 0 || !await db.Items.AnyAsync(i => i.Id == templateId))
                        continue;

                    db.CharacterInventory.Add(new CharacterInventory
                    {
                        CharacterId = characterId,
                        Container = (int)container.InventoryId.Type,
                        Slot = (int)entry.Pos,
                        ItemId = templateId,
                        Count = entry.Item.StatLists.FirstOrDefault(s => s.Stat == 26)?.Value ?? 1
                    });
                    stored++;
                }
            }

            return (stored, total);
        }

        // The character's SPELLBOOK from its FullChar snapshot's known-spell list (KnownSpellList): each type-2 record
        // is a learned spell (the char's actual highest-rank set). Scanned from the raw snapshot bytes (no tails
        // deserialize). The largest snapshot that yields a list wins; idempotent (skips already-recorded spells).
        private static async Task<int> ImportKnownSpells(DatabaseContext db, uint characterId, IReadOnlyList<byte[]> snapshotBodies)
        {
            KnownSpellList known = null;
            foreach (var body in snapshotBodies.OrderByDescending(b => b.Length))
            {
                if (KnownSpellList.TryExtractFromSnapshot(body, out known))
                    break;
            }

            if (known == null)
                return 0;

            var existing = (await db.CharacterSpells.Where(s => s.CharacterId == characterId).Select(s => s.SpellId).ToListAsync()).ToHashSet();
            var added = 0;
            foreach (var id in known.Records.Where(r => r.Type == KnownSpellList.SpellType).Select(r => r.Id).Distinct())
            {
                if (!existing.Add(id))
                    continue;

                db.CharacterSpells.Add(new CharacterSpell
                {
                    CharacterId = characterId,
                    SpellId = id
                });
                added++;
            }

            if (added > 0)
                await db.SaveChangesAsync();
            return added;
        }

        // The character's STAT SHEET (CharacterStat rows) from its FullChar snapshot stat block: the 17 displayed stats
        // then the 74 sheet stats, in wire order (Ordinal), so the snapshot generator authors them PER-CHARACTER
        // instead of the fixed fresh skeleton. The snapshot is the whole sheet, so it replaces any existing rows.
        private static async Task<int> ImportStatBlock(DatabaseContext db, uint characterId, IReadOnlyList<byte[]> snapshotBodies)
        {
            List<(uint Id, uint Value)> pairs = null;
            foreach (var body in snapshotBodies.OrderByDescending(b => b.Length))
            {
                pairs = SubClassStatBlock.ExtractFromSnapshot(body);
                if (pairs != null)
                    break;
            }

            if (pairs == null)
                return 0;

            var old = await db.CharacterStats.Where(s => s.CharacterId == characterId).ToListAsync();
            if (old.Count > 0)
            {
                db.CharacterStats.RemoveRange(old);
                await db.SaveChangesAsync();
            }

            for (var i = 0; i < pairs.Count; i++)
            {
                db.CharacterStats.Add(new CharacterStat
                {
                    CharacterId = characterId,
                    Ordinal = i,
                    StatId = (CharacterStatId)pairs[i].Id,
                    Value = pairs[i].Value
                });
            }

            await db.SaveChangesAsync();
            return pairs.Count;
        }

        // The creator names its temp instances "Character<instance>" until the real name is committed.
        private static bool IsCreatorTempName(string name)
        {
            return name.Length > 9 && name.StartsWith("Character", StringComparison.Ordinal) && name.Skip(9).All(char.IsAsciiDigit);
        }

        private static async Task<uint> ResolveMapId(DatabaseContext db, uint spawnMapId, CharListEntry listEntry)
        {
            foreach (var candidate in new[]
            {
                spawnMapId,
                listEntry?.MapId ?? 0
            })
            {
                if (candidate != 0 && await db.Maps.AnyAsync(m => m.Id == candidate))
                    return candidate;
            }

            // MapId is required: fall back to Tortage Beach (the universal fallback world), making sure
            // its Map row exists so the foreign key can never fail.
            if (!await db.Maps.AnyAsync(m => m.Id == Map.TortageBeachId))
            {
                db.Maps.Add(new Map
                {
                    Id = Map.TortageBeachId,
                    Name = "Tortage Beach"
                });
                await db.SaveChangesAsync();
            }

            return Map.TortageBeachId;
        }

        // One retail character-list entry (PlayerAgent 0x20EF): the only wire source for the CLASS, and the
        // character's persisted map (vs the spawn-time MapChange).
        private sealed record CharListEntry(uint Class, uint Level, uint MapId);
    }
}
