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
	// Materializes the CAPTURED characters (NecroL80, DemoL80, Strie, ...) as playable DB characters: every
	// player-own FA015807 spawn that parses with the byte-exact scaffold codec yields the character's name,
	// identity (race/sex/level), looks (head/voice/size, stored as CharacterBody in editor 2x units), vitals,
	// position/map, worn equipment (IIR InventoryEntries whose templates are DB items) and the full spawn
	// scaffold rows (PlayerSpawnScaffoldRows.ExtractTo - the same mapping the golden tests use). Characters
	// whose name already exists in the DB are skipped, so re-running the importer never duplicates or
	// overwrites; the biggest captured spawn per name wins (geared late-game spawns carry the most data).
	public static class CaptureCharacterImporter
	{
		// The account imported characters (and the claims module's AccountClaim rows) attach to. ONE definition -
		// ClaimsModule used to keep a hand-copied duplicate with a comment pointing here, which is exactly how the
		// two drift.
		internal const string ImportAccountName = "faolan";
		// The s2c character list. Named at last (#619): on all 135 captured frames the sender is `PlayerAgent`
		// (0x82F20484) and the receiver `PlayerInterface` (0x7D155738), and the dump's own `MethodId` is the full
		// 32-bit registration hash 0xC414C5EF - which is already in the enum, under the name the client gives it.
		// (`SendCharacterList` was our name for it before the method table was recovered; #615 replaced it with
		// `UpdateClientPlayerData`, which is why the ticket went looking for a member that no longer existed.)
		//
		// It stays a WIRE comparison because that is the space the dump records in, and on the hash-registered pair
		// the rendering keeps only 7 of 32 bits - six PlayerAgent/PlayerInterface pairs really do collide at this
		// width (`AuthenticateLoggedIn`/`ReceivedCharacterSettings` both render 0x20EC). This one does NOT: 0x20EF is
		// unique across both enums' 51 members, so the filter is exact rather than merely close.
		private static readonly uint CharacterListWireOpcode = CapturedPacket.Wire(PlayerInterfaceMethods.UpdateClientPlayerData);
		private const uint InventorySnapshotSubOpcode = FullPlayerCharacterCodec.Opcode;
		private const uint MapChangeSubOpcode = PlayfieldFCodec.Opcode;


		// The account imported characters are attached to. 0 = find-or-create ImportAccountName, which is the normal
		// PLAY account ("faolan"), so the reference characters are reachable from the same login as everything else.
		// Login validation is stubbed, so any password works.
		//
		// This used to import to a dedicated "captured" account, justified in a comment by "a 20+ entry character
		// list breaks the client's auto-enter flow". That is NOT true (verified 2026-07-20), so the split bought
		// nothing and only made the reference characters awkward to reach.
		public static uint AccountId = 0;

		// minLevel (0 = no filter): captured characters at or below this level are skipped as fresh/tutorial chars
		// (no trained feats, base ability scaffold). The module sets the reference-set policy; direct callers/tests
		// default to importing every level.
		// The CLASS of the character this capture was played on, or null when the capture never shows one (a
		// create-editor dump before class selection, or a trace that carries no player spawn).
		//
		// The local player is the one the server sends a personal FullChar snapshot for (FullPlayerCharacterIIR is only
		// ever delivered for your own character - the same test the own-character prune below uses), which gives its
		// instance; the matching FA015807 player spawn then yields the class the same way ImportAsync reads it, from
		// the pre-IIR value island's low byte at body offset 136.
		//
		// Used by the class-specific map import (Map.ClassSpecific): a capture of the Conarch Armory is evidence about
		// ONE class's copy of it, so its spawns are stamped with this.
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

		public static async Task<(int Seen, int Added, int Skipped)> ImportAsync(DatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures, uint minLevel = 0)
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

					// Only PLAYER spawns parse with the scaffold codec (NPC FA015807 bodies have a different
					// post-IIR layout). Editor-preview spawns (the only frame with an EMPTY sublist - what used
					// to be spelled as "the fully-constant 228-byte pre-frame") carry a
					// synthesized "Character<instance>" placeholder name - those are the creator's temp
					// instances, never importable. Real spawns also include OTHER PLAYERS visible in gameplay
					// captures; those are filtered out after pooling (only the capturing account's own played
					// character is imported - see the own-character prune below).
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
				// ⚠️ INSERT-ONLY, and #753 is why it stays that way for now: an imported character's items/worn
				// set/stat sheet are frozen at whichever corpus dump first built them, and a later re-dump cannot
				// update them.
				//
				// ⛔ The refresh rule #753 proposes - "those whose name matches a capture, with locally created ones
				// exempt by construction" - does NOT work, and both halves fail for their own reason:
				//   * this loop only ever iterates names that ARE in a capture (`best` is built from the captures),
				//     so "matches a capture" exempts nothing at all;
				//   * imported characters are attached to the NORMAL PLAY account (`ImportAccountName`) by
				//     deliberate design - the dedicated "captured" account was removed on 2026-07-20 because the
				//     split bought nothing - so a locally created character sits on the same account and the
				//     account cannot discriminate either.
				// So a refresh that deletes-and-reinserts on either rule would delete a player's own character.
				// Upsert needs a real MARKER on the row (set here, checked there); adding one is the actual next
				// step, and until it exists this skip is the only safe behaviour.
				if (await db.Characters.AnyAsync(c => c.Name == name))
				{
					skipped++;
					continue;
				}

				SpawnBodyReader.TryParse(body, out var scaffold);
				SpawnBodyReader.TryReadCharacterMessage(body, out var read);

				// The class lives in the pre-IIR frame's u16 value island at body offset 135: its LOW byte is
				// the class id (verified 10/10 against the captured character lists, all pre-frame variants;
				// the high byte is an hash group). This covers OTHER players too - the char list (0x20EF)
				// only exists for the capturing account's own characters and serves as the cross-check.
				var classId = charList.GetValueOrDefault(name)?.Class ?? (body.Length > 136
					? body[136]
					: 0u);

				// Skip captures taken BEFORE class selection: their class byte is unset (0/1), not one of the 12
				// real classes. Materializing them creates broken chars (and would violate the Class CHECK constraint).
				if (!Enum.IsDefined((ConanClass)classId))
				{
					Console.WriteLine($"Characters: skipped '{name}' - invalid/unset class {classId} (captured pre-class-selection).");
					skipped++;
					continue;
				}

				// Skip low-level captures (when a minLevel policy is set): fresh/tutorial characters carry no
				// meaningful progression - base ability scaffold, no trained feats - and only clutter the reference
				// set. The kept chars are the leveled retail captures (NecroL80/DemoL80 L80, Hakotep L24, AssassinL23 L23).
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

			return (best.Count, added, skipped);
		}

		private static async Task AddCharacter(DatabaseContext db, uint accountId, uint realmId, string name, uint mapId, uint classId, SimpleCharFullUpdateIIR scaffold, ProtoCoreCharacterIIR message,
			List<byte[]>? snapshots, CharListEntry? listEntry)
		{
			var stats = message.Stats ?? new ProtoCoreCharacterIIR.CharacterStats();

			// The character keeps its ORIGINAL retail id (the spawn handle = the on-wire instance, e.g. Strie
			// 0x02165EFF, DemoL80 0x110099BD) unless that id is already taken locally - then the DB generates
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
				AuthChallenge = Guid.NewGuid(),
				CreationDate = DateTimeOffset.UtcNow,
				Level = (ushort)Math.Max(1, stats.Level),
				Race = (Race)stats.Race,
				Class = (ConanClass)classId,
				// The Sex enum IS the wire sex-code (MALE=2 / FEMALE=3), so the captured value maps directly.
				Sex = (Sex)stats.Sex,
				// The map active when the spawn was sent (MapChange tracking; the transform's "playfield"
				// varint is a session instance id, NOT the map), else the char list's persisted map (when
				// the Map row exists - FK), else the Tortage Beach fallback.
				MapId = await ResolveMapId(db, mapId, listEntry),
				RealmId = realmId
			};

			// The transform is typed on the scaffold (ProtoVec3/ProtoQuat), so the position and rotation come off it
			// directly instead of being re-walked as tagged fixed32 pairs here.
			character.PositionX = scaffold.Position.X;
			character.PositionY = scaffold.Position.Y;
			character.PositionZ = scaffold.Position.Z;
			character.RotationX = scaffold.Rotation.X;
			character.RotationY = scaffold.Rotation.Y;
			character.RotationZ = scaffold.Rotation.Z;
			character.RotationW = scaffold.Rotation.W;

			// The captured vitals are deliberately NOT stored. Vitals are derived from (Class, Level) like every other
			// character's, so an imported character is no different from a created one (#578) - carrying the capture's
			// own numbers is what made an imported L80 necromancer's ceiling disagree with the curve by ~2x, and a
			// stored copy is what let the value go stale in the first place. The wire scale the capture uses (x100) is
			// applied at send time.

			// The rendered looks, stored as the TRUE wire values (the spawn rebuild emits them as-is).
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

			// The full spawn scaffold - the captured character's appearance + progression: the entry table read back
			// into the appearance MODEL (CharacterBody sliders + CharacterBodyResources refs - what the spawn is
			// regenerated from and what Faolan.Web renders), the post-IIR id list = the ability/SKILL scaffold, the
			// pair lists, AND the pre-IIR UNLOCKED FEAT list (a different region + id-space; see
			// [[feat_trees_decode_251]] / feats.md).
			//
			// The registry is keyed by wire TAG here - the inverse of the ref -> MorphTarget.Id map generation goes
			// through, so tag 133 comes back as "*beardmesh_210844" and 226 as "*texscript_202392".
			var refsByTag = await db.MorphTargets.ToDictionaryAsync(m => (ushort)m.Id, m => m.Name);
			var (morphsApplied, resourcesRecovered) = PlayerSpawnScaffoldRows.ExtractTo(character, scaffold, refsByTag);

			if (morphsApplied > 0 || resourcesRecovered > 0)
				Console.WriteLine($"  {character.Name}: lifted {morphsApplied} morph slider(s) into CharacterBody and recovered {resourcesRecovered} appearance resource(s).");

			// The vanity shown-mask, lifted back out of the captured spawn's PreFrame. Without this every imported
			// character came in at 0 (NO vanity shown) no matter what the capture held - the one piece of
			// per-character vanity state the import silently dropped. NecroL80's login capture carries 0x000450A4
			// (bits 2/5/7/12/14/18); the vanity-head/-chest dumps carry 0x04 and 0x20 respectively, which is what
			// pinned the bit = base equip slot mapping in the first place (#336).
			character.VanityShownMask = PlayerSpawnScaffoldRows.ReadVanityShownMask(scaffold);

			var knownTemplates = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();

			// The character's ITEMS, from the capture's FullChar snapshot when one was sent for this character's
			// instance: all four containers, WORN INCLUDED (#750). Items not in the Items catalog are skipped.
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

				// The character's SPELLBOOK is the snapshot's known-spell list (its actual learned set - EVERY rank,
				// not just the highest; see ImportKnownSpells), NOT the incremental DispatchLearnSpell grants (which
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

			// Worn equipment from the SPAWN IIR - a FALLBACK that fills only the equip slots the snapshot did not
			// describe. The snapshot's Equipped container is the better source and runs first: the spawn's worn
			// entries carry the template and nothing else, while the snapshot's carry the stack count, the crafter
			// and the SOCKETED GEMS. (The snapshot pass used to SKIP container 101 on the reasoning that "the spawn
			// already provided the worn set" - which threw away the crafter and the gems of every worn item, and
			// there was nowhere to put either until worn items became instances.)
			var filled = db.ItemInstances.Local.Where(i => i.OwnerCharacterId == character.Id && i.Container == ContainerKind.Equipped).Select(i => i.Slot).ToHashSet();
			var worn = filled.Count;
			foreach (var entry in message.InventoryEntries)
			{
				if (!knownTemplates.Contains(entry.Item.Template1))
					continue;

				// entry.Pos is a raw wire slot: the worn slots + vanity map to a known slot; the bag / out-of-range
				// ids don't.
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

		// CapturedPacket.Body, but only when it is long enough to BE a scaffold: the codec's input layout starts
		// [u32 bodyLen][u32 FA015807], so a body under 8 bytes cannot be one. This extra guard is why the check does
		// not live on CapturedPacket - every other caller wants the body whatever its length.
		private static byte[] GetScaffoldBody(CapturedPacket packet)
		{
			var body = packet.Body;
			return body.Length >= 8
				? body
				: [];
		}

		// Parses a retail PlayerAgent character list (the SendCharacterList layout) into name -> entry;
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

		// Parses the snapshot's ProtoFullCharacterIIR root and stores EVERY container's entries as item instances
		// (Container = the wire kind, Slot = pos, Count = stat 26, plus the crafter and the socketed gems).
		//
		// ⚠️ The EQUIPPED container (101) is imported HERE, not from the spawn IIR. It used to be skipped on the
		// reasoning that "the spawn IIR already provided the worn set" - and the spawn does give the worn TEMPLATES, but
		// it carries no stack count, no creator_name and no slot_map. So skipping it threw away the crafter and the gems
		// of every WORN item, and there was nowhere to put either until worn items became instances (#750). Every
		// socketed-worn-item sighting in the corpus is in this container. The spawn is the FALLBACK for slots this does
		// not cover; the caller runs it after this for exactly that reason.
		private static async Task<(int Stored, int Total)> ImportCarriedInventory(DatabaseContext db, uint characterId, IReadOnlySet<uint> knownTemplates,
			IReadOnlyList<byte[]> snapshotBodies)
		{
			// Largest snapshot first; analyzer-truncated ones (the TCP-reassembly gap) fail to split and the
			// next-largest is tried instead.
			ProtoFullCharacterIIR? root = null;
			foreach (var snapshotBody in snapshotBodies.OrderByDescending(b => b.Length))
			{
				try
				{
					// The body opens with the record's own u32 carrier length, which belongs to the sender - the
					// record itself starts behind it (#530 rule 2).
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
			// hold is a CONTENT gap (the gem was only ever seen inside a socket), and the socket is dropped.
			if (unresolvedGems.Count > 0)
			{
				Console.WriteLine(
					$"  ⚠️  {unresolvedGems.Count} socketed GEM template(s) are missing from Items, so those sockets were dropped: {string.Join(", ", unresolvedGems.OrderBy(id => id))}");
			}

			return (stored, total);
		}

		// The character's SPELLBOOK from its FullChar snapshot's known-spell list (KnownSpellList): each type-2 record
		// is a learned spell. This is the char's full learned set carrying EVERY RANK - not a "highest rank only"
		// set: NecroL80's imported book holds Pestilential Blast R1-R6, Ice Strike R1-R6, and so on. That is correct
		// and retail-accurate; collapsing a family to its top rank is a CLIENT-side UI option, so never filter the
		// import (or any grant) down to one rank per family. Scanned from the raw snapshot bytes (no tails
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
			// so an id with no Spell row is a gap in our (incomplete) catalog, not a fake spell (e.g. NecroL80's Corrupted
			// Soul rank 222682). Skip it (logged) rather than FK-fault; the gap is closed by completing the Spell catalog.
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

		// The character's STAT SHEET (CharacterStat rows) from its FullChar snapshot stat block: the 17 displayed stats
		// then the 74 sheet stats, in wire order (Ordinal), so the snapshot generator authors them PER-CHARACTER
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

		// One retail character-list entry (PlayerAgent 0x20EF): the only wire source for the CLASS, and the
		// character's persisted map (vs the spawn-time MapChange).
		private sealed record CharListEntry(uint Class, uint Level, uint MapId);
	}
}
