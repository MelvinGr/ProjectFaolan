using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Faolan.GameServer.Spawn;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The one beach NPC whose SpawnCheck answer carries status 0x02 (it is Casilda / the intro NPC, handle
		// 0x00081906 in the capture); every other polled entity gets status 0x00. This is an NPC handle from the
		// burst, NOT the player (the client never polls its own handle), so it is unchanged across characters.
		private const uint SpawnCheckStatusTwoHandle = 0x00081906;
		private const uint StarterFirstBagItem = 3774542; // Royal Signet (first bag slot)

		// Default starter gear for beach characters (item ids that are fully buildable: p_items identity + harvested
		// archetype), keyed by the CharacterEquipment slot they go in. Slot-appropriate so the client accepts them.
		private const uint StarterWristItem = 3361557; // Slave Manacles (wrist, 0x12)

		private static readonly ulong[] EquippedSlotIds = [0x01, 0x02, 0x03, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0e, 0x0f, 0x10, 0x12, 0x14, 0x15];

		// Applies the DB inventory to the parsed snapshot: replaces each captured Equipped entry whose slot the
		// character has a buildable item for, plus the first Bag entry from FirstPosBag - all IN PLACE (preserving the
		// captured entry's itemId/record id, so the entry count and the tails are unchanged).
		private void ApplyDbInventory(ProtoMessage root, DbInventory dbInventory)
		{
			var equipped = InventorySnapshotInspector.GetContainer(root, InventorySnapshotInspector.ContainerKind.Equipped);
			foreach (var entry in InventorySnapshotInspector.GetContainerEntries(equipped).ToList())
			{
				var slot = InventorySnapshotInspector.GetEntrySlot(entry) ?? 0;
				if (dbInventory.Equipped.TryGetValue(slot, out var resolved))
					ReplaceEntryInPlace(equipped, entry, resolved.Item, resolved.Archetype);
			}

			if (dbInventory.BagFirst.HasValue)
			{
				var bag = InventorySnapshotInspector.GetContainer(root, InventorySnapshotInspector.ContainerKind.Bag);
				var firstBag = InventorySnapshotInspector.GetContainerEntries(bag).FirstOrDefault();
				if (firstBag != null)
					ReplaceEntryInPlace(bag, firstBag, dbInventory.BagFirst.Value.Item, dbInventory.BagFirst.Value.Archetype, 1);
			}
		}

		// Parses the (already instance-substituted) inventory snapshot, applies the current inventory experiment,
		// and rebuilds it into a raw packet. IMPORTANT: built entirely with manual byte assembly + manual CRC and
		// the ORIGINAL packet's header copied verbatim - NOT via PacketStream/WriteLengthHash. An earlier
		// PacketStream-based rebuild stalled the client on the loading screen even though its output was byte-exact
		// (same class of issue as the Phase 1 player-spawn rebuild), whereas the manual byte path works.
		private byte[] BuildBeachInventoryPacket(byte[] substitutedPacket, DbInventory dbInventory, Character character)
		{
			var conanPacket = new ConanPacket(substitutedPacket);
			var body = new FaolanStream(conanPacket.Body.ReadArrayUInt32Length());

			var subOpcode = body.ReadUInt32();
			var c350 = body.ReadUInt32();
			var instance = body.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			// Patch the LIVE character's level + current vitals into the tails (the captured player's values
			// otherwise ride along; tail stat edits are proven safe in-client). Full vitals at login; the
			// 0x96B8DC59 vitals update after the spawn sets the max side. Best-effort: a tails layout this
			// model can't index just keeps the captured values.
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

			// GENERATE the four inventory containers from the character's DB items (typed ProtoFullCharacterIIR,
			// proven byte-faithful by the 37/39 round-trip oracle), replacing the captured player's containers
			// wholesale - entry count included (real-identity items are accepted in-client; add proven by the
			// Royal Signet test). The prefix ([u32 X][u8 Y] + the root's leading character_tag/flags fields) and
			// the tails stay captured. Falls back to the older in-place entry swap if the captured root does not
			// decode typed.
			var rootBytes = parts.Root.ToArray();
			try
			{
				var typedRoot = ((ReadOnlySpan<byte>)rootBytes).FromProtobuf<ProtoFullCharacterIIR>();
				typedRoot.Inventories.Clear();
				foreach (var container in FullCharSnapshotBuilder.BuildContainers(instance, dbInventory.Equipped, dbInventory.BagFirst, dbInventory.Rows))
					typedRoot.Inventories.Add(container);
				rootBytes = typedRoot.ToProtobufBytes();
				Logger.LogInformation("Inventory containers GENERATED from DB: {Equipped} equipped + {Bag} bag item(s).", dbInventory.Equipped.Count, dbInventory.BagFirst.HasValue ? 1 : 0);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Typed container generation failed; falling back to the in-place entry swap.");
				ApplyDbInventory(parts.Root, dbInventory);
				rootBytes = parts.Root.ToArray();
			}

			// Body content: [subop][c350][instance][prefix][root][tails], then prefixed by its uint32 length.
			using var contentStream = new FaolanStream();
			contentStream.WriteUInt32(subOpcode);
			contentStream.WriteUInt32(c350);
			contentStream.WriteUInt32(instance);
			contentStream.WriteArray(parts.Prefix);
			contentStream.WriteArray(rootBytes);
			contentStream.WriteArray(parts.Tails.ToArray());
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

			// The swap changes the one item's bytes, so the rebuilt packet legitimately differs from the verbatim
			// input here (it is no longer a no-op). The parse/re-serialize machinery itself is proven lossless by
			// InventorySnapshotTests; this just logs the size delta for visibility.
			Logger.LogInformation("Inventory DB-swap rebuilt: verbatim={VerbatimLen} -> rebuilt={RebuiltLen} bytes.", substitutedPacket.Length, rebuilt.Length);

			return rebuilt;
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
					var registry = await Database.GetMorphTargets(scaffoldCharacter.BodyResources.Select(r => r.Ref).ToArray());
					weighted = PlayerSpawnScaffoldRows.BuildWeightedEntries(scaffoldCharacter.BodyResources, registry);
					if (weighted == null)
						Logger.LogWarning("Character {CharacterId} has {Count} chosen appearance resources but none resolved via MorphTargets (registry imported?); using the default weighted entries.", character.Id,
							scaffoldCharacter.BodyResources.Count);
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

		// Replaces one container entry with a DB-built entry for `item`, preserving the original slot/itemId/record id
		// (and count unless forced) so the surrounding structure and the tails stay byte-stable.
		private void ReplaceEntryInPlace(ProtoMessage container, ProtoField original, Item item, uint archetype, ulong? forceCount = null)
		{
			var slot = InventorySnapshotInspector.GetEntrySlot(original) ?? 0;
			var itemId = InventorySnapshotInspector.GetEntryItemId(original) ?? 0;
			var recordId = InventorySnapshotInspector.GetEntryRecordId(original) ?? 0;
			var count = forceCount ?? InventorySnapshotInspector.GetEntryCount(original) ?? 1;

			var built = SnapshotItemBuilder.BuildEntry(slot, itemId, recordId, count, item, archetype);
			container.Fields[container.Fields.IndexOf(original)] = built;

			Logger.LogInformation("Inventory: slot {Slot} -> DB item {Name} (template {Template}, archetype {Archetype}), keeping itemId {ItemId}/record {RecordId}/count {Count}.", slot, item.Name, item.Id, archetype, itemId,
				recordId, count);
		}

		// Tortage Beach (0xFAA/4010) - the universal fallback world. Its burst is always seeded (the proven blob), so
		// any character whose own map has no usable burst still enters a real, rendered world instead of a hand-built
		// one. (There is no hand-built login/teleport path anymore.)
		private const uint DefaultWorldMapId = 0x00000FAA;

		// The single existing-character WORLD ENTRY used by both login and the teleport GM command: send the 0x201c
		// channel handshake, then replay the character's current-map world burst from the DB (MapChange + world
		// objects + NPC spawns + the player spawn + the inventory snapshot, all re-addressed and DB-driven). Falls
		// back to the beach burst for any map without a usable burst. The caller has already set Character.MapId/Pos.
		private async Task SendWorldEntry(INetworkClient client)
		{
			await Send0X201C(client);

			var mapId = client.Session.Character.MapId ?? 0;
			var worldBurst = await Database.GetMapWorldBurst(mapId) ?? await Database.GetMapWorldBurst(DefaultWorldMapId);
			if (worldBurst == null)
			{
				Logger.LogError("World entry (map={Map}, '{Name}'): no world burst available (not even the beach); the world cannot be built.", mapId, client.Session.Character?.Name);
				return;
			}

			Logger.LogInformation("World entry (map={Map}, '{Name}'): replaying {Count} IIR records from the DB ({Source}).", mapId, client.Session.Character?.Name, worldBurst.Entities.Count,
				worldBurst.MapId == mapId ? "own map" : $"beach fallback (map {worldBurst.MapId})");
			await ReplayMapWorldBurst(client, worldBurst);
		}

		// Replays a map's captured world burst (from the DB), re-addressed from the burst's captured instance to the
		// logged-in character. Works for ANY map that has a stored burst (beach included - its burst is the proven
		// blob, seeded into the DB by Faolan.Importer).
		private async Task ReplayMapWorldBurst(INetworkClient client, MapWorldBurst burst)
		{
			// Rebuild the exact original packet bytes from the structured IIR records (IirRecordCodec is round-trip
			// lossless), then drive the same proven byte-preserving replay (instance rewrite + inventory/spawn rebuild).
			var records = burst.Entities.OrderBy(e => e.Sequence).ToList();
			var packets = records.Select(IirRecordCodec.Rebuild).ToList();
			var capturedInstance = burst.CapturedInstance;
			var liveInstance = client.Session.Account.ClientInstance;
			Logger.LogInformation("Replaying world burst for map {Map} ({Count} packets, instance {Old:X8} -> {New:X8}).", burst.MapId, packets.Count, capturedInstance, liveInstance);

			client.Session.UsedWorldBurstReplay = true;

			// Phase 5b: drive the inventory from the character's CharacterEquipment. Beach characters are seeded with
			// default starter gear (if they have none), then every captured inventory entry whose slot the character
			// has a fully-buildable DB item for (real p_items identity + harvested archetype) is REPLACED IN PLACE -
			// same slot/itemId/recordId, so the entry count and the tails stay byte-stable (the safe path proven in
			// client). Resolved once here (async) so the send loop stays tight. Empty => inventory sent verbatim.
			var equipment = await EnsureBeachStarterEquipment(client.Session.Character);
			var inventoryRows = client.Session.Character != null
				? await Database.GetCharacterInventory(client.Session.Character.Id)
				: [];
			var dbInventory = ResolveDbInventory(equipment, inventoryRows);

			// The spawn-scaffold rows (entry table / feat-ID list / pre-IIR frame) drive the player spawn's
			// scaffold frame; characters without rows are seeded with the fresh-character defaults once.
			var scaffoldCharacter = await EnsureSpawnScaffoldRows(client.Session.Character);

			// IMPORTANT: do ALL per-packet work (instance rewrite, player-spawn rebuild, inventory parse/rebuild)
			// HERE, before the send loop. The inventory rebuild in particular parses ~9 KB of tails and re-serializes
			// - doing that mid-loop adds a CPU stall between sends that reshapes the burst's TCP segmentation, and
			// the client (no reassembly on the uncompressed download) then misframes and hangs on the loading
			// screen. Pre-building keeps the actual send loop tight and uniform.
			var toSend = new List<byte[]>(packets.Count + 2);

			// MapChange must lead the world build. It's a global record the server always generates from the live
			// map/instance - it is never stored in the burst - so emit it first.
			toSend.Add(BuildMapChange(client));

			foreach (var packet in packets)
			{
				// Re-address the capture's instance to the logged-in character so the burst works for any character.
				// The NPC handles in the burst are left as-is.
				var pkt = RewriteBurstInstance(packet, capturedInstance, liveInstance);

				if (dbInventory.Count > 0 && IsInventorySnapshot(pkt))
					pkt = BuildBeachInventoryPacket(pkt, dbInventory, client.Session.Character);
				else if (TryRebuildPlayerSpawnFromDb(client, equipment, scaffoldCharacter, pkt, out var rebuilt))
					pkt = rebuilt;

				// Record each spawned NPC's handle -> name so a clicked handle can be routed to its conversation by
				// the stable name (handles are session-volatile). Reads the final bytes the client receives.
				RecordSpawnedNpcName(client, pkt);

				toSend.Add(pkt);
			}

			// The player's own vitals are server-generated from the live character (not the captured player's), emitted
			// after the player has spawned in the burst.
			toSend.Add(BuildVitals(client));

			// GameTime (the world clock) is a global record the server always generates - never stored in the burst.
			toSend.Add(BuildGameTime(client));

			// Tight send loop - no per-packet computation here.
			foreach (var bytes in toSend)
				await client.SendAsync(bytes);
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
					return System.Text.Encoding.ASCII.GetString(body.Slice(i + 2, len));
			}

			return null;
		}

		// Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: body is
		// 0x0000C350 + the polled entity handle + a status byte (0x02 for the player's own avatar, else 0x00).
		private Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			var status = (byte)(handle == SpawnCheckStatusTwoHandle
				? 0x02
				: 0x00);

			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.SpawnCheck).WriteUInt32(0x0000C350).WriteUInt32(handle).WriteByte(status).SendAsync(client);
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

			// Position only for now. Rebuilding the rotation from the DB stalled the client on the loading screen
			// (the player avatar spawn was rejected), and existing characters have unreliable rotation data anyway,
			// so the captured rotation is kept verbatim until DB rotation is trustworthy and verified safe.
			var body = packet[bodyStart..];
			var keptRotation = new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotXOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotYOffset, 4)),
				BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotZOffset, 4)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(SpawnPacketBuilder.RotWOffset, 4)));

			// Rebuild the body from the DB: position (fixed-length) then the character message. If the character has
			// fully-buildable worn gear, rebuild the whole CharacterIIR (name + the worn equipment as its
			// InventoryEntries) from the DB via the typed message; otherwise fall back to the proven name-only splice.
			var newBody = SpawnPacketBuilder.BuildSpawnBody(body, handle, character.Position, keptRotation);

			if (PlayerSpawnReader.TryReadCharacterMessage(newBody, out var read))
			{
				var message = read.Message;
				message.Name = character.Name;

				// Override the player's identity (model) from the DB when set; otherwise keep the captured stats
				// (a race/sex of 0 would be an invalid model). The remaining CharacterStats fields stay captured.
				if (message.Stats != null && (uint)character.Race != 0)
				{
					// CharacterStats.race == the Race enum (Aquilonian=1, Cimmerian=2, Stygian=3); the spawn's sex code
					// is the Sex enum + 2 (Male=2, Female=3, both in the captured-valid range).
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
			else
			{
				newBody = SpawnPacketBuilder.SetSpawnName(newBody, character.Name);
			}

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

		// Maps an equipped-container slot id (matching the archive player_items/npc_items slot bytes) to the matching
		// CharacterEquipment item nav. 0x11 (first_pos_bag) is handled separately as the bag's first slot.
		private static Item EquippedSlotItem(CharacterEquipment e, ulong slot)
		{
			return slot switch
			{
				0x01 => e.Neck,
				0x02 => e.Head,
				0x03 => e.Tokens,
				0x05 => e.Chest,
				0x06 => e.MainHandLeft,
				0x07 => e.Belt,
				0x08 => e.OffHandLeft,
				0x09 => e.RingLeft,
				0x0a => e.RingRight,
				0x0b => e.Cloak,
				0x0c => e.Shoulder,
				0x0e => e.Boots,
				0x0f => e.Hands,
				0x10 => e.Legs,
				0x12 => e.Wrist,
				0x14 => e.MainHandRight,
				0x15 => e.OffHandRight,
				_    => null
			};
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

		// Resolves which CharacterEquipment slots hold a fully-buildable item (template in Items + archetype harvested).
		private static DbInventory ResolveDbInventory(CharacterEquipment equipment, CharacterInventory[] inventoryRows)
		{
			var inventory = new DbInventory
			{
				Rows = inventoryRows ?? []
			};
			if (equipment == null)
				return inventory;

			foreach (var slot in EquippedSlotIds)
			{
				var item = EquippedSlotItem(equipment, slot);
				if (item != null && item.Archetype != 0)
					inventory.Equipped[slot] = (item, item.Archetype);
			}

			if (equipment.FirstPosBag is { Archetype: not 0 })
				inventory.BagFirst = (equipment.FirstPosBag, equipment.FirstPosBag.Archetype);

			return inventory;
		}

		// Re-addresses every reference to the captured Strie instance (0x02165EFF) in a burst packet to the live
		// character instance: the receiver-conversation varint (header) and the inventory nested varints appear as
		// varint(0x02165EFF) = FF BD D9 10; entity handles / body fields appear as the raw uint32 02 16 5E FF. The
		// raw form is always 4 bytes; the live id's varint is padded (overlong) to the captured varint's byte length
		// so the substitution stays length-preserving even for maps whose captured instance encodes to more bytes
		// (1000/11040 captured a 5-byte instance like 0x110099BD). We then recompute the CRC.
		[Obsolete]
		private static byte[] RewriteBurstInstance(byte[] packet, uint oldInstance, uint newInstance)
		{
			if (oldInstance == newInstance)
				return packet;

			var oldVarint = EncodeVarint(oldInstance);
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

		// The character's DB-resolved inventory: which equipped-container slot ids (and the first bag slot) have a
		// fully-buildable item (real p_items identity + harvested archetype). Only these get swapped in; everything
		// else stays verbatim.
		private sealed class DbInventory
		{
			public readonly Dictionary<ulong, (Item Item, uint Archetype)> Equipped = new Dictionary<ulong, (Item Item, uint Archetype)>();
			public (Item Item, uint Archetype)? BagFirst;

			// Persisted CharacterInventory rows (bag cells from unequips; resource/quest once written).
			public CharacterInventory[] Rows = [];

			public int Count =>
				Equipped.Count + Rows.Length + (BagFirst.HasValue
					? 1
					: 0);
		}
	}
}
