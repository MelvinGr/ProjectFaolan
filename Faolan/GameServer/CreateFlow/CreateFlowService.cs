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
using Faolan.Core.Protobuf;
using Faolan.Core.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Faolan.GameServer.Scripting;
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

		internal static readonly ProtoEndpoint MorphAckSender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x0C);

		// The create-editor's own grant conversation (GameCharAgent 0x0C); the login/world grants ride a different one.
		private static readonly ProtoEndpoint CreateGrantSender = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x0C);

		// Character ids that have just been named in the create-editor and are about to open their FIRST world
		// connection (the second GameServer connection, after the 0x2002 enter-world handoff). Added when the name is
		// committed (HandleCommandPacket TheNameIs) and consumed by the listener's world Hello via
		// TryConsumePendingCreateWorldEntry, which is how that connection is recognised as the create-world entry (and
		// given the intro/cutscene) rather than a normal login. Static because the editor and world connections are
		// different sessions but hit the same listener/service instance; ConcurrentDictionary-as-set because it is
		// mutated from different connection threads (add on one connection, consume on the reconnect). Value unused.
		private static readonly ConcurrentDictionary<uint, byte> PendingCreateWorldEntry = new ConcurrentDictionary<uint, byte>();

		// The overhead name-COLOUR category retail answers RequestAggroStatus with for each of the five editor scene
		// characters (0 friendly / 2 neutral - see UpdateAggroStatusCodec). The client polls all five as soon as the
		// editor scene is streamed, before the character is named, and retail answers every poll: 55/55 in
		// account-stricken998-character-create-no-worldenter, 95/95 in Barbarian-create.
		//
		// Keyed by the burst's own handles (the captured 0x0009FF92-96 the editor scene is emitted with, above). The
		// categories are per ROLE, not per session: the same five characters carry the same categories under three
		// different handle blocks - Female Template friendly, the other four neutral (stricken998 0x9FF92-96,
		// Barbarian-create 0x4302E-32, Strie_rogue-assassin_create 0x818A2-A6).
		internal static readonly FrozenDictionary<uint, uint> EditorSceneDispositions = new Dictionary<uint, uint>
		{
			[0x0009FF92] = WorldEntity.DispositionNeutral, // Stygian Female
			[0x0009FF93] = WorldEntity.DispositionNeutral, // Saddur
			[0x0009FF94] = WorldEntity.DispositionNeutral, // Male Template
			[0x0009FF95] = WorldEntity.DispositionFriendly, // Female Template
			[0x0009FF96] = WorldEntity.DispositionNeutral // Stygian Slaver
		}.ToFrozenDictionary();

		// The 18 galley scene-prop dynels the create-editor initial burst streams, in retail send order. Each is a
		// WorldObjects row on the editor map (4000), built byte-exact via WorldObjectCodec (see
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

		// The listener-owned "wash the new character ashore and hand it to the world server" op (change-map prep +
		// the 0x2002 redirect). A narrow Func seam like _sendBurstlessWorldEntry rather than a back-reference to the
		// listener - see the composed-collaborator note above.
		private readonly Func<INetworkClient, Task> _sendEnterWorldHandoff;

		private readonly IDatabaseRepository _database;

		private readonly ILogger _logger;

		// Set on first use by LoadNameFilter; see there.
		private (IReadOnlySet<string> Hashes, IReadOnlySet<int> Lengths)? _nameFilter;

		// The live world simulation (owns the connected players' authoritative entities + zone model). Used by the
		// cutscene actor-binding (LiveNpcHandle) to re-address the destiny cinematic to OUR live Kalanthes.
		private readonly WorldSimulation _simulation;

		public CreateFlowService(ILogger logger, IDatabaseRepository database, WorldSimulation simulation, Func<INetworkClient, uint, Task> sendBurstlessWorldEntry,
			Func<INetworkClient, Task> sendEnterWorldHandoff)
		{
			_logger = logger;
			_database = database;
			_simulation = simulation;
			_sendBurstlessWorldEntry = sendBurstlessWorldEntry;
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
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchReportDimesionID,
				(byte[])[0x00, 0x01, 0x31]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchReportServerID,
				(byte[])[0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x03]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchAckAuthenticate,
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
		internal static async Task SendCreateIntroCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// EnergyIIR (0x40749B8A) seed on the avatar - constructed via EnergyCodec (byte-exact-locked by
			// CreateEditorRecordTests), was a verbatim blob. env=1, value=0x36B0.
			await SendIirRecord(client, Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id),
				EnergyCodec.Build(new EnergyIIR(id, 0x01, 0x36B0)));
			await SendScry(client, ScryScriptCodec.Build(id, 48126));
			await SendScry(client,
				ScryScriptCodec.Build(id,
					"<remoteformat token=\"playorskip(100)|onclientconnected(1)|character_creation Tortage Beach (server-side)\" category=\"50023\" key=\"*&#36;!0mcl&#36;Z!5Ii0UG!TV?\" knubot=\"0\"  ></remoteformat>",
					6,
					resources:
					"1010003:3147889;1010003:3147890;1010003:3147891;1010003:3147892;1010003:3147893;1010003:3147894;1010003:137283;1010003:202270;1010003:210528;1010003:205370;1010003:3147896;1010003:3147895;1010003:3147897;1010003:3147898;1010003:3147899;1010003:3147900;1010003:208235;1010003:3203880;1010003:3203879;1010003:3203881;1010003:3203876;1010003:3203877;1010003:3203878",
					f6Field1: 4617355, f6Field2: 7004418));
			await SendScry(client, ScryScriptCodec.Build(id, 385910));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, id),
				GameInterfaceMethods.DispatchPassBlob, (byte[])[0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x00]));
		}

		private async Task<NameCommitResult> CommitCharacterName(INetworkClient client, string? name)
		{
			var character = client.Session.Character;
			if (character == null || string.IsNullOrWhiteSpace(name))
				return NameCommitResult.Invalid;

			if (string.Equals(character.Name, name, StringComparison.Ordinal))
				return NameCommitResult.Committed;

			// Retail's own rules, in the order the client applies them: 4..16 characters (the client states that
			// number itself and enforces it locally, so this only ever fires for a modified client), then the
			// client's shipped forbidden-word list (#884). A server whose database has no imported list runs the
			// length rule alone - the absence is LOGGED once by LoadNameFilter rather than passing silently.
			var (forbidden, lengths) = await LoadNameFilter();
			var verdict = CharacterNameRules.Check(name, forbidden, lengths);
			if (verdict != CharacterNameRules.Verdict.Ok)
			{
				_logger.LogInformation("Character-editor: name rejected as {Verdict}.", verdict);
				return NameCommitResult.Invalid;
			}

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
			// Nothing to regenerate: vitals are DERIVED from (Class, Level) at read time, so the class chosen in the
			// final editor step is reflected the moment it is stored - there is no level-1 rogue baseline left to
			// replace (#578).
			// Persist the whole accumulated editor graph (name + sex/race/morphs Body) as one unit of work.
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
			await _database.SetCharacterQuest(character.Id, QuestContent.BeachStarterQuestId, QuestState.Active);
			_logger.LogInformation("Character-editor: committed character name '{Name}' for character {CharacterId:X8}; granted starter quest {QuestId}.", name, character.Id,
				QuestContent.BeachStarterQuestId);
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
		// Keyed on the character being UNDRESSED - having no worn items at all. This used to run on first world entry
		// keyed on the CharacterEquipment ROW being absent, and silently stopped working the moment that row started
		// being created with the character (ce580410): the row always existed, so the seeding branch became unreachable
		// and new characters entered the world naked. It then keyed on the SLOTS being empty; since #750 a naked
		// character simply has no Equipped items, so there is only one thing left to check.
		private async Task SeedStarterEquipment(Character character)
		{
			// Only ever dress an UNDRESSED character. Re-filling emptied slots later would undo the player's own
			// unequips, which is why this cannot simply run on every login.
			if ((await _database.GetEquippedItems(character.Id)).Count > 0)
				return;

			var female = character.Sex == Sex.Female;
			var femaleOutfit = StartingData.StartingFemaleOutfits[Random.Shared.Next(StartingData.StartingFemaleOutfits.Length)];

			// A null template is a slot this sex/class does not get (a male gets no gown, a class the create dumps do
			// not cover gets no weapon), so the slot is simply left unworn.
			var gear = new (CharacterEquipmentSlot Slot, uint? Template)[]
			{
				(CharacterEquipmentSlot.Wrist, StartingData.StartingWristItem),
				(CharacterEquipmentSlot.Chest, female
					? femaleOutfit.Gown
					: null),
				(CharacterEquipmentSlot.Legs, female
					? femaleOutfit.Bottom
					: StartingData.StartingMaleLegsItem),
				// Class-specific improvised weapon (main-hand-left); only the classes the create dumps cover.
				(CharacterEquipmentSlot.MainWeapon, StartingData.StartingWeapon.TryGetValue(character.Class, out var weaponId)
					? weaponId
					: null)
			};

			foreach (var (slot, template) in gear)
			{
				if (template is { } id && await _database.GetItem(id) != null)
					await _database.SetCharacterItemSlot(character.Id, ContainerKind.Equipped, (int)slot, id);
			}

			_logger.LogInformation("Dressed new character {CharacterId:X8} ({Class}, {Sex}) in its starting gear.", character.Id, character.Class, character.Sex);
		}

		// Binds the new character to its starting map's default respawn point, so bind recalls (the Path of Asura spell,
		// .recall) and death-resurrect have somewhere to send it from the very first login. The bind moves after this
		// only when the player uses a bindstone/Wayfarer. The Totem of Origins does NOT read the bind - it goes to the
		// racial home city (StartingData.RacialHomeMap).
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

			// A ref the registry does not know is a GAP: MorphTargets ships in the seed (1072 rows), so an unknown ref
			// means the editor sent something we never imported. It is checked HERE, directly, rather than by asking
			// BuildWeightedEntries - which no longer answers "could not build" with a default face but by throwing, and
			// throwing at creation would fail the create over a cosmetic gap. Saying it once, here, is what makes the
			// gap actionable before the character's every future spawn hits it.
			var unresolved = character.BodyResources.Select(r => r.Ref).Where(r => !registry.ContainsKey(r)).ToArray();
			if (unresolved.Length > 0)
			{
				_logger.LogError("Character {CharacterId:X8}: appearance resource(s) {Refs} are not in MorphTargets, so its spawn cannot be built at all. Import them.",
					character.Id, string.Join(", ", unresolved));
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
					await SendScry(client, ScryScriptCodec.Build(id, "ThisIsKalanthes", f6Field1: 50000, f6Field2: LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
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
					await SendScry(client, ScryScriptCodec.Build(id, groupId));
					// 7088774 is Kalanthes's welcome-dialogue group; retail follows its StartGroup with the
					// WelcomeTutorial step (~2.7s later, no triggering callback) - bundle it here, callback-driven.
					if (groupId == 7088774)
						await SendScry(client, ScryScriptCodec.Build(id, "WelcomeTutorial"));
					break;
				}

				case "Move":
				{
					// Kalanthes walks up to the player (f890 path), then kneels.
					await TortageIntroService.SendKalanthesMoveTask(client, TortageIntroService.BuildKalanthesWalkMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					await SendScry(client, ScryScriptCodec.Build(id, "Kneel"));
					break;
				}

				case "IntroDone":
				{
					await SendScry(client, ScryScriptCodec.Build(id, "StartFixed3d"));
					await SendScry(client, ScryScriptCodec.Build(id, 7727158));
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

			await SendIirRecord(client, MorphAckSender, Receiver(client), SetNameCodec.Build(SetNames.Accepted(instance, name)));

			await SendA36DCallback(client, "NicknameOk", name);
			// The fixed post-name GameActionList for dynel 0xCFEF; the header is GameActionListCodec's and only the
			// (still-opaque, #190) action tail rides verbatim.
			await SendCreateEditor2000Record(client, GameActionListCodec.Build(new GameActionListIIR(instance, 0x0000CFEF, 0,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
			])));
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
			// Hand the client the kit its bar is built from. It answers each grant with an AddShortcut (0x206E), which
			// the shortcut handler persists. This is the character's starting bar - it is NO LONGER what moves the flow
			// forward (#433); see ScheduleEnterWorldHandoff.
			await CharacterLoadout.GrantToClient(client, _database, _logger, CreateGrantSender);

			// Creation is finished; the world handoff is now on a timer, not on the client barring something (#433).
			ScheduleEnterWorldHandoff(client);
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

		// `now` feeds the generated GameTime resync anchor (see the GameTime step below); it is a parameter so the
		// byte-exact burst golden can pass the capturing instant and still match the capture.
		public static async Task SendRetailCreateEditorInitialBurst(INetworkClient client, IDatabaseRepository? database = null, DateTimeOffset? now = null, ILogger? logger = null)
		{
			var id = client.Session.Account.ClientInstance;
			var sender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59);
			var receiver = Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id);

			// The galley scene props are WorldObjects rows on the editor map (4000), loaded here keyed by captured
			// handle and streamed below (after the MapChange) built byte-exact from their columns via WorldObjectCodec
			// - no verbatim bytes. Requires a DB in production; a bare no-DB golden run simply omits them.
			var dynelRows = await LoadEditorSceneDynels(database);

			// MapChange: CONSTRUCTED from the editor map's reference point + the session playfield token (#189
			// item 3). mapId + playfieldToken are the meaningful values; the reference position, clip planes and
			// trailing flags are this map's constants (not yet semantically pinned - carried as builder args).
			await Record(PlayfieldFCodec.Build(StartingData.EditorSpawnPosition, StartingData.EditorMapId, 0, 1, CreateEditorCharBuilder.EditorPlayfieldInstance, 15.0f,
				BitConverter.UInt32BitsToSingle(0x415DA9E7u), 0x00000080, 1u));

			// The 18 galley scene props: SPAWNED FROM their map-4000 WorldObjects rows (built byte-exact by
			// WorldObjectCodec, re-addressed to the captured handle + editor playfield), in retail send order.
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

				await Record(WorldObjectCodec.Build(dynelRow, handle, CreateEditorCharBuilder.EditorPlayfieldInstance));
			}

			// The editor scene pedestals - the avatar (Stygian Female, the chosen looks) and the four fixed example
			// models. TYPED DATA now, not inline bytes: `CreateEditorScene` is generated from the captured burst and
			// gated byte-exact against it (#364), which retired both the hex blobs that used to sit here and the
			// reconstruct-from-your-own-bytes path they fed.
			foreach (var pedestal in CreateEditorScene.Pedestals)
			{
				var scene = pedestal();
				scene.Playfield = CreateEditorCharBuilder.EditorPlayfieldInstance;
				await Record(SimpleCharFullUpdateCodec.Build(scene));
			}

			// The live avatar preview on its pedestal: the same typed record, with the session's client instance as the
			// entity HANDLE - the one thing no capture can supply.
			var preview = CreateEditorScene.LiveAvatarPreview();
			preview.Handle = id;
			await Record(SimpleCharFullUpdateCodec.Build(preview));

			// The avatar's FullChar snapshot, built WHOLE from typed data (#651) - see CreateEditorAvatarSnapshot.
			// The carrier's u32 length belongs to the SENDER (#530 rule 2), so it is written here and the codec
			// emits the record alone.
			var avatarSnapshot = FullPlayerCharacterCodec.Build(CreateEditorAvatar.Snapshot(id));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteUInt32((uint)avatarSnapshot.Length).WriteArray(avatarSnapshot)));

			// GameTime: CONSTRUCTED from the captured day-time + rate (#189 item 3). The resync timestamp is generated
			// from the wall clock the same way the world-entry clock is (GameClock.ResyncTimestamp) rather than
			// replaying the capture's; dayTime stays captured until its unit is pinned (#351).
			// The editor's captured time of day (0x4743EB00 = 50155s = 13:55) with a generated resync anchor. The backdrop
			// has no day/night to track, so this stays the captured value rather than the live clock.
			var (editorGameDay, editorUnixSeconds) = GameClock.ResyncAnchor(now ?? DateTimeOffset.UtcNow);
			await Record(GameTimeCodec.Build(new GameTimeIIR(BitConverter.UInt32BitsToSingle(0x4743EB00), 1u, editorGameDay, editorUnixSeconds)));

			// #189: constructed via StatCodec (was verbatim). env=0 for 0x0F4266, then an env=1 / value=0 run 0x0F4285..0x0F428C.
			await Record(StatCodec.Build(StatIirs.ForCharacter(id, 0x0F4266, 0x00, env: 0x00)));

			for (uint statId = 0x0F4285; statId <= 0x0F428C; statId++)
				await Record(StatCodec.Build(StatIirs.ForCharacter(id, statId, 0x00)));

			return;

			Task Record(byte[] record)
			{
				return SendIirRecord(client, sender, receiver, record);
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
			return client.Endpoint(RpcInterfaces.GameInterfaceType, client.Session.Account.ClientInstance);
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

		internal static async Task SendRetailCreateEditorCreationScriptBurst(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			var sender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59);
			var receiver = Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id);

			// Initial vitals + behavior/action setup for the editor entities (handles 0x0009FF92..96).
			await Record(StatUpdateCodec.Build(0x0009FF92u, EditorVitals()));
			await Record(StatUpdateCodec.Build(0x0009FF93u, EditorVitals()));
			await Record(BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(0x0009FF94u, 5, 85843, 1, 24, 0)));
			await Record(StatUpdateCodec.Build(0x0009FF94u, EditorVitals()));
			await Record(BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(0x0009FF95u, 7, 41413, 1, 24, 0)));
			await Record(StatUpdateCodec.Build(0x0009FF95u, EditorVitals()));
			await Record(BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(0x0009FF96u, 7, 41413, 1, 24, 0)));
			await Record(StatUpdateCodec.Build(0x0009FF96u, EditorVitals()));

			await Record(StatCodec.Build(StatIirs.ForCharacter(id, 0x0360, 0x02, env: 0x00)));

			// MagicBuff carries the instance twice (applyTo + applyFrom); the 54-byte APPLY payload rides verbatim.
			await Record(MagicBuffCodec.Build(new MagicBuffIIR(id, 0x00, 0x00000B90, 0x00, id,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x90, 0x00, 0x00,
				0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3D, 0x5F, 0xC9, 0x66
			])));

			await Record(EnergyCodec.Build(new EnergyIIR(id, 0x01, 0x00)));

			// Stat run (env=1, value=0): entity stat ids 0x0566..0x0573, then 0x0575 and 0x0580.
			foreach (var statId in (uint[])[0x0566, 0x0567, 0x0568, 0x0569, 0x056A, 0x056B, 0x056C, 0x056D, 0x056E, 0x056F, 0x0570, 0x0571, 0x0572, 0x0573, 0x0575, 0x0580])
				await Record(StatCodec.Build(StatIirs.ForCharacter(id, statId, 0x00)));

			await Record(StatCodec.Build(StatIirs.ForCharacter(id, 0x0F428F, 0x00, env: 0x00)));

			await Record(StatUpdateCodec.Build(id, EditorAvatarStats()));

			// ScryScriptIIR: the create-world "PlayOrSkip(72) Slave Galley" remoteformat + its RDB resource manifest, 			// then the "PlayOrSkip" ScriptMessage (decoded by tools/capture/decode_scryscript.py).
			await Record(ScryScriptCodec.Build(id, CreateWorldScriptToken, 7, resources: CreateWorldResourceManifest, f6Field1: 6177372, f6Field2: 9422303));
			await Record(ScryScriptCodec.Build(id, "PlayOrSkip"));

			// SetMorphValue setup (hair meshes, then a texscript) on the editor entities.
			await Record(SetMorphValueCodec.Build(new SetMorphValueIIR(0x0009FF94u, "*hairmesh_207920", 1.0f, 0x00, 0x00000001u)));
			await Record(SetMorphValueCodec.Build(new SetMorphValueIIR(0x0009FF95u, "*hairmesh_203666", 1.0f, 0x00, 0x00000001u)));
			await Record(BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(0x0009FF94u, 5, 85843, 2, 24, 0)));
			await Record(SetMorphValueCodec.Build(new SetMorphValueIIR(id, "*texscript_183875", 1.0f, 0x01, 0x00000001u)));
			await Record(SetMorphValueCodec.Build(new SetMorphValueIIR(0x0009FF95u, "*texscript_183875", 1.0f, 0x01, 0x00000002u)));
			await Record(SetMorphValueCodec.Build(new SetMorphValueIIR(0x0009FF94u, "*texscript_183875", 1.0f, 0x01, 0x00000002u)));

			return;

			// One IIR packet: 0x2000 envelope -> [u32 length][record]. The length is recomputed, so editing a record
			// never desyncs it.
			Task Record(byte[] record)
			{
				return SendIirRecord(client, sender, receiver, record);
			}
		}

		// The three vitals every create-editor entity is seeded with, and the avatar's own six-stat push. Raw WIRE
		// values straight from the capture (hence SetRaw, which applies no display scale); the ids past PlayerId are
		// unnamed in CharacterStatId. Fresh blocks per call - CharacterStatBlock is mutable.
		private static CharacterStatBlock EditorVitals()
		{
			return new CharacterStatBlock()
				.SetRaw(CharacterStatId.HealthCurrent, 0x189C)
				.SetRaw(CharacterStatId.StaminaCurrent, 0x189C)
				.SetRaw(CharacterStatId.ManaCurrent, 0x189C);
		}

		private static CharacterStatBlock EditorAvatarStats()
		{
			return new CharacterStatBlock()
				.SetRaw(CharacterStatId.HealthCurrent, 0x1F40)
				.SetRaw(CharacterStatId.Mount, 0x00)
				.SetRaw((CharacterStatId)0x1FF, 0x00)
				.SetRaw((CharacterStatId)0x200, 0x0301)
				.SetRaw((CharacterStatId)0x201, 0x00)
				.SetRaw(CharacterStatId.PlayerId, 0x59EA5C54);
		}

		// Verbatim replay of the fixed tail of the retail create-editor post-ready follow-up from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (rows 198-199, 203-204).
		//
		// It used to be preceded by a hand-rolled ack per observed "instance echo" request id. Those acks were
		// UpdateAggroStatus replies under another name (#564) and the ids were the five editor scene entities the
		// client polls; they are answered where every other poll is, in HandleRequestAggroStatus, off the client's
		// own RequestAggroStatus rather than replayed here.
		internal static async Task SendRetailCreateEditorPostReadyBurst(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceType, id),
				GameCharInterfaceMethods.DispatchValidResurrectionSpots,
				(byte[])
				[
					0x43, 0xA7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4C, 0xCD, 0x00, 0x00, 0x07, 0xE2, 0x43, 0xA7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4C, 0xCD, 0x00, 0x00,
					0xC7, 0x9E, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x00
				]));
			await SendIirRecord(client, Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), CharInPlayCodec.Build(new CharInPlayIIR(id)));

			// The racial CHARACTER-TEMPLATE choice list the creation editor reads (Male/Female/StygianSlaver ->
			// template ids 50000:65525x), delivered on two GameActionListIIR (0xF508F4C1) records for dynel 0xD00C.
			// The two captured variants differ ONLY in the leading action-id word; the property-map action-tail
			// grammar is still opaque (#190) so it rides verbatim, while the record HEADER is constructed via
			// GameActionListCodec.Build (construct-don't-replay - see SendGameActionListRecord).
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
			return SendCreateEditor2000Record(client,
				ScryScriptCodec.Build(client.Session.Account.ClientInstance, token, (uint)ProtoScryScriptIIR.Type.ScriptMessage, resources: value));
		}

		// Sends a fully-built create-editor IIR RECORD - one that already begins with its sub-opcode, i.e. whatever
		// `XCodec.Build(...)` returned - on the creator connection.
		//
		// ⚠️ There is deliberately no `(subOpcode, tail)` twin any more (#542). That shape made every burst step name
		// an IIR in the SERVICE, which is #530 rule 7's definition of a file that encodes, and it forced a helper
		// (`CreateEditorRecordBuilder`) whose whole job was slicing the sub-opcode back off a record so this could
		// re-prepend it.
		private static Task SendCreateEditor2000Record(INetworkClient client, byte[] record)
		{
			return client.SendAsync(new Rpc(MorphAckSender, Receiver(client), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
		}

		// Emits a GameActionListIIR (0xF508F4C1) carrier for `dynel`, delivered to frame `id`. The fixed record
		// header (subop/type/Head/four/one, decoded 570/570) is CONSTRUCTED via GameActionListCodec.Build and
		// the still-opaque property-map action tail rides verbatim; the carrier length is computed from the body.
		private static Task SendGameActionListRecord(INetworkClient client, byte agentSub, uint id, uint dynel, byte[] actionTail)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, agentSub), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(GameActionListCodec.Build(new GameActionListIIR(id, dynel, 0u, actionTail)))));
		}

		// The create-flow send idiom: one 0x2000 record ([u32 len][record]) delivered via DispatchPassBlob, where the
		// record is a codec's `Build` output and therefore already carries its own sub-opcode. Every editor/world-prep
		// burst funnels its records through here instead of re-spelling the PacketStream chain.
		private static Task SendIirRecord(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, byte[] record)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
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

			return SendCreateEditor2000Record(client, SetMorphValueCodec.Build(new SetMorphValueIIR(instance, morphValue, weight, 0, counter)));
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
			var character = client.Session.Character;

			await SendCreateEditor2000Record(client, StatCodec.Build(StatIirs.ForCharacter(instance, 0x0578, 0x00)));
			await SendCreateEditor2000Record(client, ScryScriptCodec.Build(instance, 35820)); // StartScriptGroupOnClient (#223)

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

			await SendCreateEditor2000Record(client, StatUpdateCodec.Build(new StatUpdateIIR(instance, classBase)));
		}

		// A ScryScriptIIR step on the GameAgent->GameInterface conversation (the framing the verbatim burst uses).
		private static Task SendScry(INetworkClient client, byte[] scry)
		{
			var id = client.Session.Account.ClientInstance;
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchPassBlob,
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

		// The client's forbidden-word list, loaded ONCE per process. It is ~200 short hashes and it changes only when
		// the importer re-reads the client, so re-querying it per name typed in the editor would be pure noise.
		private async Task<(IReadOnlySet<string> Hashes, IReadOnlySet<int> Lengths)> LoadNameFilter()
		{
			if (_nameFilter != null)
				return _nameFilter.Value;

			var terms = await _database.GetNameFilterTerms();
			_nameFilter = (terms.Select(t => t.TermHash).ToHashSet(StringComparer.Ordinal), terms.Select(t => t.Length).ToHashSet());

			// An empty table is a real state (the RDB module is opt-in and needs the client install), and it changes
			// what this server enforces - so say so rather than letting a name filter that filters nothing look
			// like one that found nothing to filter.
			if (terms.Length == 0)
				_logger.LogInformation("Character-editor: no name-filter terms imported (`--only rdb-name-filter`); only the 4..16 length rule applies.");
			else
				_logger.LogInformation("Character-editor: {Count} name-filter terms loaded.", terms.Length);

			return _nameFilter.Value;
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
