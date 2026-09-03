using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Session;
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
	// builders moved to TortageIntroService (#353); the cutscene flow calls them statically.
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

		// How long after CharCreationFinished the enter-world handoff goes out (#433). Retail's own gap across the 13
		// create captures that reach a redirect is 8.90-9.52s; 9.0 sits inside that band, near its floor, so the
		// client gets its creation fade at the retail length without an added wait.
		private static readonly TimeSpan EnterWorldHandoffDelay = TimeSpan.FromSeconds(9.0);

		// Character ids that have just been named in the create-editor and are about to open their FIRST world
		// connection (the second GameServer connection, after the 0x2002 enter-world handoff). Added when the name is
		// committed (HandleCommandPacket TheNameIs) and consumed by the listener's world Hello via
		// TryConsumePendingCreateWorldEntry, which is how that connection is recognised as the create-world entry (and
		// given the intro/cutscene) rather than a normal login. Static because the editor and world connections are
		// different sessions but hit the same listener/service instance; ConcurrentDictionary-as-set because it is
		// mutated from different connection threads (add on one connection, consume on the reconnect). Value unused.
		private readonly ConcurrentDictionary<uint, byte> PendingCreateWorldEntry = new ConcurrentDictionary<uint, byte>();

		// The burst-less world entry (WorldEntryService) - the constructed live-char world the create-flow enters
		// before the destiny cutscene overlay. A plain collaborator since the entry left the listener; it used to be
		// the graph's last BOUND seam, because a service cannot take a listener method in its constructor.
		private readonly WorldEntryService _worldEntry;

		// The name commit + the character seed (CharacterCreationService).
		private readonly CharacterCreationService _creation;

		// The "wash the new character ashore and hand it to the world server" op (change-map prep + the 0x2002
		// redirect) - TeleportService's two sends, as a single-op seam rather than the whole service.
		private readonly Func<INetworkClient, Task> _sendEnterWorldHandoff;

		private readonly IDatabaseRepository _database;

		private readonly ILogger _logger;

		// The live world simulation (owns the connected players' authoritative entities + zone model). Used by the
		// cutscene actor-binding (LiveNpcHandle) to re-address the destiny cinematic to OUR live Kalanthes.
		private readonly WorldSimulation _simulation;

		public CreateFlowService(ILogger logger, IDatabaseRepository database, WorldSimulation simulation, WorldEntryService worldEntry, CharacterCreationService creation, Func<INetworkClient, Task> sendEnterWorldHandoff)
		{
			_logger = logger;
			_database = database;
			_simulation = simulation;
			_worldEntry = worldEntry;
			_creation = creation;
			_sendEnterWorldHandoff = sendEnterWorldHandoff;
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
					var commit = await _creation.CommitCharacterName(client, valueString);

					if (commit == CharacterCreationService.NameCommitResult.Committed && client.Session.Character.Name == valueString)
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
						await CharacterCreationService.SendNicknameRejected(client, valueString!, commit == CharacterCreationService.NameCommitResult.Taken);
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
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchReportDimesionID,
				(byte[])[0x00, 0x01, 0x31]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchReportServerID,
				(byte[])[0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x03]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchAckAuthenticate,
				(byte[])[0x00, 0x00, 0x00, 0x01]));

			// (b) The live-char world (constructed; same path existing-char logins use). The intro cutscene does NOT
			// follow here - see SendCreateIntroCutscene for why it waits for CharInPlayIIR.
			await _worldEntry.SendBurstlessWorldEntry(client, client.Session.Character.MapId);
		}

		// (c) Start the destiny cutscene (Scry scripts only - the begin marker, the scene-load character_creation step,
		// and the two opening script groups). The per-callback steps follow via SendCreateCutsceneStep.
		//
		// Sent in RESPONSE to the create-world CharInPlayIIR, after the ready burst - the order retail uses (Blablatest
		// create: c2s CharInPlayIIR fr317 -> s2c 0x2024 fr324 -> s2c cutscene fr325). Sending it with the world entry
		// instead put the cutscene FIRST, so the client's CharInPlayIIR - and therefore ReadyForPlayScreen - landed in
		// the middle of the intro. That is the same failure the 0x2020-poll flush caused, just moved later: the intro
		// began correctly and then the world snapped back to the un-ready state.
		internal static async Task SendCreateIntroCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// EnergyIIR (0x40749B8A) seed on the avatar - constructed via EnergyCodec (byte-exact-locked by
			// CreateEditorRecordTests), was a verbatim blob. env=1, value=0x36B0.
			await CreateFlowWire.SendIirRecord(client, Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id),
				EnergyCodec.Build(new EnergyIIR(id, 0x01, 0x36B0)));
			await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, 48126));
			await CreateFlowWire.SendScry(client,
				ScryScriptCodec.Build(id,
					"<remoteformat token=\"playorskip(100)|onclientconnected(1)|character_creation Tortage Beach (server-side)\" category=\"50023\" key=\"*&#36;!0mcl&#36;Z!5Ii0UG!TV?\" knubot=\"0\"  ></remoteformat>",
					6,
					resources:
					"1010003:3147889;1010003:3147890;1010003:3147891;1010003:3147892;1010003:3147893;1010003:3147894;1010003:137283;1010003:202270;1010003:210528;1010003:205370;1010003:3147896;1010003:3147895;1010003:3147897;1010003:3147898;1010003:3147899;1010003:3147900;1010003:208235;1010003:3203880;1010003:3203879;1010003:3203881;1010003:3203876;1010003:3203877;1010003:3203878",
					f6Field1: 4617355, f6Field2: 7004418));
			await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, 385910));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, id),
				GameInterfaceMethods.DispatchPassBlob, (byte[])[0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x00]));
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
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, "ThisIsKalanthes", f6Field1: 50000, f6Field2: LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					// Position Kalanthes next to the washed-ashore player (his cutscene start), so the walk-up reads
					// (his DB spawn is back by the rocks). The walk path itself follows on the Move callback.
					await TortageIntroService.SendKalanthesMoveTask(client, TortageIntroService.BuildKalanthesIntroMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
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
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, groupId));
					// 7088774 is Kalanthes's welcome-dialogue group; retail follows its StartGroup with the
					// WelcomeTutorial step (~2.7s later, no triggering callback) - bundle it here, callback-driven.
					if (groupId == 7088774)
						await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, "WelcomeTutorial"));
					break;
				}

				case "Move":
				{
					// Kalanthes walks up to the player (f890 path), then kneels.
					await TortageIntroService.SendKalanthesMoveTask(client, TortageIntroService.BuildKalanthesWalkMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, "Kneel"));
					break;
				}

				case "IntroDone":
				{
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, "StartFixed3d"));
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, 7727158));
					break;
				}
			}
		}

		// Arms the enter-world handoff (#433). The handoff USED TO be sent in reply to the client's first 0x206E
		// AddShortcut - i.e. it fired only if the character had been granted something the client chose to auto-bar.
		// That is not a signal about entering the world at all, and it stranded creation completely whenever the kit
		// came back empty: an Assassin created 2026-07-29 got 0 abilities and 0 spells (every ClassAbilities row for
		// the class carries the LEVEL OF THE CAPTURED CHARACTER it was harvested from - 23 - so a level-1 character
		// matches none), barred nothing, sent no 0x206E, and sat on a black screen forever while the server waited for
		// a message the client had no reason to send. The ticket predicted this exact failure a month before it hit.
		//
		// Retail's trigger is TIME, measured from CharCreationFinished, and is independent of the bar. Across all 13
		// create captures that reach a redirect, CharCreationFinished -> s2c 0x2002 is 8.90-9.52s (median 9.21; one
		// 10.16s outlier on a slow session), while the LAST c2s before the redirect scatters over 0.31-1.65s and is
		// not even the same message each time - AddShortcut in 11, a StatIIR PassBlob in one, and a bare Ping in
		// another (Fwfnwfjwk_soldier-guardian_create). A trigger cannot be a packet that is sometimes absent.
		//
		// So: arm at creation-finished, fire once, whatever the client does or does not put on its bar.
		private void ScheduleEnterWorldHandoff(INetworkClient client)
		{
			ScheduleEnterWorldHandoff(client, EnterWorldHandoffDelay);
		}

		// The delay is a parameter only so the tests can arm it at zero; production always uses the retail-measured
		// EnterWorldHandoffDelay above.
		internal void ScheduleEnterWorldHandoff(INetworkClient client, TimeSpan delay)
		{
			// ARMING is a claim too (#706). It used to write the latch unconditionally, which meant the atomic claim
			// below only stopped two tasks sharing ONE arm: arm -> claim -> send -> arm again -> claim -> send again
			// walked straight past it, and a second 0x2002 re-redirects a client that is already reconnecting.
			//
			// A refusal here means the creation-finished path ran TWICE on one connection - a retried or duplicated
			// message - so it is logged rather than swallowed. Not an error: refusing is the correct outcome, and the
			// client is already on its way to the world.
			if (!client.Session.TryArmEnterWorldHandoff())
			{
				_logger.LogInformation("Create-editor: enter-world handoff already {State} for this connection - not arming a second one.",
					client.Session.AwaitingEnterWorld ? "armed" : "sent");
				return;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(delay);

					// The other half of the same property: several tasks could still be in flight if arming ever
					// becomes legitimate more than once, and "read the flag then clear it" is not single-shot at all -
					// two can both pass the read before either writes. This is the compare-and-swap that settles it,
					// and it moves the connection to the TERMINAL `Sent` state.
					if (!client.Session.TryClaimEnterWorldHandoff())
						return;

					await _sendEnterWorldHandoff(client);
				}
				catch (Exception e)
				{
					// Never silent: this is the step whose failure looks exactly like the bug above - a client waiting
					// forever on a black screen - so the reason has to be in the log (see the no-silent-fallbacks rule).
					_logger.LogError(e, "Create-editor: failed to send the enter-world handoff; the client will hang on the loading screen.");
				}
			});
		}
		// Verbatim replay of the retail create-editor INITIAL download from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (GameServer port 7030),
		// rows 30-65. This stage front-loads MapChange + SpawnObject/Spawn + AccessWindow + GameTime
		// + the first 9x 0xF98E10B3 before the client sends its first 0x205F.

		// #197 deterministic trigger: the client sends Finish + its SetMorphValue batch in one burst; retail sends
		// the post-morph trio once that recv batch is fully received. Called from the listener's batch-drained hook
		// (ReceivedBatchComplete). No-op until Finish/a morph has been seen this session, and sends exactly once.
		internal static Task FlushPostMorphBurstOnBatchDrained(INetworkClient client)
		{
			return TrySendPostMorphBurstOnce(client);
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
		// IirTypes / docs/re/aoc-opcode-ids.md); bodies are kept verbatim - they are opaque
		// editor-state (entity handles 0x0009FFxx, morph mesh names, stat blobs) the client choreography expects.

		// Step 3 of the create->world handshake: the world-prep / scene burst the creator sends after
		// CharCreationFinished, before the client will send 0x206E (enter world). Verbatim from
		// Captures/Dikketest_rogue-barbarian_create_random-actions.tshark.json (creator gs013:7016, 		// idx 364-438, excluding the 0x2082/0x2009 keepalive replies which are answered naturally), with the
		// client instance retargeted from dikketest's 0x01316D2B to the local temp instance 0x021662C8
		// (receiver varint AB DA C5 09 -> C8 C5 D9 10, body instance 01 31 6D 2B -> 02 16 62 C8). Each packet
		// is re-serialized (as an Rpc) so its CRC/length are recomputed for the retargeted bytes.

		// Consumes (removes) a pending create-world-entry marker for `clientInstance`, returning true if one was set.
		// The listener's world Hello calls this to recognise the post-creation world connection (and skip the helper
		// auth), then take the create-world entry path; consuming it means a later normal login doesn't re-trigger the intro.
		internal bool TryConsumePendingCreateWorldEntry(uint clientInstance)
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
		private bool IsCreateWorldConnection(INetworkClient client)
		{
			return client.Session is { EntryMode: WorldEntryMode.CreateWorld, Character.Name: not null };
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

		// Sends the SetMorphValueIIR ack for one SetMorphValue (SetMorphValueCodec owns the layout).
		// The weight is a server-side default that is not present in the client's message: textures
		// (values starting with '*') use 1.0, body morphs use -0.03 (the only two values seen in retail).
		private static Task SendMorphValueAck(INetworkClient client, string morphValue)
		{
			var instance = client.Session.Account.ClientInstance;
			var counter = client.Session.MorphAckCounter == 0
				? client.Session.MorphAckCounter = 2
				: ++client.Session.MorphAckCounter;
			var weight = morphValue.StartsWith("*", StringComparison.Ordinal)
				? 1.0f
				: -0.03f;

			return CreateFlowWire.SendCreateEditor2000Record(client, SetMorphValueCodec.Build(new SetMorphValueIIR(instance, morphValue, weight, 0, counter)));
		}

		// The fixed trio the server sends after the last morph; tails are verbatim from dikketest with the
		// client instance substituted. This is what nudges the client to send the TheNameIs callback.
		private static async Task SendPostMorphBurst(INetworkClient client)
		{
			var instance = client.Session.Account.ClientInstance;
			var character = client.Session.Character;

			await CreateFlowWire.SendCreateEditor2000Record(client, StatCodec.Build(StatIirs.ForCharacter(instance, 0x0578, 0x00)));
			await CreateFlowWire.SendCreateEditor2000Record(client, ScryScriptCodec.Build(instance, 35820)); // StartScriptGroupOnClient (#223)

			// The editor's class-base push: stat 0x0001 / 0x01F9 / 0x01FB for the class the player just chose. Retail
			// sends THIS CHARACTER's base here - the Blablabla guardian create carries 12000/17250/0 at f2613, which is
			// RDB 1000511's Guardian row (12000, 17250) to the unit, and the value is re-derived per class.
			//
			// It used to be a verbatim captured blob: 11000 / 17200 / 0. That is the BARBARIAN row - harvested from a
			// barbarian create dump and then replayed at every class alike, so an Assassin (9000 / 23000) was handed a
			// health base 20 too high and a stamina base 58 too low, and a mage was handed a stamina pool it does not
			// have. Exactly the "wrong-looking constant that is really a captured value for ONE case" this codebase
			// warns about; the class curve is the same source the snapshot's own max slots already use.
			var (health, mana, stamina) = CharacterMaxStats.ClassBaseHundredths(character.Class, character.Level);
			// Stat ids in retail's order (health, stamina, mana - see the captured f2613 body); the record itself is
			// StatUpdateCodec's, so this path no longer hand-writes the `[u32 0][u8 count][id][value]*` tail
			// (#530 wave 7). Sent whole, because the record's `[subop][class][handle]` head IS what the tail form's
			// helper was re-writing by hand.
			var classBase = new CharacterStatBlock()
				.SetRaw(CharacterStatId.HealthMax, health)
				.SetRaw(CharacterStatId.StaminaMax, stamina)
				.SetRaw(CharacterStatId.ManaMax, mana);

			await CreateFlowWire.SendCreateEditor2000Record(client, StatUpdateCodec.Build(new StatUpdateIIR(instance, classBase)));
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


		// Step 2: answer the client's TheNameIs callback so it knows the name was accepted and advances to
		// enter-world. Sequence from dikketest (idx 359-363): 0xADCE0CDA (echoes the name) + an 0xA36D3B74
		// "NicknameOk" callback (echoes the name) + 0xF508F4C1 (fixed) + an 0xA36D3B74 "CharCreationFinished".
		private async Task SendNicknameAccepted(INetworkClient client, string name)
		{
			var instance = client.Session.Account.ClientInstance;

			await CreateFlowWire.SendIirRecord(client, CreateFlowWire.MorphAckSender, CreateFlowWire.Receiver(client), SetNameCodec.Build(SetNames.Accepted(instance, name)));

			await CreateFlowWire.SendA36DCallback(client, "NicknameOk", name);
			// The fixed post-name GameActionList for dynel 0xCFEF; the header is GameActionListCodec's and only the
			// (still-opaque, #190) action tail rides verbatim.
			await CreateFlowWire.SendCreateEditor2000Record(client, GameActionListCodec.Build(new GameActionListIIR(instance, 0x0000CFEF, 0,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
			])));
			await CreateFlowWire.SendA36DCallback(client, "CharCreationFinished", "");

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
			// Hand the client the kit its bar is built from. It answers each grant with an AddShortcut (0x206E), which
			// the shortcut handler persists. This is the character's starting bar - it is NO LONGER what moves the flow
			// forward (#433); see ScheduleEnterWorldHandoff.
			await CharacterLoadout.GrantToClient(client, _database, _logger, CreateFlowWire.CreateGrantSender);

			// Creation is finished; the world handoff is now on a timer, not on the client barring something (#433).
			ScheduleEnterWorldHandoff(client);
		}
	}
}
