using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Materializes the captured characters as playable DB characters.
	/// </summary>
	/// <remarks>
	///     Every player-own FA015807 spawn that parses with the byte-exact scaffold codec yields the character's name,
	///     identity (race/sex/level), looks (head/voice/size, stored as CharacterBody), position/map, worn equipment
	///     (IIR InventoryEntries whose templates are DB items) and the full spawn scaffold rows
	///     (PlayerSpawnScaffoldRows.ExtractTo - the same mapping the golden tests use). Characters whose name already
	///     exists in the DB are skipped, so re-running the importer never duplicates or overwrites; the biggest
	///     captured spawn per name wins (geared late-game spawns carry the most data).
	/// </remarks>
	public static class CaptureCharacterImporter
	{
		// The account imported characters (and the claims module's AccountClaim rows) attach to. The one definition;
		// ClaimsModule reads it from here.
		internal const string ImportAccountName = "faolan";
		// The s2c character list (sender `PlayerAgent`, receiver `PlayerInterface`, registration hash 0xC414C5EF).
		// Compared in wire space because that is the space the dump records in; on the hash-registered pair the
		// rendering keeps only 7 of 32 bits and some pairs collide at that width, but 0x20EF is unique across both
		// enums, so this filter is exact.
		private static readonly uint CharacterListWireOpcode = CapturedPacket.Wire(PlayerInterfaceMethods.UpdateClientPlayerData);
		private const uint InventorySnapshotSubOpcode = FullPlayerCharacterCodec.Opcode;
		private const uint MapChangeSubOpcode = PlayfieldFCodec.Opcode;


		// The account imported characters are attached to. 0 = find-or-create ImportAccountName, which is the normal
		// play account, so the reference characters are reachable from the same login as everything else. Login
		// validation is stubbed, so any password works. A long character list does not break the client's auto-enter
		// flow, so no separate account is needed.
		public static uint AccountId = 0;

		/// <summary>
		///     The class of the character this capture was played on, or null when the capture never shows one (a
		///     create-editor dump before class selection, or a trace that carries no player spawn).
		/// </summary>
		/// <remarks>
		///     The local player is the one the server sends a personal FullChar snapshot for (FullPlayerCharacterIIR is
		///     only ever delivered for your own character - the same test the own-character prune in
		///     <see cref="ImportAsync" /> uses), which gives its instance; the matching FA015807 player spawn then yields
		///     the class the same way ImportAsync reads it, from the pre-IIR value island's low byte at body offset 136.
		///     Used by the class-specific map import (Map.ClassSpecific): a capture of a class armory is evidence about
		///     one class's copy of it, so its spawns are stamped with this.
		/// </remarks>
		public static ConanClass? DetectPlayedClass(IReadOnlyList<CapturedPacket> capture)
		{
			var localInstances = new HashSet<uint>();
			var classByHandle = new Dictionary<uint, uint>();

			foreach (var p in capture)
			{
				if (!p.IsIirCarrier)
					continue;

				var body = GetScaffoldBody(p);
				if (body.Length < 38)
					continue;

				var subop = body.ReadU32Be(4);
				if (subop == InventorySnapshotSubOpcode)
				{
					localInstances.Add(body.ReadU32Be(12));
					continue;
				}

				if (subop != SimpleCharFullUpdateCodec.Opcode || body.Length <= 136)
					continue;

				// Editor-preview spawns (empty sublist) carry no real class byte - the same frame ImportAsync rejects.
				if (!SpawnBodyReader.TryParse(body, out var scaffold) || !SpawnBodyReader.IsPlayer(scaffold) || scaffold.PreIir.Sublist.Count == 0)
					continue;

				classByHandle[scaffold.Handle] = body[136];
			}

			foreach (var instance in localInstances)
			{
				if (classByHandle.TryGetValue(instance, out var classId) && Enum.IsDefined((ConanClass)classId))
					return (ConanClass)classId;
			}

			return null;
		}

		/// <param name="minLevel">
		///     0 = no filter; captured characters at or below this level are skipped as fresh/tutorial characters (no
		///     trained feats, base ability scaffold). The module sets the reference-set policy; direct callers and tests
		///     default to importing every level.
		/// </param>
		/// <param name="refresh">
		///     The overwrite mode: an existing character the importer itself built (its row carries
		///     `ImportedFromCapture`) is deleted and rebuilt from the current corpus, so a re-dump can update a frozen
		///     bag/worn set/stat sheet. Off by default, because it discards any rearrangement the player has made to that
		///     character.
		/// </param>
		public static async Task<(int Seen, int Added, int Skipped, int Refreshed)> ImportAsync(DatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures, uint minLevel = 0,
			bool refresh = false)
		{
			// Best (largest) parseable player spawn body per character name, with the map active when it
			// spawned and the capture's FullChar snapshot for the same instance (the carried inventory -
			// snapshots are addressed by instance, the spawn provides the name <-> instance link).
			var best = new Dictionary<string, (byte[] Body, uint MapId, uint Handle)>(StringComparer.OrdinalIgnoreCase);
			var charList = new Dictionary<string, CharListEntry>(StringComparer.OrdinalIgnoreCase);
			// Instance -> snapshot bodies, pooled across captures (instances are globally stable; some
			// snapshots are truncated, so the import falls back through the candidates).
			var snapshots = new Dictionary<uint, List<byte[]>>();
			foreach (var capture in captures)
			{
				uint currentMap = 0;
				var spawns = new List<(string Name, byte[] Body, uint MapId, uint Handle)>();
				foreach (var p in capture)
				{
					if (p.IsDownload && p.WireOpcode == CharacterListWireOpcode)
					{
						HarvestCharacterList(p.Bytes, charList);
						continue;
					}

					if (!p.IsIirCarrier)
						continue;

					var body = GetScaffoldBody(p);
					if (body.Length < 38)
						continue;

					var subop = body.ReadU32Be(4);
					if (subop == MapChangeSubOpcode)
					{
						// body is [u32 len][subop][PlayfieldFiir]; PlayfieldFCodec parses [subop][body], so skip the length.
						try
						{
							currentMap = PlayfieldFCodec.Parse(body.AsSpan(4)).MapId;
						}
						catch (CodecParseException)
						{
							// not a full PlayfieldFIIR body - leave the current map alone rather than guessing
						}

						continue;
					}

					if (subop == InventorySnapshotSubOpcode)
					{
						var instance = body.ReadU32Be(12);
						if (!snapshots.TryGetValue(instance, out var list))
							snapshots[instance] = list = [];
						list.Add(body);
						continue;
					}

					if (subop != SimpleCharFullUpdateCodec.Opcode)
						continue;

					// Only player spawns parse with the scaffold codec (NPC FA015807 bodies have a different
					// post-IIR layout). Editor-preview spawns (the only frame with an empty sublist) carry a
					// synthesized "Character<instance>" placeholder name - those are the creator's temp instances,
					// never importable. Real spawns also include other players visible in gameplay captures; those
					// are filtered out after pooling (only the capturing account's own played character is imported -
					// see the own-character prune below).
					if (!SpawnBodyReader.TryParse(body, out var scaffold) || !SpawnBodyReader.IsPlayer(scaffold) || scaffold.PreIir.Sublist.Count == 0)
						continue;
					if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read) || string.IsNullOrEmpty(read.Message.Name) || IsCreatorTempName(read.Message.Name))
						continue;

					spawns.Add((read.Message.Name, body, currentMap, scaffold.Handle));
				}

				foreach (var (name, body, mapId, handle) in spawns)
				{
					if (!best.TryGetValue(name, out var existing) || body.Length > existing.Body.Length)
						best[name] = (body, mapId, handle);
				}
			}

			// Keep only the capturing account's own played character(s); drop other players who merely appeared
			// in a gameplay capture. The own character is identified by being in the account's character list
			// (0x20EF carries only the capturing account's own characters) and/or by the server having sent it a
			// personal FullChar snapshot (only ever delivered for the local player). Other players have neither,
			// so this excludes them. Pooled across all captures first, so a character listed in one capture but
			// spawned in another still matches.
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
			var refreshed = 0;
			foreach (var (name, (body, mapId, handle)) in best.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
			{
				// An existing row is left alone unless this is a refresh and the row is one the importer built. The
				// discriminator is the row's own `ImportedFromCapture` marker, which only this importer ever sets: a
				// character created in-game cannot carry it, so it is exempt by construction. Neither the name (this
				// loop only iterates names that are in a capture) nor the account (imported characters share the
				// normal play account) can tell the two apart.
				var existing = await db.Characters.FirstOrDefaultAsync(c => c.Name == name);
				if (existing != null)
				{
					if (!refresh || !existing.ImportedFromCapture)
					{
						skipped++;
						if (refresh && !existing.ImportedFromCapture)
							Console.WriteLine($"Characters: kept '{name}' - created in-game (no ImportedFromCapture marker), so a refresh must not touch it.");

						continue;
					}

					// Delete and reinsert, not a field-by-field update: the character's capture-derived state is
					// spread over a dozen child tables (items, equipment, scaffold rows, spellbook, stat sheet) and
					// a partial refresh would leave whichever rows the new dump no longer states. The FK cascade is
					// what makes this one statement; `PRAGMA foreign_keys` is on through EF, unlike the sqlite3 CLI.
					// It costs the player's own rearrangement of an imported character's bag, which is the trade
					// `--mode overwrite` is asking for.
					db.Characters.Remove(existing);
					await db.SaveChangesAsync();
					refreshed++;
				}

				SpawnBodyReader.TryParse(body, out var scaffold);
				SpawnBodyReader.TryReadCharacterMessage(body, out var read);

				// The class lives in the pre-IIR frame's u16 value island at body offset 135: its low byte is
				// the class id (the high byte is a hash group), consistent with the captured character lists.
				// This covers other players too - the char list (0x20EF) only exists for the capturing account's
				// own characters and serves as the cross-check.
				var classId = charList.GetValueOrDefault(name)?.Class ?? (body.Length > 136
					? body[136]
					: 0u);

				// Skip captures taken before class selection: their class byte is unset (0/1), not one of the 12
				// real classes. Materializing them creates broken characters (and would violate the Class check
				// constraint).
				if (!Enum.IsDefined((ConanClass)classId))
				{
					Console.WriteLine($"Characters: skipped '{name}' - invalid/unset class {classId} (captured pre-class-selection).");
					skipped++;
					continue;
				}

				// Skip low-level captures (when a minLevel policy is set): fresh/tutorial characters carry no
				// meaningful progression - base ability scaffold, no trained feats - and only clutter the reference
				// set.
				var level = read.Message?.Stats?.Level ?? 0;
				if (minLevel > 0 && level <= minLevel)
				{
					Console.WriteLine($"Characters: skipped '{name}' - level {level} (<= {minLevel}, fresh/tutorial).");
					skipped++;
					continue;
				}

				await AddCharacter(db, accountId, realmId, name, mapId, classId, scaffold!, read.Message!, snapshots.GetValueOrDefault(handle), charList.GetValueOrDefault(name));
				added++;
			}

			return (best.Count, added, skipped, refreshed);
		}

		private static async Task AddCharacter(DatabaseContext db, uint accountId, uint realmId, string name, uint mapId, uint classId, SimpleCharFullUpdateIIR scaffold, ProtoCoreCharacterIIR message,
			List<byte[]>? snapshots, CharListEntry? listEntry)
		{
			var stats = message.Stats ?? new ProtoCoreCharacterIIR.CharacterStats();

			// The character keeps its original retail id (the spawn handle = the on-wire instance) unless that id
			// is already taken locally - then the DB generates one. Dependents that bake the raw CharacterId
			// (scaffold rows, equipment) are added after the first save settles it.
			var originalId = scaffold.Handle != 0 && !await db.Characters.AnyAsync(c => c.Id == scaffold.Handle)
				? scaffold.Handle
				: 0;

			var character = new Character
			{
				Id = originalId,
				AccountId = accountId,
				Name = name,
				// The marker that makes this row refreshable. Set here and nowhere else, so a character created
				// in-game never carries it and `--mode overwrite` cannot touch one.
				ImportedFromCapture = true,
				AuthChallenge = Guid.NewGuid(),
				CreationDate = DateTimeOffset.UtcNow,
				Level = (ushort)Math.Max(1, stats.Level),
				Race = (Race)stats.Race,
				Class = (ConanClass)classId,
				// The Sex enum is the wire sex-code (MALE=2 / FEMALE=3), so the captured value maps directly.
				Sex = (Sex)stats.Sex,
				// The map active when the spawn was sent (MapChange tracking; the transform's "playfield"
				// varint is a session instance id, not the map), else the char list's persisted map (when
				// the Map row exists - FK), else the Tortage Beach fallback.
				MapId = await ResolveMapId(db, mapId, listEntry),
				RealmId = realmId
			};

			character.PositionX = scaffold.Position.X;
			character.PositionY = scaffold.Position.Y;
			character.PositionZ = scaffold.Position.Z;
			character.RotationX = scaffold.Rotation.X;
			character.RotationY = scaffold.Rotation.Y;
			character.RotationZ = scaffold.Rotation.Z;
			character.RotationW = scaffold.Rotation.W;

			// The captured vitals are deliberately not stored. Vitals are derived from (Class, Level) like every other
			// character's, so an imported character is no different from a created one; a stored copy would disagree
			// with the curve and go stale. The wire scale the capture uses (x100) is applied at send time.

			// The rendered looks, stored as the true wire values (the spawn rebuild emits them as-is).
			character.Body = new CharacterBody
			{
				HeadMesh = stats.HeadMesh,
				Voice = (byte)Math.Min(byte.MaxValue, stats.VoiceSet),
				Size = (byte)Math.Min(byte.MaxValue, stats.MonsterScale)
			};

			// First save assigns the DB-generated id; the scaffold rows and equipment bake the raw CharacterId,
			// so they are added after it exists.
			db.Characters.Add(character);
			await db.SaveChangesAsync();

			// The full spawn scaffold - the captured character's appearance and progression: the entry table read back
			// into the appearance model (CharacterBody sliders + CharacterBodyResources refs - what the spawn is
			// regenerated from and what Faolan.Web renders), the post-IIR id list (the ability/skill scaffold), the
			// pair lists, and the pre-IIR unlocked feat list (a different region and id-space; see
			// docs/subsystems/feats.md).
			//
			// The registry is keyed by wire tag here - the inverse of the ref -> MorphTarget.Id map generation goes
			// through, so tag 133 comes back as "*beardmesh_210844" and 226 as "*texscript_202392".
			var refsByTag = await db.MorphTargets.ToDictionaryAsync(m => (ushort)m.Id, m => m.Name);
			var (morphsApplied, resourcesRecovered) = PlayerSpawnScaffoldRows.ExtractTo(character, scaffold, refsByTag);

			if (morphsApplied > 0 || resourcesRecovered > 0)
				Console.WriteLine($"  {character.Name}: lifted {morphsApplied} morph slider(s) into CharacterBody and recovered {resourcesRecovered} appearance resource(s).");

			// The vanity shown-mask, lifted out of the captured spawn's PreFrame (bit = base equip slot; the
			// vanity-head/-chest dumps carry 0x04 and 0x20 respectively).
			character.VanityShownMask = PlayerSpawnScaffoldRows.ReadVanityShownMask(scaffold);

			var knownTemplates = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();

			// The character's items, from the capture's FullChar snapshot when one was sent for this character's
			// instance: all four containers, worn included. Items not in the Items catalog are skipped.
			var carried = 0;
			var carriedTotal = 0;
			var knownSpells = 0;
			var statSlots = 0;
			if (snapshots is { Count: > 0 })
			{
				try
				{
					(carried, carriedTotal) = await ImportCarriedInventory(db, character.Id, knownTemplates, snapshots);
				}
				catch (Exception e)
				{
					Console.WriteLine($"Character '{name}': inventory import failed ({e.GetType().Name}: {e.Message}); worn equipment only.");
				}

				// The character's spellbook is the snapshot's known-spell list (its actual learned set - every rank,
				// not just the highest; see ImportKnownSpells), not the incremental DispatchLearnSpell grants (which
				// only carry a session's re-learns). Scanned straight out of the raw snapshot bytes so it survives a
				// tails-parse failure on a large caster snapshot.
				try
				{
					knownSpells = await ImportKnownSpells(db, character.Id, snapshots);
				}
				catch (Exception e)
				{
					Console.WriteLine($"Character '{name}': known-spell import failed ({e.GetType().Name}: {e.Message}).");
				}

				// The character's stat sheet (level/vitals/attributes/PvP/ratings/...) authored from the snapshot's
				// stat block, replacing the fixed fresh-character skeleton for this character. Scanned from raw bytes.
				try
				{
					statSlots = await ImportStatBlock(db, character.Id, snapshots);
				}
				catch (Exception e)
				{
					Console.WriteLine($"Character '{name}': stat-block import failed ({e.GetType().Name}: {e.Message}).");
				}
			}

			// Worn equipment from the spawn IIR - a fallback that fills only the equip slots the snapshot did not
			// describe. The snapshot's Equipped container is the better source and runs first: the spawn's worn
			// entries carry the template and nothing else, while the snapshot's carry the stack count, the crafter
			// and the socketed gems.
			var filled = db.ItemInstances.Local.Where(i => i.OwnerCharacterId == character.Id && i.Container == ContainerKind.Equipped).Select(i => i.Slot).ToHashSet();
			var worn = filled.Count;
			foreach (var entry in message.InventoryEntries)
			{
				if (!knownTemplates.Contains(entry.Item.Template1))
					continue;

				// entry.Pos is a raw wire slot: the worn slots and vanity map to a known slot; the bag / out-of-range
				// ids do not.
				var slot = (CharacterEquipmentSlot)entry.Pos;
				if (!Enum.IsDefined(slot) || !filled.Add((int)slot))
					continue;

				db.ItemInstances.Add(new ItemInstance
				{
					OwnerCharacterId = character.Id,
					Container = ContainerKind.Equipped,
					Slot = (int)slot,
					TemplateId = entry.Item.Template1
				});
				worn++;
			}

			await db.SaveChangesAsync();

			Console.WriteLine(
				$"Character '{name}': id {character.Id:X8}{(originalId == 0 ? " (generated)" : "")}, level {character.Level} {character.Sex} {character.Race} {character.Class}, map {character.MapId}, {worn}/{message.InventoryEntries.Count} worn, {carried}/{carriedTotal} carried, {knownSpells} spells, {statSlots} stat slots, {resourcesRecovered} appearance refs, {character.Skills.Count} skill ids, {character.Feats.Count} feats.");
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
				LastIpAddress = ""
			};
			db.Accounts.Add(account);
			await db.SaveChangesAsync();
			Console.WriteLine($"Characters: created import account '{ImportAccountName}' (id {account.Id}).");
			return account.Id;
		}

		// RealmId is required and captures do not carry our local realm ids, so imported characters join the
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

		// CapturedPacket.Body, but only when it is long enough to be a scaffold: the codec's input layout starts
		// [u32 bodyLen][u32 FA015807], so a body under 8 bytes cannot be one. This extra guard is why the check does
		// not live on CapturedPacket - every other caller wants the body whatever its length.
		private static byte[] GetScaffoldBody(CapturedPacket packet)
		{
			var body = packet.Body;
			return body.Length >= 8
				? body
				: [];
		}

		// Parses a retail PlayerAgent character list (the UpdateClientPlayerData layout) into name -> entry;
		// later lists overwrite earlier ones (latest capture state wins).
		private static void HarvestCharacterList(byte[] packet, Dictionary<string, CharListEntry> charList)
		{
			try
			{
				var headerLen = packet.ReadU32Be(8);
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

		// Parses the snapshot's ProtoFullCharacterIIR root and stores every container's entries as item instances
		// (Container = the wire kind, Slot = pos, Count = stat 26, plus the crafter and the socketed gems).
		//
		// The Equipped container (101) is imported here, not from the spawn IIR: the spawn gives the worn templates
		// but carries no stack count, no creator_name and no slot_map, so importing worn items from it would lose the
		// crafter and the gems of every worn item. The spawn is the fallback for slots this does not cover; the caller
		// runs it after this for exactly that reason.
		private static async Task<(int Stored, int Total)> ImportCarriedInventory(DatabaseContext db, uint characterId, IReadOnlySet<uint> knownTemplates,
			IReadOnlyList<byte[]> snapshotBodies)
		{
			// Largest snapshot first; truncated ones fail to split and the next-largest is tried instead.
			ProtoFullCharacterIIR? root = null;
			foreach (var snapshotBody in snapshotBodies.OrderByDescending(b => b.Length))
			{
				try
				{
					// The body opens with the record's own u32 carrier length, which belongs to the sender - the
					// record itself starts behind it.
					root = SnapshotRoot.Of(snapshotBody.AsSpan(4));
					break;
				}
				catch
				{
					// truncated - try the next one
				}
			}

			if (root == null)
				return (0, 0);

			var unresolvedGems = new HashSet<uint>();
			var stored = 0;
			var total = 0;
			foreach (var container in root.Inventories)
			{
				var kind = (ContainerKind)container.InventoryId.Type;

				foreach (var entry in container.Items)
				{
					total++;
					if (CapturedItemInstances.From(entry, characterId, kind, knownTemplates, unresolvedGems) is not { } instance)
						continue;

					db.ItemInstances.Add(instance);
					stored++;
				}
			}

			// Say it out loud rather than importing a gemmed item as gemless: a gem template the Items catalog does not
			// hold is a content gap (the gem was only ever seen inside a socket), and the socket is dropped.
			if (unresolvedGems.Count > 0)
			{
				Console.WriteLine(
					$"  ⚠️  {unresolvedGems.Count} socketed GEM template(s) are missing from Items, so those sockets were dropped: {string.Join(", ", unresolvedGems.OrderBy(id => id))}");
			}

			return (stored, total);
		}

		// The character's spellbook from its FullChar snapshot's known-spell list (KnownSpellList): each type-2 record
		// is a learned spell. This is the character's full learned set carrying every rank - not a "highest rank
		// only" set. That is retail-accurate; collapsing a family to its top rank is a client-side UI option, so never
		// filter the import (or any grant) down to one rank per family. Scanned from the raw snapshot bytes (no tails
		// deserialize). The largest snapshot that yields a list wins; idempotent (skips already-recorded spells).
		private static async Task<int> ImportKnownSpells(DatabaseContext db, uint characterId, IReadOnlyList<byte[]> snapshotBodies)
		{
			KnownSpellList? known = null;
			foreach (var body in snapshotBodies.OrderByDescending(b => b.Length))
			{
				if (KnownSpellListScan.TryExtractFromSnapshot(body, out known))
					break;
			}

			if (known == null)
				return 0;

			var existing = (await db.CharacterSpells.Where(s => s.CharacterId == characterId).Select(s => s.SpellId).ToListAsync()).ToHashSet();
			var spellbookIds = known.Records.Where(r => r.Type == KnownSpellListCodec.SpellType).Select(r => r.Id).Distinct().ToList();

			// CharacterSpell.SpellId FKs Spells. A spellbook id resolves in-client by RDB id regardless of our catalog,
			// so an id with no Spell row is a gap in our catalog, not a fake spell. Skip it (logged) rather than
			// FK-fault; the gap is closed by completing the Spell catalog.
			var cataloged = (await db.Spells.Where(s => spellbookIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
			var uncataloged = spellbookIds.Where(id => !cataloged.Contains(id)).ToList();
			if (uncataloged.Count > 0)
				Console.WriteLine($"Spellbook: character {characterId} - {uncataloged.Count} spell id(s) skipped (not in the Spell catalog): {string.Join(", ", uncataloged)}.");

			var added = 0;
			foreach (var id in spellbookIds)
			{
				if (!cataloged.Contains(id) || !existing.Add(id))
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

		// The character's stat sheet (CharacterStat rows) from its FullChar snapshot stat block: the 17 displayed stats
		// then the 74 sheet stats, in wire order (Ordinal), so the snapshot generator authors them per character
		// instead of the fixed fresh skeleton. The snapshot is the whole sheet, so it replaces any existing rows.
		private static async Task<int> ImportStatBlock(DatabaseContext db, uint characterId, IReadOnlyList<byte[]> snapshotBodies)
		{
			List<(uint Id, uint Value)>? pairs = null;
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

		private static async Task<uint> ResolveMapId(DatabaseContext db, uint spawnMapId, CharListEntry? listEntry)
		{
			foreach (var candidate in new[] { spawnMapId, listEntry?.MapId ?? 0 })
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

		// One retail character-list entry (PlayerAgent 0x20EF): the only wire source for the class, and the
		// character's persisted map (vs the spawn-time MapChange).
		private sealed record CharListEntry(uint Class, uint Level, uint MapId);
	}
}
