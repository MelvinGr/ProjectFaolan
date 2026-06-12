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

		// Default starter gear for beach characters (item ids that are fully buildable: p_items identity + harvested
		// archetype), keyed by the CharacterEquipment slot they go in. Slot-appropriate so the client accepts them.
		private const uint StarterWristItem = 3361557; // Slave Manacles (wrist, 0x12)

		// Rebuilds the (already instance-substituted) world-entry inventory snapshot (0x737A6DF9), patching the LIVE
		// character's level/vitals into the tails while keeping the captured inventory CONTAINERS verbatim.
		//
		// We deliberately do NOT substitute the character's DB items into this snapshot. Doing so - by ANY method,
		// in-place entry swap OR wholesale typed-container generation - stalls the client's world-load completion:
		// the client tries to resolve each item's RDB resources, thrashes ("Out of scratch memories") on items it
		// can't find locally, and never reaches the state where it dials the chat/CommunityServer. The result is the
		// chat-channels regression (AgentServer gets zero TCP accepts). Verified by bisecting the live client: any
		// container mutation breaks chat; verbatim containers + this tail patch keep it. The player's VISIBLE worn
		// gear is DB-driven via the entity spawn (TryRebuildPlayerSpawnFromDb), which is chat-safe. Delivering the
		// real backpack inventory must happen AFTER world load (a post-ready inventory update), not in this snapshot.
		// See [[protocol_chat_connection_regression]].
		//
		// IMPORTANT: built entirely with manual byte assembly + manual CRC and the ORIGINAL packet's header copied
		// verbatim - NOT via PacketStream/WriteLengthHash. An earlier PacketStream-based rebuild stalled the client
		// on the loading screen even though its output was byte-exact, whereas the manual byte path works.
		private byte[] BuildBeachInventoryPacket(byte[] substitutedPacket, Character character)
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
			// InventorySnapshotTests). No DB item substitution here (see the method header).
			var rootBytes = parts.Root.ToArray();

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

			return rebuilt;
		}

		// Equipped-container slot id -> the CharacterEquipment nav (archive player_items slot bytes).
		private static readonly (ulong Slot, Func<CharacterEquipment, Item> Get)[] InventoryEquippedSlots =
		[
			(0x01, e => e.Neck), (0x02, e => e.Head), (0x03, e => e.Tokens), (0x05, e => e.Chest), (0x06, e => e.MainHandLeft), (0x07, e => e.Belt), (0x08, e => e.OffHandLeft), (0x09, e => e.RingLeft),
			(0x0a, e => e.RingRight), (0x0b, e => e.Cloak), (0x0c, e => e.Shoulder), (0x0e, e => e.Boots), (0x0f, e => e.Hands), (0x10, e => e.Legs), (0x12, e => e.Wrist), (0x14, e => e.MainHandRight), (0x15, e => e.OffHandRight)
		];

		// Builds the player's DB inventory snapshot (0x737A6DF9) from the verbatim world-entry snapshot packet:
		// GENERATES the four containers from the character's DB items (equipped + first bag + persisted bag rows),
		// keeping the captured prefix + tails (with the live level/vitals patch). This is the SAME generation that
		// stalls the client when sent DURING world load - it is only ever sent AFTER the post-ready burst (post-load),
		// when the client is out of the constrained load phase, so it updates the bag without risking the chat gate.
		// Returns null if it can't be built (then nothing is sent and the verbatim snapshot stands).
		private byte[] BuildDbInventorySnapshot(byte[] verbatimPacket, Character character, CharacterEquipment equipment, CharacterInventory[] inventoryRows)
		{
			if (equipment == null)
				return null;

			try
			{
				var conanPacket = new ConanPacket(verbatimPacket);
				var body = new FaolanStream(conanPacket.Body.ReadArrayUInt32Length());
				var subOpcode = body.ReadUInt32();
				var c350 = body.ReadUInt32();
				var instance = body.ReadUInt32();
				var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

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

				var verbatimRoot = parts.Root.ToArray();

				var typedRoot = ((ReadOnlySpan<byte>)verbatimRoot).FromProtobuf<ProtoFullCharacterIIR>();
				typedRoot.Inventories.Clear();
				foreach (var container in FullCharSnapshotBuilder.BuildContainers(instance, equipped, bagFirst, inventoryRows ?? []))
					typedRoot.Inventories.Add(container);
				var rootBytes = typedRoot.ToProtobufBytes();

				// The client reads the FullCharacterIIR protobuf root by a length the snapshot carries in its 8-byte
				// IIR prefix: a varint at prefix offset 4 (after the 4-byte base-class version) equals
				// constant + rootLength (proven across two captures: prefix byte tracks the root length 1:1, delta 5
				// = root delta 5). Kept verbatim it stays the CAPTURED root's length, so a regenerated root of a
				// different size desyncs the client - it over/under-reads the protobuf and misframes the following
				// subclass version ("ReadSubClass Wrong IIR_t version ..."). Delta-adjust that varint by the
				// root-length change, exactly as SetCharacterMessage delta-adjusts the spawn's CharacterIIR length.
				var prefix = AdjustSnapshotPrefixRootLength(parts.Prefix, rootBytes.Length - verbatimRoot.Length);
				var tailsBytes = parts.Tails.ToArray();

				// FROM-SCRATCH beach path (default on; opt out with FAOLAN_SCRATCH_BEACH_SNAPSHOT=0): on the beach,
				// build the prefix and tails from the ground up - the prefix from BeachSnapshotPrefix (constants +
				// root length, no captured prefix / no delta-adjust) and the tails from the server-owned
				// BeachTailsBuilder template (level + vitals generated, instance re-addressed) - instead of carrying
				// the captured burst's prefix/tails. Both are proven byte-identical to the capture
				// (BeachSnapshotConstructionTests) and construct in-client with 0 IIR errors. Gated to the beach map;
				// other maps keep the burst prefix/tails + delta-adjust. See [[snapshot_tails_construction_scope]].
				if (Environment.GetEnvironmentVariable("FAOLAN_SCRATCH_BEACH_SNAPSHOT") != "0" && character?.MapId == DefaultWorldMapId && BeachTailsBuilder.IsAvailable)
				{
					prefix = BeachSnapshotPrefix.Build(rootBytes.Length);
					tailsBytes = BeachTailsBuilder.Build(character, instance);
					Logger.LogInformation("From-scratch beach snapshot: built prefix ({Prefix} b) + server-owned tails ({Tails} b).", prefix.Length, tailsBytes.Length);
				}

				using var contentStream = new FaolanStream();
				contentStream.WriteUInt32(subOpcode);
				contentStream.WriteUInt32(c350);
				contentStream.WriteUInt32(instance);
				contentStream.WriteArray(prefix);
				contentStream.WriteArray(rootBytes);
				contentStream.WriteArray(tailsBytes);
				var bodyContent = contentStream.ToArray();

				var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(verbatimPacket.AsSpan(8, 4));
				var bodyStart = 12 + headerLen;
				var rebuilt = new byte[bodyStart + 4 + bodyContent.Length];
				Array.Copy(verbatimPacket, 0, rebuilt, 0, bodyStart);
				BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(bodyStart, 4), (uint)bodyContent.Length);
				bodyContent.CopyTo(rebuilt, bodyStart + 4);
				BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(0, 4), (uint)(rebuilt.Length - 4));
				BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(4, 4), Crc32.Calculate(rebuilt[8..]));
				Logger.LogInformation("Post-load DB inventory snapshot built: {Equipped} equipped + {Bag} bag-first + {Rows} rows.", equipped.Count, bagFirst.HasValue
					? 1
					: 0, (inventoryRows ?? []).Length);
				return rebuilt;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Post-load DB inventory snapshot build failed; backpack stays verbatim.");
				return null;
			}
		}

		// Delta-adjusts the snapshot IIR prefix's embedded root-length varint by `delta`. The prefix is
		// [4-byte base-class version][varint = const + rootLength][trailing varint(s)]; the client uses that
		// varint to delimit the protobuf root from the following subclass (the tails). Reads the varint at
		// offset 4, adds delta, and re-encodes in place (variable width), preserving the bytes around it.
		// Returns the prefix unchanged when delta is 0 or the layout isn't the known 8-byte shape.
		internal static byte[] AdjustSnapshotPrefixRootLength(byte[] prefix, int delta)
		{
			const int varintOffset = 4;
			if (delta == 0 || prefix.Length < varintOffset + 2)
				return prefix;

			// Read the varint at offset 4.
			ulong value = 0;
			var shift = 0;
			var end = varintOffset;
			while (end < prefix.Length)
			{
				var b = prefix[end++];
				value |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					break;

				shift += 7;
			}

			var adjusted = (long)value + delta;
			if (adjusted < 0)
				return prefix;

			// Re-encode and splice: [0..offset) + varint(adjusted) + [end..].
			var newVarint = new List<byte>();
			var v = (ulong)adjusted;
			do
			{
				var b = (byte)(v & 0x7F);
				v >>= 7;
				if (v != 0)
					b |= 0x80;
				newVarint.Add(b);
			} while (v != 0);

			var result = new byte[varintOffset + newVarint.Count + (prefix.Length - end)];
			Array.Copy(prefix, 0, result, 0, varintOffset);
			newVarint.CopyTo(result, varintOffset);
			Array.Copy(prefix, end, result, varintOffset + newVarint.Count, prefix.Length - end);
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
			var inventoryRows = client.Session.Character != null
				? await Database.GetCharacterInventory(client.Session.Character.Id)
				: [];

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

			foreach (var packet in packets)
			{
				// Re-address the capture's instance to the logged-in character so the burst works for any character.
				// The NPC handles in the burst are left as-is.
				var pkt = RewriteBurstInstance(packet, capturedInstance, liveInstance);

				if (IsInventorySnapshot(pkt))
				{
					// Send the world-entry snapshot VERBATIM (chat-safe). Build the player's DB inventory snapshot
					// from the same packet and stash it - it is sent AFTER the post-ready burst (post-load) so the
					// backpack shows the player's real items without risking the world-load/chat gate. The verbatim
					// packet is also kept for the session so mid-session inventory changes (.additem) can rebuild
					// + re-send the snapshot through the same proven path.
					client.Session.InventorySnapshotTemplate = pkt;
					client.Session.PendingPostLoadInventory = BuildDbInventorySnapshot(pkt, client.Session.Character, equipment, inventoryRows);
					pkt = BuildBeachInventoryPacket(pkt, client.Session.Character);
				}
				else if (TryRebuildPlayerSpawnFromDb(client, equipment, scaffoldCharacter, pkt, out var rebuilt))
				{
					pkt = rebuilt;
					_broadcaster.CaptureOwnSpawnBody(client, pkt); // stash the player's FA015807 body so other players can be sent it
				}
				else if (TryRebuildNpcSpawnFromDb(client, npcByPosition, pkt, out var npcRebuilt, out var wasIdentical))
				{
					npcBuilt++;
					// DB-built NPC bodies are now the DEFAULT on the wire - the verbatim burst body is dropped and each
					// NPC renders from its NpcSpawns row (transform columns + re-encoded CharacterIIR). The burst handle
					// is kept (TryRebuildNpcSpawnFromDb builds with it) so the spawn stays paired with the burst's
					// 96C46740 MoveTask registration. wasIdentical still tracks how many rebuild byte-identical to the
					// capture (the proven-exact subset) for the log; cross-session rows differ (the row's captured
					// pre-IIR/scaffold blocks carry the source session's instance refs) - this is the from-DB render
					// path that supersedes the verbatim burst.
					pkt = npcRebuilt;
					if (wasIdentical)
						npcIdentical++;
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
				Logger.LogInformation("NPC spawns for map {Map}: {Built} table-built spawns put on the wire (DB-driven, replacing the verbatim burst), {Identical} of them byte-identical to the capture; {Rows} rows available.", burst.MapId,
					npcBuilt, npcIdentical, npcByPosition.Count);
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
		private bool TryRebuildNpcSpawnFromDb(INetworkClient client, IReadOnlyDictionary<(int, int, int), NpcSpawn> npcByPosition, byte[] packet, out byte[] rebuilt, out bool wasIdentical)
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
			if (!npcByPosition.TryGetValue(key, out var row) || !NpcSpawnBuilder.TryBuild(row, handle, out var newBody))
				return false;

			// Keep the burst packet's header (already re-addressed to the live player); swap only the FA015807 body.
			wasIdentical = packet.Length - bodyStart == newBody.Length && packet
				.AsSpan(bodyStart)
				.SequenceEqual(newBody);

			rebuilt = new byte[bodyStart + newBody.Length];
			Array.Copy(packet, 0, rebuilt, 0, bodyStart); // [len][crc][headerLen][header]
			newBody.CopyTo(rebuilt, bodyStart);

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
			else
				newBody = SpawnPacketBuilder.SetSpawnName(newBody, character.Name);

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
	}
}
