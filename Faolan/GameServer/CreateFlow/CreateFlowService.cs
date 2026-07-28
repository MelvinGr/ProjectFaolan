using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.CreateFlow
{
	// The character-creation -> editor -> destiny-cutscene -> enter-world flow, extracted out of the
	// GameServerListener god-class. The listener holds this as
	// _createFlow and forwards the inbound create-flow callbacks to it; the flow's outbound packet builders
	// (the create-world handshake, the destiny-cutscene Scry scripts, and the Retail*Burst editor bursts) all
	// live in this file. Verbatim/byte-exact burst builders stay `static` so the golden tests reach them
	// unchanged (CreateFlowService.SendRetailCreateEditorInitialBurst / ...). The Kalanthes cutscene MoveTask
	// builders moved to QuestHandlerService (#353); the cutscene flow calls them statically.
	//
	// Cross-cutting deps follow the house pattern (CLAUDE.md "composed collaborators"): the world model comes in by
	// ctor, and the one listener-owned outbound op the cutscene needs (SendBurstlessWorldEntry, also used by the
	// existing-char login replay) arrives as a narrow Func seam rather than a back-reference to the listener.

	public sealed class CreateFlowService
	{
		internal const string CreateWorldResourceManifest =
			"1010004:793713;1010004:793714;1070003:4000;1000097:4000;1010030:156211;1010030:239448;1010030:565913;1010004:3728533;1060668:4000;1000059:4000;1060667:167368;1010004:3075445;1010004:3180025;1010004:221285;1010004:3663190;1010004:208923;1010004:208266;1010004:226891;1010004:3663191;1010004:3656109;1010030:210069;1010001:210070;1010004:3075452;1010004:3075450;1010004:3075451;1010300:4000;1000049:4000;1066611:4000;1000013:40000000;1000015:40000000;1010506:4000;1000058:4000;1010207:110006;1010207:262241;1010207:568806;1010207:3420331;1010207:167183";

		// The create-world ScryScript step content (decoded by tools/capture/decode_scryscript.py, #223): the PlayOrSkip(72)
		// remoteformat token for the "Slave Galley" creation step (f4) and the RDB resource manifest the client preloads
		// (f5, "rdbId:value;..."). Carried as named strings now (was a verbatim hex blob); ScryScript.BuildStep frames them.
		internal const string CreateWorldScriptToken =
			"<remoteformat token=\"playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)\" category=\"50023\" key=\"&quot;B]SZ.AGRGrAyOGe*BG-\" knubot=\"0\"  ></remoteformat>";

		internal static readonly ProtoEndpoint MorphAckSender = Endpoints.Endpoint(Endpoints.GameAgentType, 0x0C);

		// The create-editor's own grant conversation (GameCharAgent 0x0C); the login/world grants ride a different one.
		private static readonly ProtoEndpoint CreateGrantSender = Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x0C);

		// Character ids that have just been named in the create-editor and are about to open their FIRST world
		// connection (the second GameServer connection, after the 0x2002 enter-world handoff). Added when the name is
		// committed (HandleCommandPacket TheNameIs) and consumed by the listener's world Hello via
		// TryConsumePendingCreateWorldEntry, which is how that connection is recognised as the create-world entry (and
		// given the intro/cutscene) rather than a normal login. Static because the editor and world connections are
		// different sessions but hit the same listener/service instance; ConcurrentDictionary-as-set because it is
		// mutated from different connection threads (add on one connection, consume on the reconnect). Value unused.
		private static readonly ConcurrentDictionary<uint, byte> PendingCreateWorldEntry = new ConcurrentDictionary<uint, byte>();

		private static readonly ProtoEndpoint RetailCreateEditorPostReadyAckSender = Endpoints.Endpoint(Endpoints.GameCharAgentProjectType, 0x59);
		private static readonly byte[] EditorAvatarSnapshot = Program.LoadEmbeddedResource("Faolan.CreateEditorAvatarSnapshot.bin");

		// The 18 galley scene-prop dynels the create-editor initial burst streams, in retail send order. Each is a
		// WorldObjects row on the editor map (4000), built byte-exact via WorldObjectBuilder (see
		// LoadEditorSceneDynels + CreateEditorSceneDynelTests). The client also renders the galley from its own RDB on
		// the MapChange; retail streams these so their handles are server-known.
		//
		// Two values per prop, and the distinction matters: the HANDLE is what the burst EMITS (the captured wire
		// value, re-addressed onto the row), while the DISPLAY ID - the prop's LDB string reference - is what FINDS
		// the row. This used to be a handle list alone, looked up against the row's own CapturedHandle column, which
		// made the burst depend on a SESSION-VOLATILE value as a persistence key (the thing the wire-protocol notes in
		// CLAUDE.md warn against). Importing a capture whose session re-observed the editor scene renumbered all 18
		// CapturedHandles, every lookup missed, and all 18 props silently dropped out of the burst. The display id is
		// placement truth and is unique across map 4000.
		private static readonly (uint Handle, uint DisplayId)[] EditorSceneDynels =
		[
			(0x0002A7EF, 347648), (0x0002A7FA, 347660), (0x0002A7ED, 837652), (0x0002A7EE, 347646), (0x0002A7F0, 347650), (0x0002A7F1, 347652), (0x0002A7F2, 347667), (0x0002A7F3, 347670),
			(0x0002A7F4, 347653), (0x0002A7F5, 347654), (0x0002A7F6, 347655), (0x0002A7F7, 347656), (0x0002A7F8, 347657), (0x0002A7F9, 347659), (0x0002A7FB, 347661), (0x0002A7FC, 347662),
			(0x0002A7FD, 347663), (0x0002A7FE, 3072626)
		];

		// The listener's burst-less world entry (GameServerListener.WorldEntry.cs) - the constructed live-char world the
		// create-flow enters before the destiny cutscene overlay. Injected as a seam (shared with the login replay).
		private readonly Func<INetworkClient, uint, Task> _sendBurstlessWorldEntry;
		private readonly IDatabaseRepository _database;

		private readonly ILogger _logger;

		// The live world simulation (owns the connected players' authoritative entities + zone model). Used by the
		// cutscene actor-binding (LiveNpcHandle) to re-address the destiny cinematic to OUR live Kalanthes.
		private readonly WorldSimulation _simulation;

		public CreateFlowService(ILogger logger, IDatabaseRepository database, WorldSimulation simulation, Func<INetworkClient, uint, Task> sendBurstlessWorldEntry)
		{
			_logger = logger;
			_database = database;
			_simulation = simulation;
			_sendBurstlessWorldEntry = sendBurstlessWorldEntry;
		}

		// Handles the client->server 0xA36D3B74 character-creation script callbacks.
		//
		// Wire format (after the 0x2000 wrapper and the 0xA36D3B74 subopcode have been consumed):
		//   uint32 ack            (0x0000C350)
		//   uint32 clientInstance (the create-editor temp instance, e.g. 0x021662C8)
		//   byte   reserved       (always 0x00)
		//   uint32 pbLength       (length of the protobuf message that follows)
		//   protobuf message:
		//     field 1 (varint) = 5      (constant)
		//     field 2 (varint) = 2      (constant)
		//     field 3 (varint) = numeric value   (e.g. ChangeSex sex id, ChangeHeadMesh mesh id)
		//     field 4 (string) = token            (the callback name, e.g. "TheNameIs")
		//     field 5 (string) = value            (optional string, e.g. the chosen character name)
		//     field 6 (bytes)  = trailing nested block (ignored)
		//
		// These callbacks are how the editor reports the player's choices (sex/race/head/size/class/
		// voice/name) and lifecycle events (IntroDone, ClientLoadingDone, Move). Most Change* callbacks are
		// parse-only (the client self-advances), BUT three DO get a server reply - proven from the retail
		// create captures (#197 analysis 2026-07-06, contradicting the older "zero replies" note here):
		//   - SetMorphValue -> a 0xBADF5A4B MorphAck echoing each morph value (Hoxxling_..._modified_all_sliders:
		//     36 client morphs @f553 -> 34 acks @f562; distinct from the ~5 hairmesh/texscript setup 0xBADF5A4B
		//     the server sends earlier at editor init).
		//   - Finish -> the post-morph trio (StatIIR + StartScriptGroup + StatUpdate) that prompts TheNameIs.
		//     Finish arrives in the SAME client batch as the morphs but BEFORE them, and retail sends the trio
		//     AFTER acking the batch's morphs - which is why SchedulePostMorphBurst debounces (see #197: the
		//     deterministic trigger is "Finish + the morph batch drained", the ~350ms timer fakes that).
		//   - TheNameIs -> NicknameOk / NicknameTaken / NicknameInvalid (SendNicknameAccepted/Rejected).
		//
		// TheNameIs commits the chosen name straight to the character row (CommitCharacterName); the
		// create-editor gating keys on Character.Name so the commit is what promotes it out of the editor.
		internal async Task HandleCommandPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var ack = body.ReadUInt32();
			var clientInstance = body.ReadUInt32();
			var reserved = body.ReadByte();
			var msg = body.ReadProtobufUInt32Length<ProtoScryScriptIIR>();

			var value = msg.IntParameter; // decoded signed value (identity ids, morph slider, ...)
			var rawValue = (uint)((value << 1) ^ (value >> 31)); // the raw wire varint (zigzag re-encode) for script echoes

			var scryType = msg.type;
			var token = msg.StringParameterOne;
			var valueString = msg.StringParameterTwo;

			var character = client.Session.Character;
			_logger.LogInformation("Character-editor script callback: token={Token}, scryType={ScryType}, value={Value} (0x{ValueHex:X}), string={String}, clientInstance={ClientInstance:X8}.", token,
				scryType, value, value, valueString ?? "", clientInstance);

			// #234: the beach cutscene's PlayOrSkipResultPlay (scryType 8) carries NO name token. On the create-world
			// connection it's the cue to bind the cinematic actor - retail sends ThisIsKalanthes in RESPONSE to
			// PlayResult (AFTER the player commits to playing the cutscene), not before. Route it explicitly.
			// NB the ProtoScryScriptIIR getter returns "" (not null) for an absent token, so test for empty, not null -
			// a `token == null` here silently killed PlayResult (and the intro/actor bind -> black screen) after the
			// 585a789f protobuf-reader refactor.
			if (string.IsNullOrEmpty(token) && scryType == ProtoScryScriptIIR.Type.PlayOrSkipResultPlay && IsCreateWorldConnection(client))
			{
				await SendCreateCutsceneStep(client, "PlayResult", 0);
				return;
			}

			switch (token)
			{
				// The editor sends class/race as 2x the real id (verified against captured char-lists: editor
				// 36/68/86/82 -> class 0x12/0x22/0x2B/0x29; editor race 2/6 -> race 1/3). Store the halved
				// canonical id so the character-list packet (and everything else) gets a valid class/race; sending
				// the raw 2x value makes the client show dashes for race/class on the select screen.
				case "ChangeSex":
				{
					// Same 2x editor convention as race/class: the halved (zigzag-decoded) value is the wire sex-code
					// 2=MALE / 3=FEMALE (anchored on Hoxxling, male: ChangeSex 4 -> 2). The Sex enum is Male=2/Female=3,
					// so the decoded value casts straight to the enum: 4->2 Male, 6->3 Female. No magic ==.
					character?.Sex = (Sex)value;

					break;
				}

				case "ChangeRace":
				{
					character?.Race = (Race)value;
					break;
				}

				case "ChangeClass":
				{
					character?.Class = (ConanClass)value;
					break;
				}

				case "ChangeHeadMesh":
				{
					if (character != null)
					{
						EnsureBody(character).HeadMesh = (uint)value;
						await PersistLateBodyEdit(client);
					}

					break;
				}

				case "ChangeSize":
				{
					if (character != null)
					{
						EnsureBody(character).Size = (byte)value;
						await PersistLateBodyEdit(client);
					}

					break;
				}

				case "ChangeVoice":
				{
					if (character != null)
					{
						EnsureBody(character).Voice = (byte)value;
						await PersistLateBodyEdit(client);
					}

					break;
				}

				case "TheNameIs":
				{
					// Commit the chosen name to the character row so the later world-entry Hello (a
					// separate connection/session that re-loads the character from the database) sees a
					// named character and takes the normal world-entry path. The client may try several
					// names (e.g. Stricken -> Strickennn -> Strickenen); the last one that sticks wins.
					var commit = await CommitCharacterName(client, valueString);

					if (commit == NameCommitResult.Committed && client.Session.Character.Name == valueString)
					{
						// Confirm the name (NicknameOk + CharCreationFinished) so the client advances to enter-world.
						await SendNicknameAccepted(client, valueString!);

						// The character is now named; its NEXT GameServer connection (opened after the 0x2002
						// enter-world handoff) is the create-world entry and must get the intro/cutscene. Mark it
						// pending so the world Hello recognises that connection (and not later normal logins).
						PendingCreateWorldEntry.TryAdd(client.Session.Character!.Id, 0);
						_logger.LogInformation("Create-editor: character {CharacterId:X8} named '{Name}'; marked pending create-world entry.", client.Session.Account.ClientInstance, valueString);
					}
					else
					{
						// Reject the name so the client re-prompts instead of silently stalling (no loading screen).
						// NicknameTaken = already in use; NicknameInvalid = empty/unsaveable (retail callbacks, see
						// SendNicknameRejected). Without this the create flow dead-ends on a duplicate name.
						await SendNicknameRejected(client, valueString!, commit == NameCommitResult.Taken);
					}

					break;
				}

				case "Finish":
				{
					// The editor is done customizing; its SetMorphValue stream follows (only for changed
					// morphs, so the set varies and may be empty if nothing was morphed). Debounce the
					// post-morph trio off Finish + each morph so it fires once the stream goes quiet,
					// regardless of which morphs were sent.
					SchedulePostMorphBurst(client);
					break;
				}

				// Applies one editor SetMorphValue callback to the character's body. The value is a ZIGZAG-encoded
				// signed slider value; CharacterMorphs decodes it and stores the signed value in the matching
				// CharacterBody column (the spawn morph float is that value / 1000 - see CharacterMorphs). Resource
				// refs (*hairmesh/*beardmesh/*texscript - the chosen hair/colors/tattoos/scars) are recorded in
				// editor send order; they become the spawn scaffold's weighted entries via MorphTarget. Sliders not
				// yet seen in captures are logged and skipped.
				case "SetMorphValue":
				{
					// The retail creator acks every non-empty morph with a 0xBADF5A4B packet that echoes the
					// morph value. (Confirmed from the dikketest capture, whose creator DL the analyzer
					// decoded - strickenen only LOOKED silent because its creator DL was dropped by the
					// compression-cut.) The post-morph trio is debounced off Finish + each morph rather than
					// keyed to a specific morph, since leaving the body default means "thigh"/body morphs are
					// never sent.
					if (!string.IsNullOrEmpty(valueString))
					{
						if (character != null)
						{
							if (valueString[0] == '*')
								RecordBodyResource(character, valueString);
							else
							{
								if (!CharacterMorphs.TryApply(EnsureBody(character), valueString, value))
									_logger.LogDebug("Character-editor: unmapped morph '{Morph}'={Value} (slider not yet modelled).", valueString, value);
							}

							await PersistLateBodyEdit(client);
						}

						await SendMorphValueAck(client, valueString);
					}

					SchedulePostMorphBurst(client);
					break;
				}

				case "IntroDone":
				{
					// On the post-creation WORLD connection this is the last cutscene trigger (the beach /
					// ApproachCasilda finale) - send the next chunk. On the EDITOR connection it instead marks
					// the editor interactive (the editor connection then drives itself; see the create flow).
					if (IsCreateWorldConnection(client)) // #234 step2: drive the cutscene Scry steps over the burst-less world
					{
						await SendCreateCutsceneStep(client, token, rawValue);
						break;
					}

					_logger.LogInformation("Character-editor: IntroDone - editor is interactive; the editor connection is now silent until enter-world.");
					break;
				}

				// The intro cutscene is a call-and-response: each client script trigger
				// (ClientLoadingDone -> StartScript(7088774) -> StartScript(7124502) -> Move -> IntroDone) gets the
				// matching Scry response, with StartScript echoing the requested group id (numericValue / field 3).
				case "ClientLoadingDone":
				case "StartScript":
				case "Move":
				{
					if (IsCreateWorldConnection(client)) // #234 step2: drive the cutscene Scry steps over the burst-less world
						await SendCreateCutsceneStep(client, token, rawValue);
					break;
				}

				default:
				{
					_logger.LogInformation("Character-editor: unhandled script callback token '{Token}' (value={Value}, string={String}).", token, value, valueString ?? "");
					break;
				}
			}
		}

		// The post-creation world entry: handshake + the burst-less live-char world. The intro cutscene follows
		// separately, on CharInPlayIIR (SendCreateIntroCutscene).
		internal async Task SendCreateWorldWithCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// (a) The create-world handshake - the three records the verbatim scene burst opened with (the shared
			// Hello handshake at GameServerListener is skipped for isCreateWorldEntry, so send it here).
			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameInterfaceOpcodes.DispatchReportDimesionID,
				(byte[])[0x00, 0x01, 0x31]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameInterfaceOpcodes.DispatchReportServerID,
				(byte[])[0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x03]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameInterfaceOpcodes.DispatchAckAuthenticate,
				(byte[])[0x00, 0x00, 0x00, 0x01]));

			// (b) The live-char world (constructed; same path existing-char logins use). The intro cutscene does NOT
			// follow here - see SendCreateIntroCutscene for why it waits for CharInPlayIIR.
			await _sendBurstlessWorldEntry(client, client.Session.Character.MapId);
		}

		// (c) Start the destiny cutscene (Scry scripts only - the begin marker, the scene-load character_creation step,
		// and the two opening script groups). The per-callback steps follow via SendCreateCutsceneStep.
		//
		// Sent in RESPONSE to the create-world CharInPlayIIR, after the ready burst - the order retail uses (Blablatest
		// create: c2s CharInPlayIIR fr317 -> s2c 0x2024 fr324 -> s2c cutscene fr325). Sending it with the world entry
		// instead put the cutscene FIRST, so the client's CharInPlayIIR - and therefore ReadyForPlayScreen - landed in
		// the middle of the intro. That is the same failure the 0x2020-poll flush caused, just moved later: the intro
		// began correctly and then the world snapped back to the un-ready state.
		internal async Task SendCreateIntroCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// EnergyIIR (0x40749B8A) seed on the avatar - constructed via CreateEditorRecordBuilder (byte-exact-locked
			// by CreateEditorRecordTests), was a verbatim blob. flag=1, value=0x36B0.
			await SendIir(client, Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameServer0X2000Opcodes.EnergyIIR,
				CreateEditorRecordBuilder.BuildEnergyIir(EntityClass.Character, id, 0x01, 0x36B0));
			await SendScry(client, ScryScript.BuildStartScriptGroup(id, 48126));
			await SendScry(client,
				ScryScript.BuildStep(id,
					"<remoteformat token=\"playorskip(100)|onclientconnected(1)|character_creation Tortage Beach (server-side)\" category=\"50023\" key=\"*&#36;!0mcl&#36;Z!5Ii0UG!TV?\" knubot=\"0\"  ></remoteformat>",
					6,
					resources:
					"1010003:3147889;1010003:3147890;1010003:3147891;1010003:3147892;1010003:3147893;1010003:3147894;1010003:137283;1010003:202270;1010003:210528;1010003:205370;1010003:3147896;1010003:3147895;1010003:3147897;1010003:3147898;1010003:3147899;1010003:3147900;1010003:208235;1010003:3203880;1010003:3203879;1010003:3203881;1010003:3203876;1010003:3203877;1010003:3203878",
					f6Field1: 4617355, f6Field2: 7004418));
			await SendScry(client, ScryScript.BuildStartScriptGroup(id, 385910));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentProjectType, 0x08), Endpoints.Endpoint(Endpoints.GameCharInterfaceProjectType, id),
				GameInterfaceOpcodes.DispatchPassBlob, (byte[])[0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x00]));
		}

		private async Task<NameCommitResult> CommitCharacterName(INetworkClient client, string? name)
		{
			var character = client.Session.Character;
			if (character == null || string.IsNullOrWhiteSpace(name))
				return NameCommitResult.Invalid;

			if (string.Equals(character.Name, name, StringComparison.Ordinal))
				return NameCommitResult.Committed;

			// Check the name is free BEFORE saving (Characters.Name is UNIQUE) so a taken name is rejected cleanly as
			// NicknameTaken rather than blowing up the SaveCharacter unit of work. A row owned by THIS character doesn't
			// count (idempotent re-name to the same value is handled above).
			var existing = await _database.GetCharacterByName(name, character.RealmId);
			if (existing != null && existing.Id != character.Id)
			{
				_logger.LogInformation("Character-editor: name '{Name}' already taken by character {OwnerId:X8}; rejecting.", name, existing.Id);
				return NameCommitResult.Taken;
			}

			character.Name = name;
			// The class is chosen before naming (the final editor step), so regenerate vitals from the now-known
			// class/level - replacing the level-1 rogue baseline seeded at row allocation.
			character.RegenerateMaxStats();
			// Persist the whole accumulated editor graph (name + sex/race/morphs Body + MaxStats) as one unit of work.
			await _database.SaveCharacter(character);

			// Seed the spawn scaffold and the starter gear HERE, the first moment either can be right: both are
			// generated from the class (and the scaffold also from the editor body), and those are only known once the
			// editor's final step commits. (Not at row allocation - CreateCharacter hands out a placeholder Cimmerian
			// barbarian with no body, which would bake the wrong skill list, appearance and weapon into every
			// character.) Doing it at creation makes "a character has scaffold rows and its starting gear" an
			// invariant, so world entry is a pure read.
			await SeedSpawnScaffold(character);
			await SeedStarterEquipment(character);
			await CharacterLoadout.Seed(_database, _logger, character);
			await SeedBind(character);

			// Grant the beach starter quest (10238) ONCE, here at character creation - retail auto-grants it on first
			// beach entry; we persist it now so the journal is data-driven from CharacterQuests (delivered live on the
			// create-world entry, then riding the snapshot on later logins) instead of baked into the snapshot. It is
			// NOT re-seeded on world entry, so a completed/removed quest never reappears.
			await _database.SetCharacterQuest(character.Id, WorldQuest.BeachStarterQuestId, QuestState.Active);
			_logger.LogInformation("Character-editor: committed character name '{Name}' for character {CharacterId:X8}; granted starter quest {QuestId}.", name, character.Id,
				WorldQuest.BeachStarterQuestId);
			return NameCommitResult.Committed;
		}

		// Grants the character's class-starting spells during create as DispatchLearnSpell (0x200A) packets - the
		// spell counterpart to the world-prep burst's hardcoded 0x2052 ability grants. The client bars each granted
		// spell, so these MUST arrive before its 0x206E bar drags. The body is just the 4-byte spell id (retail
		// f239). Melee classes resolve to an empty set (no starting spells).
		// The live wire handle of the in-zone NPC named `npcName` (the cinematic actor to bind), or `fallback` when
		// it isn't loaded. Used to re-address the cutscene's actor reference (ThisIsKalanthes f6) to OUR live entity.
		private uint LiveNpcHandle(INetworkClient client, string npcName, uint fallback)
		{
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
				return fallback;

			var npc = _simulation.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.Name == npcName);
			return npc?.Handle ?? fallback;
		}

		// Sends the post-morph trio once the morph stream goes quiet. After Finish the editor streams its
		// SetMorphValue callbacks, but ONLY for morphs that were changed - so the set (and which one is last)
		// varies, and if the body is left default the "thigh"/body morphs never arrive. Instead of keying off
		// a specific morph, we debounce: Finish and every morph bump MorphActivitySequence; ~350ms after the
		// last activity (so any morphs have arrived and been acked) we send the trio exactly once.
		// #314: appearance edits (morph/resource/head/size/voice) mutate the in-memory character.Body, which is
		// persisted once at name-commit (CommitCharacterName -> SaveCharacter). An edit that arrives AFTER the name is
		// committed - the client streams SetMorphValue after Finish, and the naming can land first, or a morph rides a
		// later TCP segment - would only touch the in-memory body and be LOST on the spawn (which reloads from the DB),
		// showing wrong hair/mustache/size intermittently. Re-persist here so a late edit survives. No-op until named
		// (the pre-name edits are flushed by the name-commit save), so the common path takes no extra write.
		//
		// Saving the body IS the edit landing (#483): the spawn's appearance entries are DERIVED from Body /
		// BodyResources when the spawn is built, so there is no second copy to push the edit into. This used to be
		// followed by a RefreshAppearance + SaveCharacterScaffold pair, and missing that pair anywhere froze the face.
		private async Task PersistLateBodyEdit(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character?.Name == null)
				return;

			await _database.SaveCharacter(character);
		}

		// Seeds the fresh-character spawn-scaffold rows onto `character` and persists them. No-op for a character that
		// already has them (TrySeedFreshRows says so), so a re-commit costs nothing.
		//
		// Deliberately NOT wrapped in a catch. "Loud but not fatal" would be a lie here: PlayerSpawnBuilder throws
		// without these rows, so a character committed without them is one that fails at every world entry and every
		// spawn broadcast, forever - a create that half-succeeded is worse than one that visibly failed.
		private async Task SeedSpawnScaffold(Character character)
		{
			// Creation is the one place the character's editor choices are worth checking against the catalog: from
			// here on they are resolved silently on every spawn, so an unimportable ref would show up as a wrong face
			// and nothing else.
			await ReportUnresolvableAppearance(character);

			if (!PlayerSpawnScaffoldRows.TrySeedFreshRows(character))
				return;

			await _database.SaveCharacterScaffold(character);
			_logger.LogInformation("Seeded fresh spawn-scaffold rows for character {CharacterId:X8}.", character.Id);
		}

		// Dresses a newly created character in the #220 starting gear: the manacles both sexes wear, the sex-specific
		// slave outfit (male loincloth / female gown + bottom, colour randomised per character as retail does) and the
		// class's improvised weapon.
		//
		// This used to run on first world entry, keyed on the CharacterEquipment ROW being absent - and it silently
		// stopped working the moment that row started being created with the character (ce580410): the row now always
		// exists, so the seeding branch became unreachable and new characters entered the world naked. Keyed on the
		// SLOTS being empty instead, and run where sex and class are known, which is the same place they became known
		// for the scaffold.
		private async Task SeedStarterEquipment(Character character)
		{
			var equipment = await _database.GetCharacterEquipment(character.Id)
				?? throw new InvalidOperationException($"Character {character.Id:X8} has no CharacterEquipment row - it is created with the character, so this is corruption.");

			// Only ever dress an UNDRESSED character. Re-filling emptied slots later would undo the player's own
			// unequips, which is why this cannot simply run on every login.
			if (equipment.WristId != null || equipment.ChestId != null || equipment.LegsId != null || equipment.MainHandLeftId != null)
				return;

			var female = character.Sex == Sex.Female;
			var femaleOutfit = StartingData.StartingFemaleOutfits[Random.Shared.Next(StartingData.StartingFemaleOutfits.Length)];

			equipment.Wrist = await _database.GetItem(StartingData.StartingWristItem);
			equipment.Chest = female
				? await _database.GetItem(femaleOutfit.Gown)
				: null;
			equipment.Legs = await _database.GetItem(female
				? femaleOutfit.Bottom
				: StartingData.StartingMaleLegsItem);
			// Class-specific improvised weapon (main-hand-left); only the classes the create dumps cover.
			equipment.MainHandLeft = StartingData.StartingWeapon.TryGetValue(character.Class, out var weaponId)
				? await _database.GetItem(weaponId)
				: null;

			await _database.SaveCharacterEquipment(equipment);
			_logger.LogInformation("Dressed new character {CharacterId:X8} ({Class}, {Sex}) in its starting gear.", character.Id, character.Class, character.Sex);
		}

		// Binds the new character to its starting map's default respawn point, so recalls (Totem of Origin, Path of
		// Asura, .recall) and death-resurrect have somewhere to send it from the very first login. The bind moves after
		// this only when the player uses a bindstone/Wayfarer.
		//
		// No bind packet is sent here: the "You are now bound here" banner (#346) belongs to a bindstone USE, and this
		// connection is still the character editor. The client learns the bind with the rest of its state at world
		// entry.
		private async Task SeedBind(Character character)
		{
			var spawn = await _database.GetDefaultSpawn(character.MapId);
			if (spawn == null)
			{
				// Not fatal, and not silent: a map with no respawn point is a content gap, not corruption - the
				// character simply has nowhere to recall to until one is imported for that map.
				_logger.LogWarning("Bind: map {Map} has no default respawn point, so {CharacterId:X8} starts unbound.", character.MapId, character.Id);
				return;
			}

			character.BindRespawnPointId = spawn.Id;
			await _database.SaveCharacterBind(character.Id, spawn.Id);
			_logger.LogInformation("Bind: {Name} bound to respawn point {Point} on map {Map} at creation.", character.Name, spawn.Id, character.MapId);
		}

		// Says out loud when the character's own editor appearance choices will NOT reach its spawn. The spawn resolves
		// them itself, on every send (PlayerSpawnScaffoldRows.BuildWeightedEntries); this runs once, at creation,
		// because that is where a gap is still actionable and where saying it once is not log spam.
		private async Task ReportUnresolvableAppearance(Character character)
		{
			if (character.BodyResources is not { Count: > 0 })
				return;

			var registry = await _database.GetMorphTargetRegistry();
			if (PlayerSpawnScaffoldRows.BuildWeightedEntries(character.BodyResources, registry) != null)
				return;

			// Falling back to the defaults means wearing DIKKETEST's face, so be precise about why, instead of the old
			// one-size warning: BuildWeightedEntries returns null for two unrelated reasons and only one of them is a
			// problem.
			//   - a ref the registry does not know  -> a GAP. MorphTargets ships in the seed (1072 rows), so an
			//     unknown ref means the editor sent something we never imported, and this character will not look like
			//     what the player built.
			//   - every ref resolved but all were CATEGORY 2 (face paint, e.g. the always-sent *texscript_183875) ->
			//     by design: captured spawns never carry those, so there is nothing to express and the defaults are the
			//     correct baseline.
			var unresolved = character.BodyResources.Select(r => r.Ref).Where(r => !registry.ContainsKey(r)).ToArray();
			if (unresolved.Length > 0)
			{
				_logger.LogError("Character {CharacterId:X8}: appearance resource(s) {Refs} are not in MorphTargets, so its own looks cannot be built and it gets the default face. Import them.",
					character.Id, string.Join(", ", unresolved));
			}
			else
			{
				_logger.LogInformation("Character {CharacterId:X8}: all {Count} appearance resource(s) are cosmetic-only (category 2), which spawns never carry - using the default entries.",
					character.Id, character.BodyResources.Count);
			}
		}

		private void SchedulePostMorphBurst(INetworkClient client)
		{
			if (client.Session.PostMorphBurstSent)
				return;

			var sequence = ++client.Session.MorphActivitySequence;

			// FALLBACK ONLY. The deterministic, retail-faithful trigger is FlushPostMorphBurstOnBatchDrained,
			// fired when the TCP recv that carried Finish + the SetMorphValue burst drains (#197 - retail sends
			// the trio once the client's customization batch is fully received, not after a wall-clock delay).
			// This timer only catches the rare case where that burst is split across recvs, so the editor can
			// never hang waiting for the trio; it's a long safety net, not the primary path.
			_ = Task.Run((Func<Task>)(async () =>
			{
				try
				{
					await Task.Delay(1500);

					// A newer morph/Finish arrived (the burst is still streaming) -> a later task / the batch-drain
					// will send it. TrySend also no-ops if it was already sent.
					if (client.Session.MorphActivitySequence != sequence)
						return;

					await TrySendPostMorphBurstOnce(client);
				}
				catch (Exception e)
				{
					_logger.LogWarning(e, "Character-editor: failed to send the post-morph burst (fallback timer).");
				}
			}));
		}

		// One cutscene step per client callback, driving the beach/Kalanthes destiny sequence to IntroDone. This is a
		// call-and-RESPONSE: decoded from Blablabla_* (port 7020), retail's world connection runs
		//   C2S PlayResult                  -> S2C ThisIsKalanthes
		//   C2S StartScript int=7088774     -> S2C StartGroup 7088774  (then, ~2.7s later, WelcomeTutorial)
		//   C2S StartScript int=7124502     -> S2C StartGroup 7124502
		//   C2S Move                        -> S2C Kneel
		//   C2S IntroDone                   -> S2C StartFixed3d (+ StartGroup 7727158)
		// The crucial bit: the client REQUESTS each script group by id (StartScript carries the id in field 3) and
		// waits for the server to ECHO BACK a StartGroup with the SAME id before it actually runs the group. Group
		// 7088774 is Kalanthes's welcome-dialogue group, so without that echoed confirmation the speech never plays.
		// (The earlier counter-based version pushed StartGroup 7088774 prematurely on ClientLoadingDone and then
		// answered the client's StartScript 7088774 with WelcomeTutorial instead of the StartGroup echo -> no speech.)
		// `groupId` is the requested id from the StartScript callback (0 for the other lifecycle callbacks).
		private async Task SendCreateCutsceneStep(INetworkClient client, string token, uint groupId)
		{
			var id = client.Session.Account.ClientInstance;
			switch (token)
			{
				case "PlayResult":
				{
					// #234 cinematic actor-binding: ThisIsKalanthes's f6 = {0xC350 type, <Kalanthes ENTITY HANDLE>} -
					// it tells the cutscene which live entity is Kalanthes (the walk-up + speech actor). Decoded from
					// Blablabla_* f658 (f6Field2 = 0x2C1C = the live Kalanthes spawn handle, == the MoveTask handle).
					// Bind OUR live Kalanthes (streamed by EnsureZoneNpcs, within range) or fall back if unloaded.
					// Retail sends this in RESPONSE to PlayResult (AFTER the player commits to playing the cutscene) -
					// binding BEFORE the cinematic starts (on ClientLoadingDone) loses the actor when it initialises.
					await SendScry(client, ScryScript.BuildStep(id, "ThisIsKalanthes", f6Field1: 50000, f6Field2: LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					// Position Kalanthes next to the washed-ashore player (his cutscene start), so the walk-up reads
					// (his DB spawn is back by the rocks). The walk path itself follows on the Move callback.
					await QuestHandlerService.SendKalanthesMoveTask(client, QuestHandlerService.BuildKalanthesIntroMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					break;
				}

				case "ClientLoadingDone":
				{
					// World finished loading. Retail's beach binds the actor on PlayResult (above), not here - no-op.
					break;
				}

				case "StartScript":
				{
					// Echo back the StartGroup the client just requested (same id) - the confirmation that actually
					// RUNS the group on the client. Retail sends exactly this in response to each StartScript.
					await SendScry(client, ScryScript.BuildStartScriptGroup(id, groupId));
					// 7088774 is Kalanthes's welcome-dialogue group; retail follows its StartGroup with the
					// WelcomeTutorial step (~2.7s later, no triggering callback) - bundle it here, callback-driven.
					if (groupId == 7088774)
						await SendScry(client, ScryScript.BuildStep(id, "WelcomeTutorial"));
					break;
				}

				case "Move":
				{
					// Kalanthes walks up to the player (f890 path), then kneels.
					await QuestHandlerService.SendKalanthesMoveTask(client, QuestHandlerService.BuildKalanthesWalkMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					await SendScry(client, ScryScript.BuildStep(id, "Kneel"));
					break;
				}

				case "IntroDone":
				{
					await SendScry(client, ScryScript.BuildStep(id, "StartFixed3d"));
					await SendScry(client, ScryScript.BuildStartScriptGroup(id, 7727158));
					break;
				}
			}
		}

		// Step 2: answer the client's TheNameIs callback so it knows the name was accepted and advances to
		// enter-world. Sequence from dikketest (idx 359-363): 0xADCE0CDA (echoes the name) + an 0xA36D3B74
		// "NicknameOk" callback (echoes the name) + 0xF508F4C1 (fixed) + an 0xA36D3B74 "CharCreationFinished".
		private async Task SendNicknameAccepted(INetworkClient client, string name)
		{
			var instance = client.Session.Account.ClientInstance;

			await SendIir(client, MorphAckSender, Receiver(client), GameServer0X2000Opcodes.SetNameIIR, CreateEditorRecordBuilder.BuildSetNameIir(instance, name));

			await SendA36DCallback(client, "NicknameOk", name);
			await SendCreateEditor2000(client, (uint)GameServer0X2000Opcodes.GameActionListIIR,
			[
				0x00, 0x00, 0x00, 0x07, 0xE2, 0x00, 0x00, 0xCF, 0xEF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
			], instance);
			await SendA36DCallback(client, "CharCreationFinished", "");

			// Step 3 (REMOVED): the world-prep grant burst (SendWorldPrepBurst) used to fire here - the guardian
			// capture's hardcoded 0x2052 abilities / ComboBookIIR combos / SetItemStat / CharacterAction records.
			// Proven redundant in-client (a created necro is fully functional without it): every grant is re-emitted
			// class-driven by the creation-time loadout seed (CharacterLoadout.Seed: ClassAbilities + GetStartingBarSpells
			// + GrantCharacterCombos), which the create flow reaches via SendBurstlessWorldEntry. The only WorldPrep-unique
			// effect was the live create-session quickbar placement of the 3 basic attacks, which the shared bar-seed
			// covers for a fresh char (verified: they appear on the quickbar after relog). Removed rather than folded.

			// Step 3b: grant the class's starting SPELLS so the client places them on the starting bar. The
			// world-prep burst hardcodes only the 0x2052 ABILITY grants; retail also sends the class spells as
			// DispatchLearnSpell (0x200A) on the same GameCharInterface framing, during create right BEFORE the
			// client's 0x206E bar drags (Blablatest necro f239: Ice Strike R1 + Frozen Hatred). Class-aware, so
			// casters get spells and melee get none (matching the barbarian capture the burst was seeded from).
			// With the client's bar drags now persisted, these land on the bar (the client only bars what it was
			// granted); without it a fresh caster's bar has abilities but no spells.
			// Hand the client the kit its bar is built from. It answers each grant with an AddShortcut (0x206E), and
			// the first of those is what the enter-world handoff keys on - so this is not decoration, it is what moves
			// the flow forward.
			await CharacterLoadout.GrantToClient(client, _database, _logger, CreateGrantSender);

			// The client will now send 0x206E (enter world); answer it with the 0x2002 world handoff.
			client.Session.AwaitingEnterWorld = true;
		}
		// Verbatim replay of the retail create-editor INITIAL download from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (GameServer port 7030),
		// rows 30-65. This stage front-loads MapChange + SpawnObject/Spawn + AccessWindow + GameTime
		// + the first 9x 0xF98E10B3 before the client sends its first 0x205F.

		// `now` feeds the generated GameTime resync anchor (see the GameTime step below); it is a parameter so the
		// byte-exact burst golden can pass the capturing instant and still match the capture.
		public static async Task SendRetailCreateEditorInitialBurst(INetworkClient client, IDatabaseRepository? database = null, DateTimeOffset? now = null, ILogger? logger = null)
		{
			var id = client.Session.Account.ClientInstance;
			var sender = Endpoints.Endpoint(Endpoints.GameAgentType, 0x59);
			var receiver = Endpoints.Endpoint(Endpoints.GameInterfaceType, id);

			// The galley scene props are WorldObjects rows on the editor map (4000), loaded here keyed by captured
			// handle and streamed below (after the MapChange) built byte-exact from their columns via WorldObjectBuilder
			// - no verbatim bytes. Requires a DB in production; a bare no-DB golden run simply omits them.
			var dynelRows = await LoadEditorSceneDynels(database);

			// MapChange: CONSTRUCTED from the editor map's reference point + the session playfield token (#189
			// item 3). mapId + playfieldToken are the meaningful values; the reference position, clip planes and
			// trailing flags are this map's constants (not yet semantically pinned - carried as builder args).
			await Iir(GameServer0X2000Opcodes.PlayfieldFIIR,
				CreateEditorRecordBuilder.BuildPlayfieldFiir(StartingData.EditorSpawnPosition.X, StartingData.EditorSpawnPosition.Y, StartingData.EditorSpawnPosition.Z, StartingData.EditorMapId, 0, 1,
					CreateEditorCharBuilder.EditorPlayfieldInstance, 15.0f, BitConverter.UInt32BitsToSingle(0x415DA9E7u), 0x00000080, 1u));

			// The 18 galley scene props: SPAWNED FROM their map-4000 WorldObjects rows (built byte-exact by
			// WorldObjectBuilder, re-addressed to the captured handle + editor playfield), in retail send order.
			foreach (var (handle, displayId) in EditorSceneDynels)
			{
				if (dynelRows == null)
					break; // no DB at all (the bare golden run) - documented above, and it omits ALL of them, not some

				if (!dynelRows.TryGetValue(displayId, out var dynelRow))
				{
					// Never quietly: a missing prop leaves a visible hole in the editor scene, and the burst that
					// dropped all 18 of them still looked like a successful send.
					logger?.LogWarning("Create-editor scene prop {DisplayId} has no map {MapId} WorldObjects row - the burst omits it.", displayId, StartingData.EditorMapId);
					continue;
				}

				var dynel = WorldObjectBuilder.Build(dynelRow, handle, CreateEditorCharBuilder.EditorPlayfieldInstance);
				await Iir(GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR, dynel[8..]);
			}

			// The editor scene pedestals: each SimpleChar is RECONSTRUCTED from its own bytes via IirChar (#189) -
			// the avatar (Stygian Female, the chosen looks) and the four fixed example models. The live preview is
			// the IirId record further down.
			await IirChar(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x92, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0x2B, 0x77, 0x83, 0x43, 0x15, 0xD9, 0x73, 0xC0, 0x40, 0x1D, 0x29, 0x7C, 0x8C,
				0x43, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x25, 0xD9, 0x7C, 0x3F, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x5D, 0x30, 0x20, 0x3E, 0x18, 0xF6, 0xC4, 0xC0, 0x02, 0x05, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x71, 0x51, 0xE7, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x71, 0x51, 0xE8, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x5B,
				0x60, 0x90, 0x58, 0x62, 0xB7, 0xE0, 0x00, 0x00, 0x00, 0x40, 0x82, 0x94, 0x00, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x10, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5E, 0x49, 0xD8, 0xB0, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x71, 0x51, 0xE8, 0x1C, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
				0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x71, 0x51, 0xE7, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x05, 0x00,
				0x00, 0x00, 0x00, 0x77, 0x53, 0x96, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x82, 0x03, 0x00, 0x00, 0x00, 0xDF, 0x0A, 0x0E, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x20,
				0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x22, 0x49, 0x08, 0x03, 0x10, 0x00, 0x18, 0x03, 0x20, 0x5A, 0x28, 0xA3, 0xB2, 0x08, 0x30, 0x9C, 0x31, 0x38, 0x9C, 0x31, 0x40, 0x9C, 0x31, 0x48,
				0x9C, 0x31, 0x50, 0x9C, 0x31, 0x58, 0x9C, 0x31, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 0x88, 0x01, 0xF3, 0xCE, 0x0C, 0x90, 0x01, 0x01, 0x98,
				0x01, 0x01, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xE8, 0x01, 0x00, 0x90, 0x02, 0x01, 0xA0, 0x02, 0x03, 0x2A, 0x13, 0x08, 0x14, 0x10, 0x05, 0x18, 0x01, 0x25,
				0x74, 0x0A, 0x00, 0x00, 0x28, 0x01, 0x48, 0x80, 0x80, 0x70, 0x70, 0x04, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00, 0xA0, 0x01, 0x01,
				0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x05, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xD9, 0xA3, 0x57, 0x33, 0x15, 0xBC, 0x2F, 0x97, 0x4D, 0x1D, 0xB7, 0x49, 0x95, 0x5F, 0x25, 0x41, 0x2D,
				0x27, 0xB7, 0x12, 0x14, 0x0D, 0xD9, 0xA3, 0x57, 0x33, 0x15, 0xBC, 0x2F, 0x97, 0x4D, 0x1D, 0xB7, 0x49, 0x95, 0x5F, 0x25, 0x41, 0x2D, 0x27, 0xB7, 0x1A, 0x14, 0x0D, 0xD9, 0xA3, 0x57,
				0x33, 0x15, 0xBC, 0x2F, 0x97, 0x4D, 0x1D, 0xB7, 0x49, 0x95, 0x5F, 0x25, 0x41, 0x2D, 0x27, 0xB7, 0x25, 0x74, 0x9D, 0x01, 0x00, 0x2D, 0x74, 0x9D, 0x01, 0x00, 0x38, 0x01, 0x45, 0x49,
				0x38, 0x52, 0x38, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00,
				0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1);
			await IirChar(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x93, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0xEC, 0xC1, 0x83, 0x43, 0x15, 0xA4, 0x70, 0xD1, 0x40, 0x1D, 0xA0, 0x4A, 0x8E,
				0x43, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x00, 0x00, 0x80, 0x3F, 0x18, 0xF6, 0xC4, 0xC0, 0x02, 0x05, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x76, 0x72, 0x4F, 0x60, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0xEF, 0x10, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45,
				0xEF, 0x10, 0x76, 0x72, 0x4F, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x10, 0x00, 0x00, 0x00, 0x40, 0x46, 0x2E, 0x93, 0xB0, 0x77, 0x5A, 0xED, 0x00, 0x66, 0xF3, 0xF7, 0xC0,
				0x5E, 0x49, 0xD9, 0xB0, 0x57, 0x45, 0xEF, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x50, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x5E, 0x54, 0x99, 0x70, 0x00, 0x00, 0x00, 0x00, 0x77, 0x5A, 0xED, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x76, 0x72, 0x4F, 0x60, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5E, 0x49, 0xD9, 0xB0, 0x66, 0xF3, 0xF7, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x74, 0x2B, 0x90,
				0x00, 0x00, 0x00, 0x7F, 0x25, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x74, 0xBF, 0xE6, 0x10, 0x00,
				0x00, 0x00, 0x00, 0x74, 0xBF, 0xE6, 0x20, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x82, 0x03, 0x00, 0x00, 0x00, 0xD7, 0x0A, 0x06, 0x53, 0x61, 0x64, 0x64, 0x75, 0x72, 0x22, 0x4B,
				0x08, 0x02, 0x10, 0x00, 0x18, 0x03, 0x20, 0x69, 0x28, 0xA3, 0xEB, 0xC7, 0x01, 0x30, 0x9C, 0x31, 0x38, 0x9C, 0x31, 0x40, 0x9C, 0x31, 0x48, 0x9C, 0x31, 0x50, 0x9C, 0x31, 0x58, 0x9C,
				0x31, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 0x88, 0x01, 0xB0, 0xEB, 0xC7, 0x01, 0x90, 0x01, 0x01, 0x98, 0x01, 0x01, 0xA0, 0x01, 0x64, 0xA8,
				0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xE8, 0x01, 0x03, 0x90, 0x02, 0x01, 0xA0, 0x02, 0x02, 0x2A, 0x11, 0x08, 0x0F, 0x10, 0x0A, 0x18, 0x05, 0x25, 0xB1, 0x0B, 0x00, 0x00, 0x28, 0x01,
				0x48, 0x00, 0x70, 0x04, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00, 0xA0, 0x01, 0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x06,
				0x12, 0x53, 0x0A, 0x14, 0x0D, 0x57, 0xA5, 0xF9, 0xF2, 0x15, 0x04, 0x34, 0x01, 0xEA, 0x1D, 0x6A, 0x2F, 0x32, 0x40, 0x25, 0x44, 0x28, 0x62, 0x3D, 0x12, 0x14, 0x0D, 0x57, 0xA5, 0xF9,
				0xF2, 0x15, 0x04, 0x34, 0x01, 0xEA, 0x1D, 0x6A, 0x2F, 0x32, 0x40, 0x25, 0x44, 0x28, 0x62, 0x3D, 0x1A, 0x14, 0x0D, 0x57, 0xA5, 0xF9, 0xF2, 0x15, 0x04, 0x34, 0x01, 0xEA, 0x1D, 0x6A,
				0x2F, 0x32, 0x40, 0x25, 0x44, 0x28, 0x62, 0x3D, 0x25, 0x36, 0xFC, 0x03, 0x00, 0x2D, 0x36, 0xFC, 0x03, 0x00, 0x38, 0x05, 0x45, 0x50, 0x51, 0x46, 0x52, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x03, 0xF1);
			await IirChar(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x94, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0x9A, 0x59, 0x85, 0x43, 0x15, 0x69, 0x91, 0xBD, 0x40, 0x1D, 0xCD, 0xCC, 0x92,
				0x43, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x00, 0x00, 0x80, 0x3F, 0x18, 0xF6, 0xC4, 0xC0, 0x02, 0x05, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x87, 0x4D, 0x2C, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x87, 0x4D, 0x2C, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x77, 0x5A, 0xEE, 0x60, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5A,
				0x99, 0x30, 0x30, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x6C, 0x39, 0xB4, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
				0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x55, 0xC1, 0xDB, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x6C, 0x39, 0xB6, 0x10, 0x00, 0x00, 0x00, 0x00, 0x41, 0x08, 0x00, 0x82, 0x03, 0x00, 0x00, 0x01, 0x3C, 0x0A, 0x0D, 0x4D, 0x61, 0x6C, 0x65, 0x20, 0x54, 0x65, 0x6D,
				0x70, 0x6C, 0x61, 0x74, 0x65, 0x22, 0x4D, 0x08, 0x02, 0x10, 0x00, 0x18, 0x01, 0x20, 0x64, 0x28, 0xA7, 0xC8, 0x08, 0x30, 0x9C, 0x31, 0x38, 0x9C, 0x31, 0x40, 0x9C, 0x31, 0x48, 0x9C,
				0x31, 0x50, 0x9C, 0x31, 0x58, 0x9C, 0x31, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 0x88, 0x01, 0xBB, 0xD8, 0x0B, 0x90, 0x01, 0x01, 0x98, 0x01,
				0x01, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xE8, 0x01, 0x00, 0x90, 0x02, 0x01, 0xA0, 0x02, 0x03, 0xA8, 0x02, 0xB2, 0x06, 0x2A, 0x13, 0x08, 0x14, 0x10, 0x05,
				0x18, 0x01, 0x25, 0x77, 0x0A, 0x00, 0x00, 0x28, 0x01, 0x48, 0x80, 0x80, 0x70, 0x70, 0x04, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00,
				0xA0, 0x01, 0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x10, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x92, 0x4F, 0x7C, 0x6C, 0x15, 0xFE, 0xF0, 0x9F, 0x5E, 0x1D, 0xC2, 0xD7, 0xF8, 0xC4,
				0x25, 0xAB, 0x94, 0x48, 0x43, 0x12, 0x14, 0x0D, 0x92, 0x4F, 0x7C, 0x6C, 0x15, 0xFE, 0xF0, 0x9F, 0x5E, 0x1D, 0xC2, 0xD7, 0xF8, 0xC4, 0x25, 0xAB, 0x94, 0x48, 0x43, 0x1A, 0x14, 0x0D,
				0x92, 0x4F, 0x7C, 0x6C, 0x15, 0xFE, 0xF0, 0x9F, 0x5E, 0x1D, 0xC2, 0xD7, 0xF8, 0xC4, 0x25, 0xAB, 0x94, 0x48, 0x43, 0x25, 0x12, 0x68, 0x03, 0x00, 0x2D, 0x12, 0x68, 0x03, 0x00, 0x38,
				0x01, 0x45, 0x31, 0x53, 0x4F, 0x30, 0xBA, 0x01, 0x57, 0x08, 0x12, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x99, 0xBF, 0x60, 0xDD, 0x15, 0xF6, 0x73, 0x09, 0x76, 0x1D, 0xCA, 0xED, 0x49, 0x79,
				0x25, 0xD2, 0xC7, 0xED, 0x8D, 0x12, 0x14, 0x0D, 0x99, 0xBF, 0x60, 0xDD, 0x15, 0xF6, 0x73, 0x09, 0x76, 0x1D, 0xCA, 0xED, 0x49, 0x79, 0x25, 0xD2, 0xC7, 0xED, 0x8D, 0x1A, 0x14, 0x0D,
				0x99, 0xBF, 0x60, 0xDD, 0x15, 0xF6, 0x73, 0x09, 0x76, 0x1D, 0xCA, 0xED, 0x49, 0x79, 0x25, 0xD2, 0xC7, 0xED, 0x8D, 0x25, 0x15, 0x4B, 0x33, 0x00, 0x2D, 0x15, 0x4B, 0x33, 0x00, 0x38,
				0x01, 0x45, 0x35, 0x50, 0x55, 0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
				0xF1, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1);
			await IirChar(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x95, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0x9A, 0x29, 0x84, 0x43, 0x15, 0x24, 0x67, 0xC0, 0x40, 0x1D, 0x98, 0x8E, 0x8A,
				0x43, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x00, 0x00, 0x80, 0x3F, 0x18, 0xF6, 0xC4, 0xC0, 0x02, 0x05, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x55, 0xC1, 0xDB, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x55, 0xC1, 0xDC, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x5B,
				0x60, 0x90, 0x5F, 0x4F, 0x16, 0x00, 0x00, 0x00, 0x00, 0x40, 0x55, 0xC1, 0xDB, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6B, 0xAE, 0xD0, 0x20, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x55, 0xC1, 0xDC, 0x2C, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
				0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x57, 0xC4, 0xA0, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x82, 0x03, 0x00, 0x00, 0x01, 0x94, 0x0A, 0x0F, 0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x20, 0x54,
				0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65, 0x22, 0x49, 0x08, 0x03, 0x10, 0x00, 0x18, 0x01, 0x20, 0x64, 0x28, 0x85, 0x8F, 0x01, 0x30, 0x9C, 0x31, 0x38, 0x9C, 0x31, 0x40, 0x9C, 0x31,
				0x48, 0x9C, 0x31, 0x50, 0x9C, 0x31, 0x58, 0x9C, 0x31, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 0x88, 0x01, 0x8A, 0xC5, 0x0C, 0x90, 0x01, 0x01,
				0x98, 0x01, 0x01, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xE8, 0x01, 0x00, 0x90, 0x02, 0x01, 0xA0, 0x02, 0x03, 0x2A, 0x13, 0x08, 0x14, 0x10, 0x05, 0x18, 0x01,
				0x25, 0x78, 0x0A, 0x00, 0x00, 0x28, 0x01, 0x48, 0x80, 0x80, 0x70, 0x70, 0x04, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00, 0xA0, 0x01,
				0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x05, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xE7, 0x0E, 0x53, 0x82, 0x15, 0x3F, 0x46, 0xC4, 0x11, 0x1D, 0xE4, 0x1C, 0xAB, 0x25, 0x25, 0x6F,
				0xA0, 0xAB, 0xD1, 0x12, 0x14, 0x0D, 0xE7, 0x0E, 0x53, 0x82, 0x15, 0x3F, 0x46, 0xC4, 0x11, 0x1D, 0xE4, 0x1C, 0xAB, 0x25, 0x25, 0x6F, 0xA0, 0xAB, 0xD1, 0x1A, 0x14, 0x0D, 0xE7, 0x0E,
				0x53, 0x82, 0x15, 0x3F, 0x46, 0xC4, 0x11, 0x1D, 0xE4, 0x1C, 0xAB, 0x25, 0x25, 0x6F, 0xA0, 0xAB, 0xD1, 0x25, 0xE2, 0xF1, 0x04, 0x00, 0x2D, 0xE2, 0xF1, 0x04, 0x00, 0x38, 0x01, 0x45,
				0x39, 0x57, 0x39, 0x4D, 0xBA, 0x01, 0x57, 0x08, 0x10, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xFE, 0xC2, 0x7A, 0x03, 0x15, 0xBF, 0x46, 0xE0, 0x60, 0x1D, 0x28, 0x3D, 0x92, 0x84, 0x25, 0x52,
				0xF2, 0xD0, 0xBA, 0x12, 0x14, 0x0D, 0xFE, 0xC2, 0x7A, 0x03, 0x15, 0xBF, 0x46, 0xE0, 0x60, 0x1D, 0x28, 0x3D, 0x92, 0x84, 0x25, 0x52, 0xF2, 0xD0, 0xBA, 0x1A, 0x14, 0x0D, 0xFE, 0xC2,
				0x7A, 0x03, 0x15, 0xBF, 0x46, 0xE0, 0x60, 0x1D, 0x28, 0x3D, 0x92, 0x84, 0x25, 0x52, 0xF2, 0xD0, 0xBA, 0x25, 0x50, 0xF2, 0x04, 0x00, 0x2D, 0x50, 0xF2, 0x04, 0x00, 0x38, 0x01, 0x45,
				0x31, 0x43, 0x50, 0x4F, 0xBA, 0x01, 0x57, 0x08, 0x12, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x99, 0xBF, 0x60, 0xDD, 0x15, 0xF6, 0x73, 0x09, 0x76, 0x1D, 0xCA, 0xED, 0x49, 0x79, 0x25, 0xD2,
				0xC7, 0xED, 0x8D, 0x12, 0x14, 0x0D, 0x99, 0xBF, 0x60, 0xDD, 0x15, 0xF6, 0x73, 0x09, 0x76, 0x1D, 0xCA, 0xED, 0x49, 0x79, 0x25, 0xD2, 0xC7, 0xED, 0x8D, 0x1A, 0x14, 0x0D, 0x99, 0xBF,
				0x60, 0xDD, 0x15, 0xF6, 0x73, 0x09, 0x76, 0x1D, 0xCA, 0xED, 0x49, 0x79, 0x25, 0xD2, 0xC7, 0xED, 0x8D, 0x25, 0x15, 0x4B, 0x33, 0x00, 0x2D, 0x15, 0x4B, 0x33, 0x00, 0x38, 0x01, 0x45,
				0x35, 0x50, 0x55, 0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00,
				0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1);
			await IirChar(0x00, 0x00, 0xC3, 0x50, 0x00, 0x09, 0xFF, 0x96, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0x16, 0x30, 0x85, 0x43, 0x15, 0xA6, 0x9B, 0x1E, 0x41, 0x1D, 0x57, 0x9C, 0x9E,
				0x43, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x55, 0x15, 0xDA, 0x3D, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x5D, 0x8B, 0x7E, 0x3F, 0x18, 0xF6, 0xC4, 0xC0, 0x02, 0x05, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5F, 0x75, 0xF7, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x5F, 0x75, 0xF8, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x5B,
				0x60, 0x90, 0x56, 0xE5, 0x12, 0xF0, 0x00, 0x00, 0x00, 0x40, 0x5F, 0x75, 0xF7, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x10, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x45, 0xB1, 0x8F, 0xF0, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x5F, 0x75, 0xF8, 0x1C, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
				0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x57, 0x66, 0xDC, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x08, 0x00, 0x82, 0x03, 0x00, 0x00, 0x01, 0xF1, 0x0A, 0x0E, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x20,
				0x53, 0x6C, 0x61, 0x76, 0x65, 0x72, 0x22, 0x4D, 0x08, 0x02, 0x10, 0x00, 0x18, 0x03, 0x20, 0x64, 0x28, 0xA6, 0xC8, 0x08, 0x30, 0x9C, 0x31, 0x38, 0x9C, 0x31, 0x40, 0x9C, 0x31, 0x48,
				0x9C, 0x31, 0x50, 0x9C, 0x31, 0x58, 0x9C, 0x31, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 0x88, 0x01, 0xBE, 0xD8, 0x0B, 0x90, 0x01, 0x01, 0x98,
				0x01, 0x01, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xE8, 0x01, 0x03, 0x90, 0x02, 0x01, 0xA0, 0x02, 0x01, 0xA8, 0x02, 0xB2, 0x06, 0x2A, 0x13, 0x08, 0x0F, 0x10,
				0x0A, 0x18, 0x05, 0x25, 0xCB, 0x03, 0x00, 0x00, 0x28, 0x01, 0x48, 0x80, 0x80, 0x70, 0x70, 0x04, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50,
				0x00, 0xA0, 0x01, 0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x0B, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x06, 0x2C, 0x1E, 0xCC, 0x15, 0x1B, 0x16, 0xF6, 0xEC, 0x1D, 0xCB, 0xB5, 0xEC,
				0xE5, 0x25, 0x59, 0x2C, 0x91, 0xAC, 0x12, 0x14, 0x0D, 0x06, 0x2C, 0x1E, 0xCC, 0x15, 0x1B, 0x16, 0xF6, 0xEC, 0x1D, 0xCB, 0xB5, 0xEC, 0xE5, 0x25, 0x59, 0x2C, 0x91, 0xAC, 0x1A, 0x14,
				0x0D, 0x06, 0x2C, 0x1E, 0xCC, 0x15, 0x1B, 0x16, 0xF6, 0xEC, 0x1D, 0xCB, 0xB5, 0xEC, 0xE5, 0x25, 0x59, 0x2C, 0x91, 0xAC, 0x25, 0x43, 0x63, 0x04, 0x00, 0x2D, 0x43, 0x63, 0x04, 0x00,
				0x38, 0x01, 0x45, 0x4D, 0x49, 0x39, 0x5A, 0xBA, 0x01, 0x57, 0x08, 0x0E, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x16, 0xC2, 0x29, 0x4C, 0x15, 0x2D, 0x26, 0xDF, 0x5C, 0x1D, 0x49, 0x32, 0xC4,
				0x63, 0x25, 0x88, 0x80, 0x76, 0x2D, 0x12, 0x14, 0x0D, 0x16, 0xC2, 0x29, 0x4C, 0x15, 0x2D, 0x26, 0xDF, 0x5C, 0x1D, 0x49, 0x32, 0xC4, 0x63, 0x25, 0x88, 0x80, 0x76, 0x2D, 0x1A, 0x14,
				0x0D, 0x16, 0xC2, 0x29, 0x4C, 0x15, 0x2D, 0x26, 0xDF, 0x5C, 0x1D, 0x49, 0x32, 0xC4, 0x63, 0x25, 0x88, 0x80, 0x76, 0x2D, 0x25, 0x35, 0xE8, 0x02, 0x00, 0x2D, 0x35, 0xE8, 0x02, 0x00,
				0x38, 0x01, 0x45, 0x54, 0x43, 0x52, 0x4D, 0xBA, 0x01, 0x57, 0x08, 0x10, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x40, 0x68, 0xC1, 0x40, 0x15, 0x2C, 0x43, 0xBF, 0xB8, 0x1D, 0x2E, 0xBF, 0x12,
				0x32, 0x25, 0x5B, 0xCE, 0x97, 0xE3, 0x12, 0x14, 0x0D, 0x40, 0x68, 0xC1, 0x40, 0x15, 0x2C, 0x43, 0xBF, 0xB8, 0x1D, 0x2E, 0xBF, 0x12, 0x32, 0x25, 0x5B, 0xCE, 0x97, 0xE3, 0x1A, 0x14,
				0x0D, 0x40, 0x68, 0xC1, 0x40, 0x15, 0x2C, 0x43, 0xBF, 0xB8, 0x1D, 0x2E, 0xBF, 0x12, 0x32, 0x25, 0x5B, 0xCE, 0x97, 0xE3, 0x25, 0x40, 0x63, 0x04, 0x00, 0x2D, 0x40, 0x63, 0x04, 0x00,
				0x38, 0x01, 0x45, 0x4E, 0x59, 0x53, 0x50, 0xBA, 0x01, 0x57, 0x08, 0x12, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xFA, 0x05, 0xD8, 0xA8, 0x15, 0x3C, 0x56, 0x22, 0x32, 0x1D, 0xF4, 0x19, 0x78,
				0x9D, 0x25, 0x48, 0x8C, 0xC4, 0x58, 0x12, 0x14, 0x0D, 0xFA, 0x05, 0xD8, 0xA8, 0x15, 0x3C, 0x56, 0x22, 0x32, 0x1D, 0xF4, 0x19, 0x78, 0x9D, 0x25, 0x48, 0x8C, 0xC4, 0x58, 0x1A, 0x14,
				0x0D, 0xFA, 0x05, 0xD8, 0xA8, 0x15, 0x3C, 0x56, 0x22, 0x32, 0x1D, 0xF4, 0x19, 0x78, 0x9D, 0x25, 0x48, 0x8C, 0xC4, 0x58, 0x25, 0x7B, 0x18, 0x02, 0x00, 0x2D, 0x7B, 0x18, 0x02, 0x00,
				0x38, 0x01, 0x45, 0x59, 0x34, 0x46, 0x48, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
				0x03, 0x24, 0x3F, 0x44, 0xC5, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x43, 0x3D, 0xFD, 0x80, 0xD4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1,
				0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1);

			await IirId(GameServer0X2000Opcodes.SimpleCharFullUpdateIIR, [0x00, 0x00, 0xC3, 0x50], 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0x00, 0x00, 0x82, 0x43, 0x15, 0x00, 0x00, 0x00, 0x00,
				0x1D, 0x00, 0x00, 0x91, 0x43, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0x00, 0x00, 0x80, 0x3F, 0x18, 0xF6, 0xC4,
				0xC0, 0x02, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6F, 0x00, 0x00, 0x00, 0x00, 0x41, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x82, 0x08, 0x00, 0x00, 0x00, 0x72, 0x0A, 0x11, 0x43, 0x68, 0x61, 0x72,
				0x61, 0x63, 0x74, 0x65, 0x72, 0x33, 0x35, 0x30, 0x32, 0x31, 0x35, 0x31, 0x32, 0x22, 0x3E, 0x08, 0x00, 0x10, 0x00, 0x18, 0x01, 0x20, 0x64, 0x28, 0x00, 0x30, 0xC0, 0x3E, 0x38, 0xC0,
				0x3E, 0x40, 0x88, 0x27, 0x48, 0x64, 0x50, 0x00, 0x58, 0x00, 0x60, 0x00, 0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x01, 0x88, 0x01, 0x00, 0x90, 0x01, 0x01, 0x98, 0x01, 0x01, 0xA0, 0x01,
				0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x08, 0xE8, 0x01, 0x00, 0x90, 0x02, 0x01, 0xA0, 0x02, 0x05, 0x32, 0x08, 0x58, 0x0A, 0x68, 0x81, 0x06, 0x80, 0x01, 0x00, 0x3A, 0x0D, 0x08,
				0xF8, 0xDC, 0x04, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18, 0x01, 0x50, 0x00, 0xA0, 0x01, 0x00, 0xA8, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x02,
				0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1);

			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x59), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameInterfaceOpcodes.DispatchPassBlob,
				b => b
					.WriteArray(0x00, 0x00, 0x1F, 0x8E, 0x73, 0x7A, 0x6D, 0xF9, 0x00, 0x00, 0xC3, 0x50)
					.WriteUInt32(id)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x44, 0x28, 0x81, 0x06, 0x32, 0x0E, 0x0A, 0x07, 0x08, 0x65, 0x10)
					.WriteVarint(id)
					.WriteArray(0x10, 0x84, 0x08, 0x18, 0x00, 0x32, 0x0D, 0x0A, 0x07, 0x08, 0x68, 0x10)
					.WriteVarint(id)
					.WriteArray(0x10, 0x18, 0x18, 0x00, 0x32, 0x0D, 0x0A, 0x07, 0x08, 0x70, 0x10)
					.WriteVarint(id)
					.WriteArray(0x10, 0x30, 0x18, 0x0C, 0x32, 0x0D, 0x0A, 0x07, 0x08, 0x71, 0x10)
					.WriteVarint(id)
					.WriteArray(EditorAvatarSnapshot)));

			// GameTime: CONSTRUCTED from the captured day-time + rate (#189 item 3). The resync timestamp is generated
			// from the wall clock the same way the world-entry clock is (GameTimeCodec.ResyncTimestamp) rather than
			// replaying the capture's; dayTime stays captured until its unit is pinned (#351).
			// The editor's captured time of day (0x4743EB00 = 50155s = 13:55) with a generated resync anchor. The backdrop
			// has no day/night to track, so this stays the captured value rather than the live clock.
			var (editorGameDay, editorUnixSeconds) = GameTimeCodec.ResyncAnchor(now ?? DateTimeOffset.UtcNow);
			await Iir(GameServer0X2000Opcodes.GameTimeIIR, CreateEditorRecordBuilder.BuildGameTimeIir(BitConverter.UInt32BitsToSingle(0x4743EB00), 1u, editorGameDay, editorUnixSeconds));

			// #189: constructed via BuildStatIir (was verbatim). flag=0 for 0x0F4266, then a flag=1 / value=0 run 0x0F4285..0x0F428C.
			await Iir(GameServer0X2000Opcodes.StatIIR, CreateEditorRecordBuilder.BuildStatIir(EntityClass.Character, id, 0x00, 0x0F4266, 0x00));

			for (uint statId = 0x0F4285; statId <= 0x0F428C; statId++)
				await Iir(GameServer0X2000Opcodes.StatIIR, CreateEditorRecordBuilder.BuildStatIir(EntityClass.Character, id, 0x01, statId, 0x00));

			return;

			Task Iir(GameServer0X2000Opcodes iir, params byte[] body)
			{
				return SendIir(client, sender, receiver, iir, body);
			}

			// Emits a SimpleChar scene-pedestal record, RECONSTRUCTED from its own bytes (transform + identity
			// rebuilt, pre-IIR scaffold carried, session playfield threaded - CreateEditorCharBuilder). The live
			// "Character..." preview stays on IirId (it carries the live client instance as its handle).
			Task IirChar(params byte[] verbatim)
			{
				return Iir(GameServer0X2000Opcodes.SimpleCharFullUpdateIIR, CreateEditorCharBuilder.Reconstruct(verbatim, CreateEditorCharBuilder.EditorPlayfieldInstance)!);
			}

			Task IirId(GameServer0X2000Opcodes iir, byte[] prefix, params byte[] suffix)
			{
				return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(x => x.WriteUInt32(iir).WriteArray(prefix).WriteUInt32(id).WriteArray(suffix))));
			}
		}

		// #197 deterministic trigger: the client sends Finish + its SetMorphValue batch in one burst; retail sends
		// the post-morph trio once that recv batch is fully received. Called from the listener's batch-drained hook
		// (ReceivedBatchComplete). No-op until Finish/a morph has been seen this session, and sends exactly once.
		internal static Task FlushPostMorphBurstOnBatchDrained(INetworkClient client)
		{
			return TrySendPostMorphBurstOnce(client);
		}

		// The create-editor 0x2000 receiver: the player object (GameInterfaceType) addressed to this session's
		// client instance. Built as a typed ProtoEndpoint so the type+instance can't drift (the old hardcoded
		// receiver baked in 0x021662C8; see Endpoints / ProtoEndpointGoldenTests).
		internal static ProtoEndpoint Receiver(INetworkClient client)
		{
			return client.Endpoint(Endpoints.GameInterfaceType, client.Session.Account.ClientInstance);
		}

		// Retail create-editor CHARACTER-CREATION SCRIPT burst from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (GameServer port 7030), rows 274-302/324-331.
		//
		// This is the burst that starts the character-creation cutscene. The key packets are the two ScryScriptIIR
		// remoteformats: token "playorskip(72)|...|NEW_character_creation Slave Galley (server-side)" then "PlayOrSkip".
		// Without them the playfield loads (ambient audio) but the Slave Galley scene never launches (black screen).
		// The preceding StatUpdate / BehaviorControlCenter / MagicBuff / Energy / Stat packets set up the initial
		// character/world state the script expects; the trailing SetMorphValue packets are part of the same setup.
		// All packets ride one conversation (sender hash("GameAgent") sub-0x59 -> receiver hash("GameInterface") +
		// instance varint) and reuse the editor temp instance; a byte-for-byte replay reproduces retail exactly
		// (locked by BurstGoldenTests). The 32-bit values are the client's IIR message-type hashes (see
		// GameServer0X2000Opcodes / docs/re/aoc-opcode-ids.md); bodies are kept verbatim - they are opaque
		// editor-state (entity handles 0x0009FFxx, morph mesh names, stat blobs) the client choreography expects.

		internal static async Task SendRetailCreateEditorCreationScriptBurst(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			var sender = Endpoints.Endpoint(Endpoints.GameAgentType, 0x59);
			var receiver = Endpoints.Endpoint(Endpoints.GameInterfaceType, id);

			const uint W = EntityClass.Character; // EntityClass.Character (0xC350)

			// Initial vitals + behavior/action setup for the editor entities (handles 0x0009FF92..96).
			await Iir(GameServer0X2000Opcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF92u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000Opcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF93u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000Opcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF94u, 5, 85843, 1, 24, 0));
			await Iir(GameServer0X2000Opcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF94u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000Opcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF95u, 7, 41413, 1, 24, 0));
			await Iir(GameServer0X2000Opcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF95u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000Opcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF96u, 7, 41413, 1, 24, 0));
			await Iir(GameServer0X2000Opcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF96u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));

			await Iir(GameServer0X2000Opcodes.StatIIR, CreateEditorRecordBuilder.BuildStatIir(W, id, 0x00, 0x0360, 0x02));

			// MagicBuff carries the instance twice (caster + target).
			await client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000Opcodes.MagicBuffIIR)
					.WriteArray(0x00, 0x00, 0xC3, 0x50)
					.WriteUInt32(id)
					.WriteArray(0x00, 0x00, 0x00, 0x0B, 0x90, 0x00, 0x00, 0x00, 0xC3, 0x50)
					.WriteUInt32(id)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x90, 0x00, 0x00,
						0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3D, 0x5F, 0xC9, 0x66))));

			await Iir(GameServer0X2000Opcodes.EnergyIIR, CreateEditorRecordBuilder.BuildEnergyIir(W, id, 0x01, 0x00));

			// Stat run (flag=1, value=0): entity stat ids 0x0566..0x0573, then 0x0575 and 0x0580.
			foreach (var statId in (uint[])[0x0566, 0x0567, 0x0568, 0x0569, 0x056A, 0x056B, 0x056C, 0x056D, 0x056E, 0x056F, 0x0570, 0x0571, 0x0572, 0x0573, 0x0575, 0x0580])
				await Iir(GameServer0X2000Opcodes.StatIIR, CreateEditorRecordBuilder.BuildStatIir(W, id, 0x01, statId, 0x00));

			await Iir(GameServer0X2000Opcodes.StatIIR, CreateEditorRecordBuilder.BuildStatIir(W, id, 0x00, 0x0F428F, 0x00));

			await Iir(GameServer0X2000Opcodes.StatUpdateIIR,
				CreateEditorRecordBuilder.BuildStatUpdateIir(id, [(0x1Bu, 0x1F40u), (0xBAu, 0x00u), (0x1FFu, 0x00u), (0x200u, 0x301u), (0x201u, 0x00u), (0x25Fu, 0x59EA5C54u)]));

			// ScryScriptIIR: the create-world "PlayOrSkip(72) Slave Galley" remoteformat + its RDB resource manifest, 			// then the "PlayOrSkip" ScriptMessage (decoded by tools/capture/decode_scryscript.py).
			await IirRecord(ScryScript.BuildStep(id, CreateWorldScriptToken, 7, resources: CreateWorldResourceManifest, f6Field1: 6177372, f6Field2: 9422303));
			await IirRecord(ScryScript.BuildStep(id, "PlayOrSkip"));

			// SetMorphValue setup (hair meshes, then a texscript) on the editor entities.
			await Iir(GameServer0X2000Opcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF94u, "hairmesh_207920", 1.0f, 0x00, 0x00000001u));
			await Iir(GameServer0X2000Opcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF95u, "hairmesh_203666", 1.0f, 0x00, 0x00000001u));
			await Iir(GameServer0X2000Opcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF94u, 5, 85843, 2, 24, 0));
			await Iir(GameServer0X2000Opcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(id, "texscript_183875", 1.0f, 0x01, 0x00000001u));
			await Iir(GameServer0X2000Opcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF95u, "texscript_183875", 1.0f, 0x01, 0x00000002u));
			await Iir(GameServer0X2000Opcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF94u, "texscript_183875", 1.0f, 0x01, 0x00000002u));

			return;

			// One IIR packet: 0x2000 envelope -> [u32 length][u32 IIR-id][body]. The length is recomputed, so editing
			// a body never desyncs it.
			Task Iir(GameServer0X2000Opcodes iir, params byte[] body)
			{
				return SendIir(client, sender, receiver, iir, body);
			}

			// Sends a fully-built IIR record (one that already begins with its sub-opcode, e.g. ScryScript.BuildStep);
			// the length prefix is recomputed by WriteArrayUInt32Length.
			Task IirRecord(byte[] record)
			{
				return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
			}
		}

		internal static Task SendRetailCreateEditorPostReadyAck(INetworkClient client, uint requestId)
		{
			return client.SendAsync(new Rpc(RetailCreateEditorPostReadyAckSender, client.Endpoint(Endpoints.GameCharInterfaceProjectType, client.Session.Account.ClientInstance),
				GameInterfaceOpcodes.DispatchPassBlob, b => b
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(requestId)
					.WriteUInt32(requestId == 0x0009FF95
						? 0x00000000u
						: 0x00000002u)));
		}

		// Verbatim replay of the fixed tail of the retail create-editor post-ready follow-up from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (rows 198-199, 203-204).
		// The leading 0x0009FF92-0x0009FF96 ack packets are generated dynamically from the request ids the
		// client actually sends so the live flow can advance even when it stalls before all 5 retail requests.
		internal static async Task SendRetailCreateEditorPostReadyBurst(INetworkClient client, IReadOnlyList<uint> requestIds)
		{
			foreach (var requestId in requestIds)
				await SendRetailCreateEditorPostReadyAck(client, requestId);

			var id = client.Session.Account.ClientInstance;
			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x59), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, id),
				GameCharInterfaceOpcodes.DispatchValidResurrectionSpots,
				(byte[])
				[
					0x43, 0xA7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4C, 0xCD, 0x00, 0x00, 0x07, 0xE2, 0x43, 0xA7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4C, 0xCD, 0x00, 0x00,
					0xC7, 0x9E, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x00
				]));
			await SendIir(client, Endpoints.Endpoint(Endpoints.GameAgentType, 0x59), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameServer0X2000Opcodes.CharInPlayIIR,
				CreateEditorRecordBuilder.BuildCharInPlayIir(id));

			// The racial CHARACTER-TEMPLATE choice list the creation editor reads (Male/Female/StygianSlaver ->
			// template ids 50000:65525x), delivered on two GameActionListIIR (0xF508F4C1) records for dynel 0xD00C.
			// The two captured variants differ ONLY in the leading action-id word; the property-map action-tail
			// grammar is still opaque (#190) so it rides verbatim, while the record HEADER is constructed via
			// GameActionListCodec.BuildBody (construct-don't-replay - see SendGameActionListRecord).
			await SendGameActionListRecord(client, 0x59, id, 0xD00Cu,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x39, 0xBF, 0x93, 0x00, 0x50, 0x4D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65,
				0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x32, 0x3B, 0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35,
				0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x33, 0x3B, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x53, 0x6C, 0x61, 0x76, 0x65, 0x72, 0x3D, 0x35, 0x30, 0x30, 0x30,
				0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00
			]);
			await SendGameActionListRecord(client, 0x59, id, 0xD00Cu,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x01, 0x9D, 0xD1, 0x00, 0x50, 0x4D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65,
				0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x32, 0x3B, 0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35,
				0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x33, 0x3B, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x53, 0x6C, 0x61, 0x76, 0x65, 0x72, 0x3D, 0x35, 0x30, 0x30, 0x30,
				0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00
			]);

			// After the post-ready editor handoff, retail streams the character-creation script burst
			// (rows 274-331) that actually launches the Slave Galley creation scene via the
			// 0xA36D3B74 "NEW_character_creation" remoteformat. Without it the playfield loads (audio)
			// but the editor never appears (black screen).
			await SendRetailCreateEditorCreationScriptBurst(client);
		}

		// Step 3 of the create->world handshake: the world-prep / scene burst the creator sends after
		// CharCreationFinished, before the client will send 0x206E (enter world). Verbatim from
		// Captures/Dikketest_rogue-barbarian_create_random-actions.tshark.json (creator gs013:7016, 		// idx 364-438, excluding the 0x2082/0x2009 keepalive replies which are answered naturally), with the
		// client instance retargeted from dikketest's 0x01316D2B to the local temp instance 0x021662C8
		// (receiver varint AB DA C5 09 -> C8 C5 D9 10, body instance 01 31 6D 2B -> 02 16 62 C8). Each packet
		// is re-serialized (as an Rpc) so its CRC/length are recomputed for the retargeted bytes.

		// Consumes (removes) a pending create-world-entry marker for `clientInstance`, returning true if one was set.
		// The listener's world Hello calls this to recognise the post-creation world connection (and skip the helper
		// auth), then take the create-world entry path; consuming it means a later normal login doesn't re-trigger the intro.
		internal static bool TryConsumePendingCreateWorldEntry(uint clientInstance)
		{
			return PendingCreateWorldEntry.TryRemove(clientInstance, out _);
		}

		// Persists the chosen character name so the subsequent world-entry Hello (a fresh session that
		// re-loads the character from the database) sees a named character. Tolerates duplicate-name
		// failures (unique index) without dropping the editor connection.
		// Lazily creates and tracks the character's body row so the editor's morph / head-mesh / size / voice
		// edits have somewhere to persist (CharacterBodies was previously never populated). The body is saved
		// together with the character when the name is committed.
		private static CharacterBody EnsureBody(Character character)
		{
			// In-memory only: the body accumulates editor edits on the cached session character and is persisted as
			// part of the character graph at name-commit (SaveCharacter). No DB context is held during editing.
			character.Body ??= new CharacterBody
			{
				CharacterId = character.Id
			};
			return character.Body;
		}

		// The post-creation WORLD connection: the second (world) connection opened after the 0x2002 enter-world
		// handoff, flagged in the Hello handler. Distinguishes it from the editor connection and from a normal
		// existing-character login so the intro/cutscene bursts only fire there.
		private static bool IsCreateWorldConnection(INetworkClient client)
		{
			return client.Session is { EntryMode: WorldEntryMode.CreateWorld, Character.Name: not null };
		}

		// The editor map's scene-prop WorldObjects, keyed by captured handle. Null when no DB is available.
		private static async Task<Dictionary<uint, WorldObject>?> LoadEditorSceneDynels(IDatabaseRepository? database)
		{
			if (database == null)
				return null;

			// Keyed by the row's DISPLAY ID, not its CapturedHandle - see EditorSceneDynels for why.
			var rows = new Dictionary<uint, WorldObject>();
			foreach (var o in await database.GetWorldObjects(StartingData.EditorMapId))
			{
				if (o.DisplayId is { } displayId)
					rows.TryAdd(displayId, o);
			}

			return rows;
		}

		// Appends the resource ref to the character's chosen-appearance list (deduplicated; the editor can
		// resend the set when a creation session is resumed). Persisted with the character graph at name-commit.
		private static void RecordBodyResource(Character character, string resourceRef)
		{
			character.BodyResources ??= [];
			if (character.BodyResources.Any(r => r.Ref == resourceRef))
				return;

			character.BodyResources.Add(new CharacterBodyResource
			{
				CharacterId = character.Id,
				Sequence = character.BodyResources.Count,
				Ref = resourceRef
			});
		}

		// Builds an outgoing 0xA36D3B74 script callback: subOpcode + ack(0xC350) + clientInstance + reserved(0) +
		// pbLength + a ProtoScryScriptIIR - the same wire shape (and the same message) the INBOUND
		// HandleCommandPacket already reads typed. It used to be written as raw tag bytes.
		private static Task SendA36DCallback(INetworkClient client, string token, string value)
		{
			return client.SendAsync(new Rpc(MorphAckSender, Receiver(client), GameInterfaceOpcodes.DispatchPassBlob, b => b.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(GameServer0X2000Opcodes.ScryScriptIIR);
				s.WriteUInt32(EntityClass.Character);
				s.WriteUInt32(client.Session.Account.ClientInstance);
				s.WriteByte(0x00);
				s.WriteProtobufUInt32Length(new ProtoScryScriptIIR
				{
					Version = 5,
					type = ProtoScryScriptIIR.Type.ScriptMessage,
					IntParameter = 0, // sint32 - zigzag(0) is 0, so this is the same `18 00` as before
					StringParameterOne = token,
					StringParameterTwo = value,
					IdParameter = new ProtoID32
					{
						Type = 0,
						Instance = 0
					}
				});
			})));
		}

		// Builds a creator-connection 0x2000 packet: subOpcode + ack(0xC350) + clientInstance + fixed tail.
		private static Task SendCreateEditor2000(INetworkClient client, uint subOpcode, byte[] tail, uint instance)
		{
			return client.SendAsync(new Rpc(MorphAckSender, Receiver(client), GameInterfaceOpcodes.DispatchPassBlob, b => b.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(subOpcode);
				s.WriteUInt32(EntityClass.Character);
				s.WriteUInt32(instance);
				s.WriteArray(tail);
			})));
		}

		// Sends a fully-built create-editor IIR record (one that already begins with its sub-opcode, e.g.
		// ScryScript.BuildStartScriptGroup) on the same conversation framing as SendCreateEditor2000.
		private static Task SendCreateEditor2000Record(INetworkClient client, byte[] record)
		{
			return client.SendAsync(new Rpc(MorphAckSender, Receiver(client), GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
		}

		// Emits a GameActionListIIR (0xF508F4C1) carrier for `dynel`, delivered to frame `id`. The fixed record
		// header (subop/type/Head/four/one, decoded 570/570) is CONSTRUCTED via GameActionListCodec.BuildBody and
		// the still-opaque property-map action tail rides verbatim; the carrier length is computed from the body.
		private static Task SendGameActionListRecord(INetworkClient client, byte agentSub, uint id, uint dynel, byte[] actionTail)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, agentSub), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(GameActionListCodec.BuildBody(id, dynel, 0u, actionTail))));
		}

		// The create-flow send idiom: one 0x2000 record ([u32 len][u32 iir opcode][body]) delivered via DispatchPassBlob.
		// Every editor/world-prep burst funnels its records through here instead of re-spelling the PacketStream chain.
		private static Task SendIir(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, GameServer0X2000Opcodes iir, params byte[] body)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(x => x.WriteUInt32(iir).WriteArray(body))));
		}

		// Sends the 0xBADF5A4B ack for one SetMorphValue. The wire layout (after the 0x2000 wrapper) is:
		//   subOpcode(0xBADF5A4B) ack(0xC350) clientInstance uint16(0) [byteLen morphValue] float byte(0) counter
		// The float is a server-side default that is not present in the client's message: textures
		// (values starting with '*') use 1.0, body morphs use -0.03 (the only two values seen in retail).
		private static Task SendMorphValueAck(INetworkClient client, string morphValue)
		{
			var instance = client.Session.Account.ClientInstance;
			var counter = client.Session.MorphAckCounter == 0
				? client.Session.MorphAckCounter = 2
				: ++client.Session.MorphAckCounter;
			// IEEE-754 bits written big-endian (the editor reads these floats big-endian here, unlike
			// FaolanStream.WriteFloat which is little-endian). 1.0f=0x3F800000 for textures (values starting
			// with '*'), -0.03f=0xBCF5C28F for body morphs - the only two values retail uses.
			var floatBits = morphValue.StartsWith("*", StringComparison.Ordinal)
				? 0x3F800000u
				: 0xBCF5C28Fu;

			return client.SendAsync(new Rpc(MorphAckSender, Receiver(client), GameInterfaceOpcodes.DispatchPassBlob, b => b.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(GameServer0X2000Opcodes.SetMorphValueIIR);
				s.WriteUInt32(EntityClass.Character);
				s.WriteUInt32(instance);
				s.WriteUInt16(0);
				s.WriteStringByteLength(morphValue);
				s.WriteUInt32(floatBits);
				s.WriteByte(0);
				s.WriteUInt32(counter);
			})));
		}

		// Reject a chosen name so the client re-prompts instead of dead-ending (no loading screen). Retail answers a
		// bad TheNameIs with the same 0xA36D3B74 callback as NicknameOk but a different token: NicknameTaken (already in
		// use, echoes the name) or NicknameInvalid (empty/unsaveable, empty value). Verbatim grammar from
		// Captures/Stricken_soldier-guardian_create_random-actions (frames 151 taken / 158 invalid / 193 ok). The client
		// then simply re-sends TheNameIs with a new name - no other packets are needed.
		private static Task SendNicknameRejected(INetworkClient client, string name, bool taken)
		{
			return taken
				? SendA36DCallback(client, "NicknameTaken", name)
				: SendA36DCallback(client, "NicknameInvalid", "");
		}

		// The fixed trio the server sends after the last morph; tails are verbatim from dikketest with the
		// client instance substituted. This is what nudges the client to send the TheNameIs callback.
		private static async Task SendPostMorphBurst(INetworkClient client)
		{
			var instance = client.Session.Account.ClientInstance;

			await SendCreateEditor2000(client, (uint)GameServer0X2000Opcodes.StatIIR, [0x01, 0x00, 0x00, 0x05, 0x78, 0x00, 0x00, 0x00, 0x00], instance);
			await SendCreateEditor2000Record(client, ScryScript.BuildStartScriptGroup(instance, 35820)); // StartScriptGroupOnClient (#223)
			await SendCreateEditor2000(client, (uint)GameServer0X2000Opcodes.StatUpdateIIR,
				[0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x2A, 0xF8, 0x00, 0x00, 0x01, 0xF9, 0x00, 0x00, 0x43, 0x30, 0x00, 0x00, 0x01, 0xFB, 0x00, 0x00, 0x00, 0x00],
				instance);
		}

		// A ScryScriptIIR step on the GameAgent->GameInterface conversation (the framing the verbatim burst uses).
		private static Task SendScry(INetworkClient client, byte[] scry)
		{
			var id = client.Session.Account.ClientInstance;
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameInterfaceType, id), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(scry)));
		}

		private static async Task TrySendPostMorphBurstOnce(INetworkClient client)
		{
			// MorphActivitySequence > 0 means Finish (or a morph) has been processed - i.e. the customization is
			// being committed. Guarded single-send so the batch-drain trigger and the fallback timer can't double it.
			if (client.Session.MorphActivitySequence == 0 || client.Session.PostMorphBurstSent)
				return;

			client.Session.PostMorphBurstSent = true;
			await SendPostMorphBurst(client);
		}

		// The outcome of committing a chosen name, mirroring the retail create-editor's NicknameOk / NicknameTaken /
		// NicknameInvalid callbacks (Captures/Stricken_soldier-guardian_create_random-actions: Stricken->taken,
		// empty->invalid, Strickenen->ok).
		private enum NameCommitResult
		{
			Committed,
			Taken,
			Invalid
		}
	}
}
