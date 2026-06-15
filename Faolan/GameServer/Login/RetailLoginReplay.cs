using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.Spawn;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its burst is always seeded (the proven blob), so
		// any character whose own map has no usable burst still enters a real, rendered world instead of a hand-built
		// one. (There is no hand-built login/teleport path anymore.)
		private const uint DefaultWorldMapId = Map.TortageBeachId;

		// The one beach NPC whose SpawnCheck answer carries status 0x02 (it is Casilda / the intro NPC, handle
		// 0x00081906 in the capture); every other polled entity gets status 0x00. This is an NPC handle from the
		// burst, NOT the player (the client never polls its own handle), so it is unchanged across characters.
		private const uint SpawnCheckStatusTwoHandle = 0x00081906;
		private const uint StarterFirstBagItem = 3774542; // Royal Signet (first bag slot)

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 in its tails for EVERY character
		// (verified across the Strie / hoxxling / randochar captures - it is the canonical beach starting quest, not
		// a per-character artifact). Since the snapshot is replayed and the client renders that quest, persist it so
		// the DB matches what the journal shows (otherwise CharacterQuests is empty while the journal shows a quest -
		// see snapshot_embeds_strie_quest). See EnsureBeachStarterQuest / WorldQuest.BeachStarterQuestId.
		private const uint BeachStarterQuestId = WorldQuest.BeachStarterQuestId;

		// #184: Casilda is CHAINED (arms-up) until the player accepts her quest 13479, then UNCHAINED. The captured
		// beach burst has her chained; the position-matched NpcSpawns row is the unchained capture. So pre-quest we
		// keep her body VERBATIM (the burst's chained pose) and post-accept we serve the from-DB unchained rebuild.
		private const uint CasildaQuestId = 13479;
		private const string CasildaName = "Casilda";

		// Default starter gear for beach characters (item ids that are fully buildable: p_items identity + harvested
		// archetype), keyed by the CharacterEquipment slot they go in. Slot-appropriate so the client accepts them.
		private const uint StarterWristItem = 3361557; // Slave Manacles (wrist, 0x12)

		// Rebuilds the (already instance-substituted) world-entry inventory snapshot (0x737A6DF9): patches the LIVE
		// character's level/vitals into the tails and substitutes the player's DB containers for the captured ones
		// (the correct BE32 blob-length framing, see below) - this snapshot drives the bag AND the equipment doll.
		//
		// HISTORY: container substitution used to stall world-load/chat ("any container mutation breaks chat",
		// [[protocol_chat_connection_regression]]) - that bisect PRE-DATED the ContentMirror; the stall was the
		// client thrashing on item resources it could not fetch from the dead retail CDN. With the mirror serving
		// resources locally, substitution is in-client verified safe (2026-06-12: world loads, chat connects, DB
		// items render in bag + doll). A SECOND snapshot later in the session is NOT a delivery mechanism - the
		// client ignores/rejects re-constructs for the same identity (see Packets.cs post-load note).
		//
		// IMPORTANT: built entirely with manual byte assembly + manual CRC and the ORIGINAL packet's header copied
		// verbatim - NOT via PacketStream/WriteLengthHash. An earlier PacketStream-based rebuild stalled the client
		// on the loading screen even though its output was byte-exact, whereas the manual byte path works.
		private byte[] BuildBeachInventoryPacket(byte[] substitutedPacket, Character character, CharacterEquipment equipment = null, CharacterInventory[] inventoryRows = null, IReadOnlyList<(uint QuestId, byte[] Block)> extraQuestBlocks = null)
		{
			var conanPacket = new ConanPacket(substitutedPacket);
			var body = new FaolanStream(conanPacket.Body.ReadArrayUInt32Length());

			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			// Patch the LIVE character's level + current vitals into the tails (the captured player's values
			// otherwise ride along; tail stat edits are proven safe in-client - they do NOT affect the chat gate).
			// Full vitals at login; the 0x96B8DC59 vitals update after the spawn sets the max side. Best-effort: a
			// tails layout this model can't index just keeps the captured values.
			try
			{
				if (character != null)
				{
					parts.Tails.Level = character.Level;
					if (parts.Tails.HasKnownRepeatedStatTable)
					{
						parts.Tails.PostAccessBlock.CurrentXp = character.Experience;
						if (character.MaxStats != null)
						{
							parts.Tails.PostAccessBlock.CurrentHp = character.MaxStats.Health;
							parts.Tails.PostAccessBlock.CurrentMana = character.MaxStats.Mana;
							parts.Tails.PostAccessBlock.CurrentStamina = character.MaxStats.Stamina;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Tail stat patch failed; keeping the captured tail values.");
			}

			// Containers VERBATIM (the captured inventory tree, re-serialized losslessly - proven byte-identical by
			// InventorySnapshotTests). No DB item substitution here by default (see the method header).
			var rootBytes = parts.Root.ToArray();
			var prefix = parts.Prefix;

			// Substitute the player's DB containers into the WORLD-ENTRY snapshot (the bag + equipment doll).
			// In-client verified 2026-06-13: chat connects, names render, the bag shows the player's real DB items.
			// FRAMING (recovered from AgeOfConan.exe via Ghidra, 2026-06-13 — see docs/archive/db-container-handoff.md):
			// n3InfoItemRemote::Construct reads the body as [u32 KEY=subop][u32 identity.type=50000][u32 instance]
			// [u8 flag][u32 BE blobLen][protobuf blob = blobLen bytes][u32 BE version=0x18][tails subclass]. The
			// "blob" = [field5 (the captured `28 81 06`, = our parts.Prefix[5..])] + [the FullCharacterIIR root =
			// field 6 containers + f7/f8]. The client reads EXACTLY blobLen bytes then expects the 0x18 version — so
			// blobLen MUST equal field5Len + rootLen. The old AdjustSnapshotPrefixRootLength treated the same bytes as
			// a protobuf varint (the byte-misaligned model: `00 00 00 02`=ver, `ae 28`=varint 4483+rootLen, `81 06`=
			// trailing) and on resize wrote the wrong field, leaving blobLen ~0x2xx regardless of the real root → the
			// client over-read into the tails and rejected the FullChar ("Wrong IIR_t version"). SetSnapshotBlobLength
			// writes the correct BE32 length; the captured prefix already carries the right [flag][len][field5] shape.
			if (equipment != null)
			{
				try
				{
					var equipped = new Dictionary<ulong, (Item Item, uint Archetype)>();
					foreach (var (slot, get) in InventoryEquippedSlots)
					{
						var item = get(equipment);
						if (item is { Archetype: not 0 })
							equipped[slot] = (item, item.Archetype);
					}

					(Item, uint)? bagFirst = equipment.FirstPosBag is { Archetype: not 0 }
						? (equipment.FirstPosBag, equipment.FirstPosBag.Archetype)
						: null;

					var typedRoot = ((ReadOnlySpan<byte>)rootBytes).FromProtobuf<ProtoFullCharacterIIR>();
					typedRoot.Inventories.Clear();
					foreach (var container in FullCharSnapshotBuilder.BuildContainers(instance, equipped, bagFirst, inventoryRows ?? []))
						typedRoot.Inventories.Add(container);
					var newRoot = typedRoot.ToProtobufBytes();
					prefix = SetSnapshotBlobLength(parts.Prefix, newRoot.Length);
					Logger.LogInformation("World-entry DB containers: root {Old} -> {New} bytes ({Equipped} equipped + {Rows} rows); blobLen={Blob}.", rootBytes.Length, newRoot.Length, equipped.Count,
						(inventoryRows ?? []).Length, (parts.Prefix.Length - 5) + newRoot.Length);
					rootBytes = newRoot;
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex, "World-entry DB container substitution failed; keeping verbatim containers.");
				}
			}

			// Body content: [subop][c350][instance][prefix][root][tails], then prefixed by its uint32 length.
			using var contentStream = new FaolanStream();
			contentStream.WriteUInt32(subOpcode);
			contentStream.WriteUInt32(c350);
			contentStream.WriteUInt32(instance);
			contentStream.WriteArray(prefix);
			contentStream.WriteArray(rootBytes);
			// Author the character's accepted quests (beyond the beach starter) into the tails so they show in the
			// J-panel on login (SnapshotQuestInjector; FAOLAN_SNAPSHOT_QUESTS). A no-op when none / the flag is off.
			var tailsBytes = parts.Tails.ToArray();
			if (extraQuestBlocks is { Count: > 0 })
			{
				var before = tailsBytes.Length;
				tailsBytes = SnapshotQuestInjector.Inject(tailsBytes, extraQuestBlocks);
				if (tailsBytes.Length != before)
					Logger.LogInformation("Snapshot quests: injected up to {N} block(s); tails {Before} -> {After} B.", extraQuestBlocks.Count, before, tailsBytes.Length);
			}

			contentStream.WriteArray(tailsBytes);
			var bodyContent = contentStream.ToArray();

			// Copy the original header (len4 + crc4 + headerLen4 + header bytes) verbatim, then append the rebuilt
			// body (length-prefixed). Fix up the outer length + CRC by hand.
			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(substitutedPacket.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;

			var rebuilt = new byte[bodyStart + 4 + bodyContent.Length];
			Array.Copy(substitutedPacket, 0, rebuilt, 0, bodyStart);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(bodyStart, 4), (uint)bodyContent.Length);
			bodyContent.CopyTo(rebuilt, bodyStart + 4);

			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);

			return rebuilt;
		}

		// Builds the per-quest snapshot detail blocks for the character's accepted quests beyond the beach starter
		// (which the template already carries) - the input to SnapshotQuestInjector. Empty unless FAOLAN_SNAPSHOT_QUESTS
		// is set. The blocks carry the live instance (the tails hold raw entity references). docs/quest-log-handover.md.
		private async Task<IReadOnlyList<(uint QuestId, byte[] Block)>> BuildSnapshotQuestBlocks(INetworkClient client, uint liveInstance)
		{
			if (Environment.GetEnvironmentVariable("FAOLAN_SNAPSHOT_QUESTS") != "1" || client.Session?.Character == null)
				return [];

			var blocks = new List<(uint, byte[])>();
			var quests = await Database.GetCharacterQuests(client.Session.Character.Id);
			foreach (var quest in quests.Where(q => q.State == QuestState.Active && q.QuestId != WorldQuest.BeachStarterQuestId))
			{
				var def = await Database.GetQuestDefinition(quest.QuestId);
				if (def != null)
					blocks.Add((quest.QuestId, _broadcaster.BuildSnapshotQuestBlock(liveInstance, def)));
			}

			return blocks;
		}

		// Equipped-container slot id -> the CharacterEquipment nav (archive player_items slot bytes).
		private static readonly (ulong Slot, Func<CharacterEquipment, Item> Get)[] InventoryEquippedSlots =
		[
			(0x01, e => e.Neck), (0x02, e => e.Head), (0x03, e => e.Tokens), (0x05, e => e.Chest), (0x06, e => e.MainHandLeft), (0x07, e => e.Belt), (0x08, e => e.OffHandLeft), (0x09, e => e.RingLeft),
			(0x0a, e => e.RingRight), (0x0b, e => e.Cloak), (0x0c, e => e.Shoulder), (0x0e, e => e.Boots), (0x0f, e => e.Hands), (0x10, e => e.Legs), (0x12, e => e.Wrist), (0x14, e => e.MainHandRight), (0x15, e => e.OffHandRight)
		];

		// Sets the snapshot's protobuf-blob length to match a regenerated root. The 8-byte prefix the client reads
		// is [u8 flag][u32 BE blobLen][field5 = the rest], where the client (n3InfoItemRemote::Construct ->
		// FUN_00cbfb80 in AgeOfConan.exe) reads EXACTLY blobLen bytes as the protobuf blob = field5 + the
		// FullCharacterIIR root, then expects the 0x18 version. So blobLen = field5Len + rootLength, written as a
		// 4-byte big-endian int at prefix offset 1. (This SUPERSEDES AdjustSnapshotPrefixRootLength, which adjusted
		// a byte-misaligned protobuf-varint model and corrupted the real length on resize — see
		// docs/archive/db-container-handoff.md / [[protocol_containeradditem]].) field5Len = prefix.Length - 5 (the flag +
		// the 4 length bytes); the captured field5 bytes at prefix[5..] are preserved unchanged.
		internal static byte[] SetSnapshotBlobLength(byte[] prefix, int rootLength)
		{
			if (prefix.Length < 5 || rootLength < 0)
				return prefix;

			var field5Len = prefix.Length - 5;
			var result = (byte[])prefix.Clone();
			BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1, 4), (uint)(field5Len + rootLength));
			return result;
		}

		// Loads the character's CharacterEquipment (creating it and seeding default starter gear if absent), with the
		// item navs included so they can be built into the snapshot. Beach characters only.
		private async Task<CharacterEquipment> EnsureBeachStarterEquipment(Character character)
		{
			if (character == null)
				return null;

			try
			{
				var equipment = await Database.GetCharacterEquipment(character.Id);
				if (equipment != null)
					return equipment;

				// Seed the default starter gear ONLY for a brand-new equipment row. Re-filling emptied slots on every
				// login would undo the player's own unequips (which persistence writes back), so an existing row is
				// left exactly as the player left it.
				equipment = new CharacterEquipment
				{
					Id = character.Id,
					CharacterId = character.Id,
					Wrist = await Database.GetItem(StarterWristItem),
					FirstPosBag = await Database.GetItem(StarterFirstBagItem)
				};
				await Database.SaveCharacterEquipment(equipment);

				return equipment;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to load/seed CharacterEquipment for {CharacterId}; sending inventory verbatim.", character.Id);
				return null;
			}
		}

		// Persists the beach starting quest (10238) the world-entry snapshot already shows, so the journal matches the
		// DB. Seeded ONCE: only when the character has NO row for it at all (so a later complete/abandon isn't undone by
		// re-seeding an Active row on every login). Best-effort - a failure just leaves the journal/DB out of sync.
		private async Task EnsureBeachStarterQuest(Character character)
		{
			if (character == null)
				return;

			try
			{
				var quests = await Database.GetCharacterQuests(character.Id);
				if (quests.Any(q => q.QuestId == BeachStarterQuestId))
					return; // already has it (active or completed) - leave the player's state alone

				await Database.SetCharacterQuest(character.Id, BeachStarterQuestId, QuestState.Active);
				Logger.LogInformation("Seeded beach starting quest {QuestId} for {Name}.", BeachStarterQuestId, character.Name);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to seed beach starting quest for {CharacterId}.", character.Id);
			}
		}

		// Loads the character's spawn-scaffold rows, seeding the fresh-character defaults once for characters that
		// have none (like EnsureBeachStarterEquipment seeds starter gear). Returns null on failure - the spawn
		// then keeps the captured scaffold frame.
		private async Task<Character> EnsureSpawnScaffoldRows(Character character)
		{
			if (character == null)
				return null;

			try
			{
				var scaffoldCharacter = await Database.GetCharacterScaffold(character.Id) ?? character;

				// Resolve the character's chosen appearance resources (editor SetMorphValue refs) to the
				// weighted entry tags via the imported client registry; null (nothing stored / registry not
				// imported) falls back to the Dikketest defaults inside EnsureFreshRows.
				List<ScaffoldEntry> weighted = null;
				if (scaffoldCharacter.BodyResources is { Count: > 0 })
				{
					var registry = await Database.GetMorphTargets(scaffoldCharacter
						.BodyResources
						.Select(r => r.Ref)
						.ToArray());
					weighted = PlayerSpawnScaffoldRows.BuildWeightedEntries(scaffoldCharacter.BodyResources, registry);
					if (weighted == null)
					{
						Logger.LogWarning("Character {CharacterId} has {Count} chosen appearance resources but none resolved via MorphTargets (registry imported?); using the default weighted entries.", character.Id,
							scaffoldCharacter.BodyResources.Count);
					}
				}

				if (!PlayerSpawnScaffoldRows.EnsureFreshRows(scaffoldCharacter, weighted))
					return scaffoldCharacter;

				await Database.SaveCharacterScaffold(scaffoldCharacter);
				Logger.LogInformation("Seeded fresh spawn-scaffold rows for character {CharacterId}.", character.Id);
				return scaffoldCharacter;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to load/seed spawn-scaffold rows for {CharacterId}; keeping the captured scaffold frame.", character.Id);
				return null;
			}
		}

		// Builds one NpcSpawn row from scratch (spawn + stationary MoveTask) with a fresh server handle and appends
		// it to the burst. Opt-in (FAOLAN_BUILT_NPC_TEST=1); a no-op without the templates or a usable row.
		private void InjectBuiltTestNpc(INetworkClient client, NpcSpawn[] npcRows, byte[] spawnTemplate, byte[] moveTaskTemplate, List<byte[]> toSend)
		{
			if (Environment.GetEnvironmentVariable("FAOLAN_BUILT_NPC_TEST") != "1" || spawnTemplate == null || moveTaskTemplate == null)
				return;

			const uint builtHandle = 0x00090001; // above the burst's 0x000818xx handles and below the instance range
			var playerPos = client.Session.Character.Position;
			var pos = new Vector3(playerPos.X + 4f, playerPos.Y, playerPos.Z + 4f);

			// VALIDATE NpcSpawnBuilder: build the spawn FROM the template's own (known-good) bytes via the builder
			// (transform rebuilt to `pos`, CharacterIIR re-encoded), and pair it with the proven cloned MoveTask.
			// If this renders + polls, NpcSpawnBuilder produces client-acceptable output and the only open issue is
			// cross-session DB row data (the earlier built Crocodile came from a different session's capture).
			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(spawnTemplate.AsSpan(8, 4));
			var templateRow = new NpcSpawn
			{
				Data = spawnTemplate,
				PositionX = pos.X,
				PositionY = pos.Y,
				PositionZ = pos.Z,
				RotationX = BinaryPrimitives.ReadSingleLittleEndian(spawnTemplate.AsSpan(bodyStart + SpawnPacketBuilder.RotXOffset, 4)),
				RotationY = BinaryPrimitives.ReadSingleLittleEndian(spawnTemplate.AsSpan(bodyStart + SpawnPacketBuilder.RotYOffset, 4)),
				RotationZ = BinaryPrimitives.ReadSingleLittleEndian(spawnTemplate.AsSpan(bodyStart + SpawnPacketBuilder.RotZOffset, 4)),
				RotationW = BinaryPrimitives.ReadSingleLittleEndian(spawnTemplate.AsSpan(bodyStart + SpawnPacketBuilder.RotWOffset, 4))
			};
			if (!NpcSpawnBuilder.TryBuild(templateRow, builtHandle, out var spawnBody))
				return;

			// Opt-in (FAOLAN_MINIMAL_SCAFFOLD=1): swap the carried post-IIR for the minimal session-independent
			// form. The in-client experiment for the cross-session render fix - if the built NPC still renders with
			// the minimal scaffold, it is client-acceptable and the cross-session row's stale post-IIR was the
			// rejection cause (see NpcSpawnScaffold / [[world-packets-construct-not-replay]]).
			if (Environment.GetEnvironmentVariable("FAOLAN_MINIMAL_SCAFFOLD") == "1")
			{
				spawnBody = NpcSpawnScaffold.WithMinimalPostIir(spawnBody);
				Logger.LogInformation("Built NPC: swapped in the minimal session-independent post-IIR scaffold (body now {Len}B).", spawnBody.Length);
			}

			// Validate the minimal STATIONARY MoveTask (MoveTaskBuilder): if she now spawns by the player and STAYS
			// (no drag to a patrol route), the built stationary MoveTask is accepted - the full burst-free primitive
			// set (built spawn + built stationary MoveTask) is proven in-client.
			var moveBody = MoveTaskBuilder.BuildStationary(builtHandle, pos);
			toSend.Add(FrameLikeTemplate(spawnTemplate, spawnBody));
			toSend.Add(FrameLikeTemplate(moveTaskTemplate, moveBody));
			Logger.LogInformation("Injected BUILT spawn + BUILT stationary MoveTask at handle 0x{Handle:X} pos ({X},{Y},{Z}).", builtHandle, pos.X, pos.Y, pos.Z);
		}

		// Regenerates the spawn body's scaffold frame from the character's DB rows via the byte-exact codec
		// (PlayerSpawnScaffoldCodec round-trips every captured player spawn). Returns the body unchanged when it
		// doesn't parse or the character has no rows.
		private byte[] RebuildScaffoldFromRows(byte[] body, Character scaffoldCharacter)
		{
			if (scaffoldCharacter == null)
				return body;

			if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error))
			{
				Logger.LogWarning("Player spawn body did not parse as a scaffold ({Error}); keeping the captured frame.", error);
				return body;
			}

			if (!PlayerSpawnScaffoldRows.TryApplyFrom(scaffoldCharacter, scaffold))
				return body;

			return PlayerSpawnScaffoldCodec.Generate(scaffold);
		}

		// Replays a map's captured world burst (from the DB), re-addressed from the burst's captured instance to the
		// logged-in character. Works for ANY map that has a stored burst (beach included - its burst is the proven
		// blob, seeded into the DB by Faolan.Importer).
		private async Task ReplayMapWorldBurst(INetworkClient client, MapWorldBurst burst)
		{
			// Rebuild the exact original packet bytes from the structured IIR records (IirRecordCodec is round-trip
			// lossless), then drive the same proven byte-preserving replay (instance rewrite + inventory/spawn rebuild).
			// Drop transient gameplay records (movement/combat/regen from a mid-session capture) that would feed the
			// client movement/combat for entities that never cleanly spawned, stalling the playfield build. The
			// importer already drops these (IirMessageTypes.IsTransientGameplay), so this is a no-op on a freshly
			// imported burst - kept as defense-in-depth against an older/dirty burst. Vitals are server-generated
			// below (BuildVitals).
			var records = burst
				.Entities
				.OrderBy(e => e.Sequence)
				.Where(e => !IirMessageTypes.IsTransientGameplay(e.IirType))
				.ToList();
			var dropped = burst.Entities.Count - records.Count;
			if (dropped > 0)
				Logger.LogInformation("World burst for map {Map}: dropped {Dropped} transient gameplay records ({Kept} structural records kept).", burst.MapId, dropped, records.Count);
			var packets = records
				.Select(IirRecordCodec.Rebuild)
				.ToList();
			var capturedInstance = burst.CapturedInstance;
			var liveInstance = client.Session.Account.ClientInstance;
			Logger.LogInformation("Replaying world burst for map {Map} ({Count} packets, instance {Old:X8} -> {New:X8}).", burst.MapId, packets.Count, capturedInstance, liveInstance);

			// Record this playfield's despawn token from its 0x737A6DF9 snapshot tail (preserved verbatim in our
			// regenerated snapshot), so n3ToClientQuit removals carry the value the client learned here (WorldDespawn).
			var fullChar = records.FirstOrDefault(e => e.IirType == IirMessageTypes.FullChar);
			if (fullChar?.Body != null)
				_broadcaster.CapturePlayfieldToken(burst.MapId, fullChar.Body);

			client.Session.UsedWorldBurstReplay = true;

			// Load the character's CharacterEquipment (seeding default starter gear once for new beach characters).
			// This drives the player's WORN gear into their entity SPAWN (TryRebuildPlayerSpawnFromDb), which is the
			// chat-safe, DB-driven path for visible equipment. The world-entry inventory SNAPSHOT (0x737A6DF9), by
			// contrast, is sent verbatim - substituting DB items into it stalls the client's world-load completion
			// (it can't resolve arbitrary items' RDB resources) so it never dials the chat server. See
			// BuildBeachInventoryPacket / [[protocol_chat_connection_regression]].
			var equipment = await EnsureBeachStarterEquipment(client.Session.Character);
			await EnsureBeachStarterQuest(client.Session.Character);
			var inventoryRows = client.Session.Character != null
				? await Database.GetCharacterInventory(client.Session.Character.Id)
				: [];

			// #184: keep Casilda's chained pose (the verbatim burst body) until the player accepts her quest 13479
			// (HasQuest = active OR completed, like the dialogue gate); after that she renders un-chained from the DB.
			var keepCasildaChained = client.Session.Character != null
				&& !(await Database.GetCharacterQuests(client.Session.Character.Id)).Any(q => q.QuestId == CasildaQuestId);

			// The spawn-scaffold rows (entry table / feat-ID list / pre-IIR frame) drive the player spawn's
			// scaffold frame; characters without rows are seeded with the fresh-character defaults once.
			var scaffoldCharacter = await EnsureSpawnScaffoldRows(client.Session.Character);

			// NPC spawns sourced from the NpcSpawns table so each NPC's FA015807 body is BUILT from its row
			// (transform columns + IIR) via NpcSpawnBuilder rather than taken verbatim from the burst. Keyed by the
			// stable spawn POSITION (the dedup key) - NOT the handle, which is session-volatile - and rebuilt with
			// the burst's own handle so it stays paired with the burst's 96C46740 registration. Same-source rows
			// rebuild byte-identical; cross-session rows are a genuine reconstruction of the same NPC at the same
			// spot. The player's own spawn and non-NPC records are untouched.
			var npcRows = await Database.GetNpcSpawns(burst.MapId);
			var npcByPosition = new Dictionary<(int, int, int), NpcSpawn>();
			foreach (var n in npcRows)
				npcByPosition.TryAdd(PositionKey(n.PositionX, n.PositionY, n.PositionZ), n);
			var npcBuilt = 0;
			var npcIdentical = 0;

			// Static world objects (decor/containers/zones) sourced from the WorldObjects table the same way -
			// keyed by position, rebuilt via WorldObjectBuilder with the burst's handle, put on the wire only when
			// byte-identical to the re-addressed burst body (zero render risk).
			var worldObjectRows = await Database.GetWorldObjects(burst.MapId);
			var objectByPosition = new Dictionary<(int, int, int), WorldObject>();
			foreach (var o in worldObjectRows)
				objectByPosition.TryAdd(PositionKey(o.PositionX, o.PositionY, o.PositionZ), o);
			var objectBuilt = 0;
			var objectIdentical = 0;

			// IMPORTANT: do ALL per-packet work (instance rewrite, player-spawn rebuild, inventory parse/rebuild)
			// HERE, before the send loop. The inventory rebuild in particular parses ~9 KB of tails and re-serializes
			// - doing that mid-loop adds a CPU stall between sends that reshapes the burst's TCP segmentation, and
			// the client (no reassembly on the uncompressed download) then misframes and hangs on the loading
			// screen. Pre-building keeps the actual send loop tight and uniform.
			var toSend = new List<byte[]>(packets.Count + 2);

			// MapChange must lead the world build. It's a global record the server always generates from the live
			// map/instance - it is never stored in the burst - so emit it first.
			toSend.Add(BuildMapChange(client));

			// Templates for the additive built-NPC test (FAOLAN_BUILT_NPC_TEST): the first live-addressed NPC
			// spawn + MoveTask packets, reused as framing headers for a from-scratch built entity.
			byte[] spawnTemplate = null;
			byte[] moveTaskTemplate = null;

			// FAOLAN_NPC_INDIVIDUAL_SPAWN: render NPCs as the burst does today (default), OR drop every NPC record
			// from the burst (its FA015807 spawns + their 96C46740 MoveTasks) and let the interest system spawn each
			// NPC individually at entry (BroadcastPlayerEntry). The burst-rendered NPCs use the burst's wire handles
			// while the model uses the row handles, so they are NOT seeded into Known and the first move re-streams
			// all of them -> mass double-spawn (the "nameplate flickers then clears" bug). Making interest the SOLE
			// NPC path removes the duplication entirely. The player's own spawn, containers, world objects and the
			// snapshot stay in the burst.
			var npcIndividualSpawn = Environment.GetEnvironmentVariable("FAOLAN_NPC_INDIVIDUAL_SPAWN") == "1";

			// FAOLAN_SNAPSHOT_QUESTS (opt-in): author the character's accepted quests into the login snapshot tails so
			// they show in the J-panel on relog (the snapshot, not packets, delivers the login quest log). Default off
			// - snapshot-tails surgery is in-client-gated; bisect the block length live. docs/quest-log-handover.md.
			var snapshotQuestBlocks = await BuildSnapshotQuestBlocks(client, liveInstance);

			foreach (var packet in packets)
			{
				// Re-address the capture's instance to the logged-in character so the burst works for any character.
				// The NPC handles in the burst are left as-is.
				var pkt = RewriteBurstInstance(packet, capturedInstance, liveInstance);

				if (npcIndividualSpawn && IsBurstNpcRecord(client, pkt))
					continue; // NPC spawn/MoveTask - spawned individually via interest instead (no burst double-spawn)

				if (IsInventorySnapshot(pkt))
				{
					// The world-entry snapshot: tails patched to the live character, containers substituted with the
					// player's DB items (BuildBeachInventoryPacket) - this is THE inventory delivery (bag + doll).
					pkt = BuildBeachInventoryPacket(pkt, client.Session.Character, equipment, inventoryRows, snapshotQuestBlocks);
				}
				else if (TryRebuildPlayerSpawnFromDb(client, equipment, scaffoldCharacter, pkt, out var rebuilt))
				{
					pkt = rebuilt;
					_broadcaster.CaptureOwnSpawnBody(client, pkt); // stash the player's FA015807 body so other players can be sent it
				}
				else if (TryRebuildNpcSpawnFromDb(client, npcByPosition, pkt, keepCasildaChained, out var npcRebuilt, out var wasIdentical))
				{
					npcBuilt++;
					// The from-DB rebuild (DB-row identity/scaffold + the burst's EXACT transform spliced in, see
					// TryRebuildNpcSpawnFromDb) now goes on the wire for EVERY matched NPC, not just byte-identical ones.
					// #185 in-client verified 2026-06-14: the old "kept verbatim for non-identical / pcDynel-is-NULL"
					// problem was a spawn<->MoveTask position inconsistency, NOT the scaffolds; with the transform
					// matching the paired MoveTask, all 23 cross-session beach NPCs render from the DB. The from-model
					// interest spawn (EnsureZoneNpcs, for NPCs OUTSIDE the burst) is a separate path. Known fidelity gaps:
					// the row's IIR pose (Casilda renders un-chained, TODO #184) + a transient half-stream on moving NPCs.
					// Put the from-DB rebuild on the wire (it carries the burst's exact transform, so it renders -
					// #185 in-client verified). FAOLAN_NPC_VERBATIM=1 restores the old "byte-identical only" behaviour.
					if (wasIdentical || Environment.GetEnvironmentVariable("FAOLAN_NPC_VERBATIM") != "1")
					{
						pkt = npcRebuilt;
						if (wasIdentical)
							npcIdentical++;
					}
				}
				else if (TryRebuildWorldObjectFromDb(objectByPosition, pkt, out var objRebuilt, out var objIdentical))
				{
					objectBuilt++;
					if (objIdentical)
					{
						pkt = objRebuilt;
						objectIdentical++;
					}
				}

				// Record each spawned NPC's handle -> name so a clicked handle can be routed to its conversation by
				// the stable name (handles are session-volatile). Reads the final bytes the client receives.
				RecordSpawnedNpcName(client, pkt);

				CaptureInjectionTemplate(client, pkt, ref spawnTemplate, ref moveTaskTemplate);
				toSend.Add(pkt);
			}

			// ADDITIVE built-NPC test (opt-in via FAOLAN_BUILT_NPC_TEST=1): inject one fully-constructed entity -
			// an NpcSpawn built from scratch (NpcSpawnBuilder) + a built stationary MoveTask (MoveTaskBuilder),
			// with a fresh server handle - alongside the verbatim burst. If it renders, the burst-free path is
			// validated end-to-end in-client; if not, the burst NPCs are unaffected. Off by default (no impact).
			InjectBuiltTestNpc(client, npcRows, spawnTemplate, moveTaskTemplate, toSend);

			if (npcByPosition.Count > 0)
				Logger.LogInformation("NPC spawns for map {Map}: {Matched} burst NPCs matched a row; {Identical} put on the wire as byte-identical DB rebuilds, the other {Verbatim} kept VERBATIM (cross-session rebuild diverged); {Rows} rows available.", burst.MapId,
					npcBuilt, npcIdentical, npcBuilt - npcIdentical, npcByPosition.Count);
			if (objectByPosition.Count > 0)
				Logger.LogInformation("World objects for map {Map}: {Identical}/{Built} table-built objects put on the wire byte-identical, {Rows} rows available.", burst.MapId, objectIdentical, objectBuilt,
					objectByPosition.Count);

			// The player's own vitals are server-generated from the live character (not the captured player's), emitted
			// after the player has spawned in the burst.
			toSend.Add(BuildVitals(client));

			// GameTime (the world clock) is a global record the server always generates - never stored in the burst.
			toSend.Add(BuildGameTime(client));

			// Tight send loop - no per-packet computation here.
			foreach (var bytes in toSend)
				await client.SendAsync(bytes);
		}

		// Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: body is
		// 0x0000C350 + the polled entity handle + a status byte (0x02 for the player's own avatar, else 0x00).
		private Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			var status = (byte)(handle == SpawnCheckStatusTwoHandle
				? 0x02
				: 0x00);

			return new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.SpawnCheck)
				.WriteUInt32(0x0000C350)
				.WriteUInt32(handle)
				.WriteByte(status)
				.SendAsync(client);
		}

		// The single existing-character WORLD ENTRY used by both login and the teleport GM command: send the 0x201c
		// channel handshake, then replay the character's current-map world burst from the DB (MapChange + world
		// objects + NPC spawns + the player spawn + the inventory snapshot, all re-addressed and DB-driven). Falls
		// back to the beach burst for any map without a usable burst. The caller has already set Character.MapId/Pos.
		private async Task SendWorldEntry(INetworkClient client)
		{
			await Send0X201C(client);

			var mapId = client.Session.Character.MapId;
			var worldBurst = await Database.GetMapWorldBurst(mapId) ?? await Database.GetMapWorldBurst(DefaultWorldMapId);
			if (worldBurst == null)
			{
				Logger.LogError("World entry (map={Map}, '{Name}'): no world burst available (not even the beach); the world cannot be built.", mapId, client.Session.Character?.Name);
				return;
			}

			Logger.LogInformation("World entry (map={Map}, '{Name}'): replaying {Count} IIR records from the DB ({Source}).", mapId, client.Session.Character?.Name, worldBurst.Entities.Count, worldBurst.MapId == mapId
				? "own map"
				: $"beach fallback (map {worldBurst.MapId})");
			// Populate the shared world model: the map's NPCs (once) + this player. Registered BEFORE the burst so
			// the player's own spawn body can be captured into the entity during the replay
			// (docs/archive/server-architecture-roadmap.md).
			await _broadcaster.EnsureZoneNpcs(mapId);
			_simulation.RegisterPlayerInWorld(client);
			await ReplayMapWorldBurst(client, worldBurst);

			// Now that the spawn body is captured, run interest: spawn this player to nearby players and them to it.
			await _broadcaster.BroadcastPlayerEntry(client);

			// Restore the player's persisted quest journal.
			await _broadcaster.RestorePlayerQuests(client);
		}

		// If the (already re-addressed) burst packet is an NPC entity Spawn (FA015807 whose handle is NOT the
		// player's instance) and a matching NpcSpawns row exists at the same spawn position, rebuild its body from
		// that row via NpcSpawnBuilder (addressed to the burst's own handle) and splice it in - keeping the burst
		// packet's re-addressed header + framing, exactly like the player-spawn rebuild. `wasIdentical` reports
		// whether the built body equals the burst's (true for same-source rows: the table reproduces the capture).
		// Falls back (returns false) when there is no matching row or the build fails, so the NPC keeps its burst body.
		// True if the burst packet is an NPC spawn (FA015807 whose handle is NOT the player's own) or an NPC MoveTask
		// (96C46740). Used by the FAOLAN_NPC_INDIVIDUAL_SPAWN path to drop NPC records from the burst so the interest
		// system is the sole NPC spawn path. The player's own FA015807 (handle == client instance) returns false.
		private static bool IsBurstNpcRecord(INetworkClient client, byte[] packet)
		{
			if (packet.Length < 12)
				return false;

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			if (bodyStart + 16 > packet.Length)
				return false;

			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4));
			if (subOpcode == MoveTaskBuilder.SubOpcode)
				return true; // 96C46740 NPC movement registration

			if (subOpcode != 0xFA015807u)
				return false;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 12, 4));
			return handle != client.Session.Account.ClientInstance; // an NPC spawn, not the player's own
		}

		private bool TryRebuildNpcSpawnFromDb(INetworkClient client, IReadOnlyDictionary<(int, int, int), NpcSpawn> npcByPosition, byte[] packet, bool keepCasildaChained, out byte[] rebuilt, out bool wasIdentical)
		{
			rebuilt = null;
			wasIdentical = false;
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + SpawnPacketBuilder.PosZOffset + 4 > packet.Length)
				return false;

			var body = packet.AsSpan(bodyStart);
			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
			var handle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12, 4));
			if (subOpcode != 0xFA015807u || handle == client.Session.Account.ClientInstance)
				return false; // not a spawn, or the player's own (handled by TryRebuildPlayerSpawnFromDb)

			var key = PositionKey(BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosZOffset, 4)));
			// NO freshSpawn here: the loading-screen burst MUST stay byte-exact to the capture (the proven path that
			// loads without crashing). freshSpawn re-encodes the body (drops the transient NpcStats.SpawnAnimation),
			// which shifts the IIR/body lengths for every NPC captured mid-animation - that re-shaped the burst and
			// crashed the client on the loading screen. SpawnAnimation normalization belongs ONLY on the from-model
			// spawn (EnsureZoneNpcs -> SendWorldSpawn), which is where Casilda actually streams in (she is outside
			// the burst), so reverting here keeps the Casilda fix while restoring the working loading screen.
			if (!npcByPosition.TryGetValue(key, out var row) || !NpcSpawnBuilder.TryBuild(row, handle, out var newBody))
				return false;

			// #184: pre-quest, keep Casilda VERBATIM (the burst's chained pose) instead of the unchained DB rebuild.
			if (keepCasildaChained && string.Equals(row.Name, CasildaName, StringComparison.Ordinal))
				return false;

			// Keep the burst packet's header (already re-addressed to the live player); swap only the FA015807 body.
			rebuilt = new byte[bodyStart + newBody.Length];
			Array.Copy(packet, 0, rebuilt, 0, bodyStart); // [len][crc][headerLen][header]
			newBody.CopyTo(rebuilt, bodyStart);

			// Splice the burst's EXACT transform group (body [20:66]) into the rebuild so the spawn position
			// byte-matches its paired 96C46740 MoveTask (a waypoint list keyed on that exact position). #185:
			// in-client verified 2026-06-14 that WITHOUT this the from-DB rebuild renders nowhere (the client silently
			// drops a spawn whose position diverges from its MoveTask, even by sub-0.1 float precision); WITH it, all
			// 23 cross-session beach NPCs render. This is what lets the from-DB body replace the verbatim burst packet:
			// the identity/scaffold come from the DB row, the transform stays the burst's (correct — same NPC, same
			// captured spot). FAOLAN_NPC_VERBATIM=1 restores the old verbatim-only behaviour. TODO #184: the row's IIR
			// also carries the spawn animation, so e.g. Casilda renders un-chained; carry the burst pose for full fidelity.
			if (Environment.GetEnvironmentVariable("FAOLAN_NPC_VERBATIM") != "1"
			    && packet.Length >= bodyStart + 66 && rebuilt.Length >= bodyStart + 66)
				Array.Copy(packet, bodyStart + 20, rebuilt, bodyStart + 20, 46);

			// wasIdentical = the (transform-spliced) rebuilt body equals the burst body (now just a stat/diagnostic).
			wasIdentical = packet.Length == rebuilt.Length && packet.AsSpan(bodyStart).SequenceEqual(rebuilt.AsSpan(bodyStart));

			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);
			return true;
		}

		// If the burst packet is the player's own entity Spawn (FA015807 whose handle is the client instance),
		// rebuild it from the DB character: transform (position from the DB, rotation kept), the
		// ProtoCoreCharacterIIR (name/sex/race/level/vitals + worn equipment) and the scaffold frame (from the
		// character's scaffold rows via the codec). Each step falls back to the captured bytes on mismatch.
		private bool TryRebuildPlayerSpawnFromDb(INetworkClient client, CharacterEquipment equipment, Character scaffoldCharacter, byte[] packet, out byte[] rebuilt)
		{
			rebuilt = null;

			// Full Conan packet: [len4][crc4][headerLen4][header][body]; the body starts after the header.
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + 16 > packet.Length)
				return false;

			// Body: [len4][subop4][c350 4][handle4]...
			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4));
			var handle = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 12, 4));
			if (subOpcode != 0xFA015807u || handle != client.Session.Account.ClientInstance)
				return false;

			var character = client.Session.Character;
			if (character == null)
				return false;

			// The transform's rotation is a ProtoQuat (X/Y/Z/W); we author it from the character's DB facing
			// (Character.Rotation, a quaternion - captured into the row by the importer, identity for fresh chars).
			// An earlier attempt stalled the client because the DB carried a DEGENERATE quaternion (W defaulted to
			// 0 -> a zero rotation the avatar can't realize); RotationW now defaults to 1, but as a belt-and-braces
			// guard a near-zero-magnitude stored quaternion falls back to the captured rotation rather than risk it.
			var body = packet[bodyStart..];
			var capturedRotation = new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotZOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotWOffset, 4)));
			var dbRotation = character.Rotation;
			var rotation = dbRotation.LengthSquared() > 0.0001f
				? dbRotation
				: capturedRotation;

			// Rebuild the body from the DB: position (fixed-length) then the character message. If the character has
			// fully-buildable worn gear, rebuild the whole CharacterIIR (name + the worn equipment as its
			// InventoryEntries) from the DB via the typed message; otherwise fall back to the proven name-only splice.
			var newBody = SpawnPacketBuilder.BuildSpawnBody(body, handle, character.Position, rotation);

			if (PlayerSpawnReader.TryReadCharacterMessage(newBody, out var read))
			{
				var message = read.Message;
				message.Name = character.Name;

				// Override the player's identity (model) from the DB when set; otherwise keep the captured stats
				// (a race/sex of 0 would be an invalid model). The remaining CharacterStats fields stay captured.
				if (message.Stats != null && (uint)character.Race != 0)
				{
					// CharacterStats.race == the Race enum (Aquilonian=1, Cimmerian=2, Stygian=3); the spawn's sex
					// code is the Sex enum + 2 (MALE=2 / FEMALE=3 - the editor's ChangeSex 4/6 halved, the same
					// 2x convention as race/class; anchored on Hoxxling, who is male: editor 4 -> wire 2 and a
					// chosen beardmesh).
					message.Stats.Sex = (uint)character.Sex + 2;
					message.Stats.Race = (uint)character.Race;
					message.Stats.Level = character.Level;

					// Vitals (cur==max) from the persisted MaxStats, on the ×100 wire scale (Strie's life 9000 = 90).
					var maxStats = character.MaxStats;
					if (maxStats != null)
					{
						message.Stats.Life = maxStats.Health * 100;
						message.Stats.Health = maxStats.Health * 100;
						message.Stats.MaxStamina = maxStats.Stamina * 100;
						message.Stats.Stamina = maxStats.Stamina * 100;
						message.Stats.MaxMana = maxStats.Mana * 100;
						message.Stats.Mana = maxStats.Mana * 100;
					}
					// Remaining CharacterStats fields (looks_package, side, ...) stay captured.
				}

				// The rendered looks from the editor choices (CharacterBody). The editor reports head mesh /
				// voice / size as 2x the wire value - the same convention as race/class - so: head_mesh =
				// ChangeHeadMesh/2, voice_set = ChangeVoice/2, monster_scale = ChangeSize/2 (verified on all
				// five create captures, e.g. dikketest 2285694 -> 1142847, voice 10 -> 5, size 194 -> 97).
				// Unset values (no editor choice) keep the captured field.
				if (message.Stats != null && character.Body != null)
				{
					if (character.Body.HeadMesh != 0)
						message.Stats.HeadMesh = character.Body.HeadMesh / 2;
					if (character.Body.Voice != 0)
						message.Stats.VoiceSet = character.Body.Voice / 2u;
					if (character.Body.Size != 0)
						message.Stats.MonsterScale = character.Body.Size / 2u;
				}

				// Replace the worn equipment with the character's own (when it has fully-buildable gear).
				var wornEquipment = PlayerSpawnEquipment.BuildInventoryEntries(equipment);
				if (wornEquipment.Count > 0)
				{
					message.InventoryEntries.Clear();
					foreach (var entry in wornEquipment)
						message.InventoryEntries.Add(entry);
				}

				newBody = SpawnPacketBuilder.SetCharacterMessage(newBody, message);

				// Regenerate the scaffold frame (pre-IIR islands + entry table + feat-ID list + pair lists) from
				// the character's own DB rows, replacing the captured player's scaffold. No-op fallback when the
				// body doesn't parse or the character has no rows.
				newBody = RebuildScaffoldFromRows(newBody, scaffoldCharacter);
			}
			// else: no decodable ProtoCoreCharacterIIR in the body (a malformed / non-standard spawn). There is no
			// structural way to rename it, and the old fixed-Strie-offset SetSpawnName fallback could only have fired
			// on a body whose name signature TryReadCharacterMessage (which scans every 0x0A) would already have
			// found - so it was dead in practice (#201). Ship the body with its captured identity rather than
			// raw-patch a malformed one.

			rebuilt = new byte[bodyStart + newBody.Length];
			Array.Copy(packet, 0, rebuilt, 0, bodyStart); // [len][crc][headerLen][header]
			newBody.CopyTo(rebuilt, bodyStart);

			// Set the outer packet length (total - 4) and recompute the CRC over everything after it; we send via
			// raw client.SendAsync (not PacketStream), so both are done in-place, big-endian like the framing.
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);

			Logger.LogInformation("Rebuilt player spawn from DB: handle=0x{Handle:X}, name='{Name}', pos=({X},{Y},{Z}).", handle, character.Name, character.Position.X, character.Position.Y, character.Position.Z);
			return true;
		}

		// As TryRebuildNpcSpawnFromDb, for the static world objects (66AEDD50/F096251E/33A56FB0). Matched by
		// position, rebuilt with the burst's handle via WorldObjectBuilder; `wasIdentical` is the safety signal.
		private bool TryRebuildWorldObjectFromDb(IReadOnlyDictionary<(int, int, int), WorldObject> objectByPosition, byte[] packet, out byte[] rebuilt, out bool wasIdentical)
		{
			rebuilt = null;
			wasIdentical = false;
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + SpawnPacketBuilder.PosZOffset + 4 > packet.Length)
				return false;

			var body = packet.AsSpan(bodyStart);
			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
			if (subOpcode is not (0x66AEDD50u or 0xF096251Eu or 0x33A56FB0u or 0x6BD56736u or 0x1FD6412Bu))
				return false; // decor/container/zone + destructible (6BD56736) + climbing dynel (1FD6412B)

			var key = PositionKey(BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.Slice(SpawnPacketBuilder.PosZOffset, 4)));
			// Splice the DB object identity onto the LIVE placement (handle/transform/playfield from the burst body),
			// so the per-session playfield + handle stay correct and only the object definition comes from the table.
			if (!objectByPosition.TryGetValue(key, out var row) || !WorldObjectBuilder.TryBuildOntoLivePlacement(row, body, out var newBody))
				return false;

			wasIdentical = packet.Length - bodyStart == newBody.Length && packet
				.AsSpan(bodyStart)
				.SequenceEqual(newBody);

			rebuilt = new byte[bodyStart + newBody.Length];
			Array.Copy(packet, 0, rebuilt, 0, bodyStart);
			newBody.CopyTo(rebuilt, bodyStart);

			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
			var crc = Crc32.Calculate(rebuilt[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), crc);
			return true;
		}

		// Encodes value as a base-128 varint of EXACTLY byteLength bytes, padding the high groups with overlong
		// (non-minimal) continuation bytes when value needs fewer. Overlong varints are valid - a decoder reads the
		// continuation bytes and reconstructs the same value - so this keeps a varint substitution length-preserving
		// when the captured id encodes to more bytes than the live id. Returns null if value does not fit in
		// byteLength*7 bits (the live id can't be made to fit the captured slot without shifting later bytes).
		internal static byte[] EncodeVarintPadded(uint value, int byteLength)
		{
			if (byteLength is < 1 or > 5)
				return null;

			var bytes = new byte[byteLength];
			var v = value;
			for (var i = 0; i < byteLength; i++)
			{
				bytes[i] = (byte)((v & 0x7F) | (i < byteLength - 1
					? 0x80u
					: 0u));
				v >>= 7;
			}

			return v != 0
				? null // value didn't fit in byteLength 7-bit groups
				: bytes;
		}

		// Captures the first live-addressed NPC spawn (FA015807, non-player handle) and MoveTask (96C46740) packets
		// as framing templates for the additive built-NPC test (their header carries the live receiver/instance).
		private static void CaptureInjectionTemplate(INetworkClient client, byte[] packet, ref byte[] spawnTemplate, ref byte[] moveTaskTemplate)
		{
			if (packet.Length < 12)
				return;

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			if (bodyStart + 16 > packet.Length)
				return;

			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4));
			var handle = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 12, 4));
			if (spawnTemplate == null && subOpcode == 0xFA015807u && handle != client.Session.Account.ClientInstance)
				spawnTemplate = packet;
			else if (moveTaskTemplate == null && subOpcode == MoveTaskBuilder.SubOpcode)
				moveTaskTemplate = packet;
		}

		// Frames a built body as a full Conan packet by reusing a template packet's header (its live receiver/
		// instance), then setting the packet length and recomputing the CRC over everything after it.
		private static byte[] FrameLikeTemplate(byte[] template, byte[] body)
		{
			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(8, 4));
			var packet = new byte[bodyStart + body.Length];
			Array.Copy(template, 0, packet, 0, bodyStart); // [len][crc][headerLen][header]
			body.CopyTo(packet, bodyStart);

			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)(packet.Length - 4));
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), Crc32.Calculate(packet[8..]));
			return packet;
		}

		// True if the packet is the 0x2000 inventory snapshot (sub-opcode 0x737A6DF9).
		private static bool IsInventorySnapshot(byte[] packet)
		{
			if (packet.Length < 12)
				return false;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart + 8 <= packet.Length && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4)) == 0x737A6DF9u;
		}

		// Quantizes a world position to cm (0.01) so a burst spawn matches its NpcSpawns row across float
		// round-trips; collisions (two NPCs within a cm) are not a concern at beach scale.
		private static (int, int, int) PositionKey(float x, float y, float z)
		{
			return ((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), (int)MathF.Round(z * 100f));
		}

		// The entity name is the first `0x0A <len 1..63> <printable-ascii>` run in the spawn body.
		private static string ReadSpawnName(ReadOnlySpan<byte> body)
		{
			for (var i = 16; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A)
					continue;

				int len = body[i + 1];
				if (len is < 1 or > 63 || i + 2 + len > body.Length)
					continue;

				var ok = true;
				for (var j = 0; j < len; j++)
				{
					var c = body[i + 2 + j];
					if (c is < 0x20 or > 0x7E)
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return Encoding.ASCII.GetString(body.Slice(i + 2, len));
			}

			return null;
		}

		// Records a world-burst spawn's handle -> NPC name into the session map (for conversation routing). No-op for
		// non-FA015807 packets and the player's own spawn (handle == client instance).
		private static void RecordSpawnedNpcName(INetworkClient client, byte[] packet)
		{
			if (packet.Length < 12)
				return;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart + 16 > packet.Length)
				return;

			var body = packet.AsSpan(bodyStart);
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != 0xFA015807u)
				return;

			var handle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12, 4));
			if (handle == client.Session.Account.ClientInstance)
				return; // the player's own spawn

			var name = ReadSpawnName(body);
			if (name != null)
				client.Session.SpawnedNpcNamesByHandle[handle] = name;

			// Record the NPC's real max health from its spawn CharacterStats (health, else life; both ×100 on the
			// wire), so combat seeds its vitals from this instead of the flat DefaultNpcMaxHealth placeholder - the
			// bar then moves relative to the HP the client actually rendered from this spawn.
			if (PlayerSpawnReader.TryReadCharacterMessage(packet[bodyStart..], out var read) && read.Message.Stats is { } stats)
			{
				var wireHealth = stats.Health != 0
					? stats.Health
					: stats.Life;
				if (wireHealth >= 100) // ×100 scale; ignore implausibly small/absent values
					client.Session.SpawnedNpcMaxHealthByHandle[handle] = wireHealth / 100;
			}
		}

		// Replaces every non-overlapping occurrence of find with repl (equal length) in buffer, after the 8-byte
		// length+crc prefix.
		private static void ReplaceAll(byte[] buffer, byte[] find, byte[] repl)
		{
			for (var i = 8; i + find.Length <= buffer.Length; i++)
			{
				var match = true;
				for (var j = 0; j < find.Length; j++)
				{
					if (buffer[i + j] != find[j])
					{
						match = false;
						break;
					}
				}

				if (match)
				{
					repl.CopyTo(buffer, i);
					i += find.Length - 1;
				}
			}
		}

		// SendPostReadyBurst (the post-0x2024 login record stream) is built as explicit PacketStream sends
		// in PostReadyBurst.cs.

		// Re-addresses every reference to the captured Strie instance (0x02165EFF) in a burst packet to the live
		// character instance: the receiver-conversation varint (header) and the inventory nested varints appear as
		// varint(0x02165EFF) = FF BD D9 10; entity handles / body fields appear as the raw uint32 02 16 5E FF. The
		// raw form is always 4 bytes; the live id's varint is padded (overlong) to the captured varint's byte length
		// so the substitution stays length-preserving even for maps whose captured instance encodes to more bytes
		// (1000/11040 captured a 5-byte instance like 0x110099BD). We then recompute the CRC. (This is the interim
		// burst-replay path; construct-don't-replay supersedes it per-entity, but this is still the active fallback.)
		private static byte[] RewriteBurstInstance(byte[] packet, uint oldInstance, uint newInstance)
		{
			if (oldInstance == newInstance)
				return packet;

			var oldVarint = Varint.Encode(oldInstance);
			var newVarint = EncodeVarintPadded(newInstance, oldVarint.Length);
			if (newVarint == null)
				return packet; // live id needs more varint bytes than the captured id; can't substitute in place

			var oldRaw = new byte[4];
			var newRaw = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(oldRaw, oldInstance);
			BinaryPrimitives.WriteUInt32BigEndian(newRaw, newInstance);

			var p = (byte[])packet.Clone();
			ReplaceAll(p, oldVarint, newVarint);
			ReplaceAll(p, oldRaw, newRaw);

			var crc = Crc32.Calculate(p[8..]);
			BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4, 4), crc);
			return p;
		}
	}
}
