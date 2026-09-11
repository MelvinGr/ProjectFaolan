using System;
using System.Collections.Concurrent;
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
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.CreateFlow
{
	/// <summary>
	///     The character-creation flow: editor, destiny cutscene, and enter-world. The listener forwards the inbound
	///     create-flow callbacks here; the flow's outbound packet builders - the create-world handshake, the
	///     destiny-cutscene Scry scripts and the editor bursts - live in this file.
	/// </summary>
	/// <remarks>
	///     The byte-exact burst builders are <c>static</c> so the golden tests reach them unchanged. The Kalanthes
	///     cutscene MoveTask builders live on TortageIntroService and are called statically.
	/// </remarks>

	public sealed class CreateFlowService
	{
		internal const string CreateWorldResourceManifest =
			"1010004:793713;1010004:793714;1070003:4000;1000097:4000;1010030:156211;1010030:239448;1010030:565913;1010004:3728533;1060668:4000;1000059:4000;1060667:167368;1010004:3075445;1010004:3180025;1010004:221285;1010004:3663190;1010004:208923;1010004:208266;1010004:226891;1010004:3663191;1010004:3656109;1010030:210069;1010001:210070;1010004:3075452;1010004:3075450;1010004:3075451;1010300:4000;1000049:4000;1066611:4000;1000013:40000000;1000015:40000000;1010506:4000;1000058:4000;1010207:110006;1010207:262241;1010207:568806;1010207:3420331;1010207:167183";

		// The create-world ScryScript step content, decoded by tools/capture/decode_scryscript.py: the PlayOrSkip(72)
		// remoteformat token for the "Slave Galley" creation step (f4) and the RDB resource manifest the client preloads
		// (f5, "rdbId:value;..."). ScryScript.BuildStep frames them.
		internal const string CreateWorldScriptToken =
			"<remoteformat token=\"playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)\" category=\"50023\" key=\"&quot;B]SZ.AGRGrAyOGe*BG-\" knubot=\"0\"  ></remoteformat>";

		// How long after CharCreationFinished the enter-world handoff goes out. Retail's own gap across the 13 create
		// captures that reach a redirect is 8.90-9.52 s; 9.0 sits inside that band, near its floor, so the client gets
		// its creation fade at the retail length without an added wait.
		private static readonly TimeSpan EnterWorldHandoffDelay = TimeSpan.FromSeconds(9.0);

		// Character ids that have just been named in the create-editor and are about to open their first world
		// connection - the second GameServer connection, after the 0x2002 enter-world handoff. Added when the name is
		// committed (HandleCommandPacket TheNameIs) and consumed by the listener's world Hello through
		// TryConsumePendingCreateWorldEntry, which is how that connection is recognised as the create-world entry and
		// given the intro cutscene rather than treated as a normal login. Static because the editor and world
		// connections are different sessions hitting the same service instance, and a ConcurrentDictionary used as a
		// set because it is mutated from different connection threads: added on one connection, consumed on the
		// reconnect. The value is unused.
		private readonly ConcurrentDictionary<uint, byte> PendingCreateWorldEntry = new ConcurrentDictionary<uint, byte>();

		// The burst-less world entry (WorldEntryService): the constructed live-character world the create flow enters
		// before the destiny cutscene overlay.
		private readonly WorldEntryService _worldEntry;

		// The name commit and the character seed (CharacterCreationService).
		private readonly CharacterCreationService _creation;

		// The "wash the new character ashore and hand it to the world server" operation - change-map prep plus the
		// 0x2002 redirect. TeleportService's two sends, taken as a single-op seam rather than the whole service.
		private readonly Func<INetworkClient, Task> _sendEnterWorldHandoff;

		private readonly IDatabaseRepository _database;

		private readonly ILogger _logger;

		// The live world simulation, which owns the connected players' authoritative entities and the zone model. Used
		// by the cutscene actor binding (LiveNpcHandle) to re-address the destiny cinematic to the live Kalanthes.
		private readonly WorldSimulation _simulation;

		// The token wallet's one appearance in the create flow. The service owns every DispatchUpdateTokenAmount send,
		// so the create burst asks it rather than framing the message itself.
		private readonly AlternateAdvancementService _alternateAdvancement;

		public CreateFlowService(ILogger logger, IDatabaseRepository database, WorldSimulation simulation, WorldEntryService worldEntry, CharacterCreationService creation, Func<INetworkClient, Task> sendEnterWorldHandoff,
			AlternateAdvancementService alternateAdvancement)
		{
			_alternateAdvancement = alternateAdvancement;
			_logger = logger;
			_database = database;
			_simulation = simulation;
			_worldEntry = worldEntry;
			_creation = creation;
			_sendEnterWorldHandoff = sendEnterWorldHandoff;
		}

		/// <summary>
		///     Handles the client-to-server 0xA36D3B74 character-creation script callbacks.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Wire format, after the 0x2000 wrapper and the 0xA36D3B74 subopcode have been consumed:
		///     </para>
		///     <code>
		///     uint32 ack            (0x0000C350)
		///     uint32 clientInstance (the create-editor temp instance, e.g. 0x021662C8)
		///     byte   reserved       (always 0x00)
		///     uint32 pbLength       (length of the protobuf message that follows)
		///     protobuf message:
		///       field 1 (varint) = 5      (constant)
		///       field 2 (varint) = 2      (constant)
		///       field 3 (varint) = numeric value  (e.g. ChangeSex sex id, ChangeHeadMesh mesh id)
		///       field 4 (string) = token           (the callback name, e.g. "TheNameIs")
		///       field 5 (string) = value           (optional string, e.g. the chosen character name)
		///       field 6 (bytes)  = trailing nested block (ignored)
		///     </code>
		///     <para>
		///         These callbacks are how the editor reports the player's choices - sex, race, head, size, class, voice,
		///         name - and its lifecycle events (IntroDone, ClientLoadingDone, Move). Most Change* callbacks are
		///         parse-only, because the client self-advances, but three get a server reply, proven from the retail
		///         create captures:
		///     </para>
		///     <list type="bullet">
		///         <item>
		///             SetMorphValue: a 0xBADF5A4B MorphAck echoing each morph value
		///             (Hoxxling_..._modified_all_sliders, 36 client morphs at f553 and 34 acks at f562; distinct from the
		///             roughly five hairmesh and texscript setup 0xBADF5A4B records the server sends at editor init).
		///         </item>
		///         <item>
		///             Finish: the post-morph trio (StatIIR, StartScriptGroup, StatUpdate) that prompts TheNameIs. Finish
		///             arrives in the same client batch as the morphs but before them, and retail sends the trio after
		///             acking the batch's morphs, which is why SchedulePostMorphBurst debounces. The deterministic trigger
		///             is Finish plus the morph batch draining; the ~350 ms timer stands in for that.
		///         </item>
		///         <item>TheNameIs: NicknameOk, NicknameTaken or NicknameInvalid.</item>
		///     </list>
		///     <para>
		///         TheNameIs commits the chosen name straight to the character row (CommitCharacterName); the
		///         create-editor gating keys on Character.Name, so the commit is what promotes the character out of the
		///         editor.
		///     </para>
		/// </remarks>
		internal async Task HandleCommandPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ack
			var clientInstance = body.ReadUInt32();
			body.ReadByte(); // reserved
			var msg = body.ReadProtobufUInt32Length<ProtoScryScriptIIR>();

			var value = msg.IntParameter; // decoded signed value (identity ids, morph slider, ...)
			var rawValue = (uint)((value << 1) ^ (value >> 31)); // the raw wire varint (zigzag re-encode) for script echoes

			var scryType = msg.type;
			var token = msg.StringParameterOne;
			var valueString = msg.StringParameterTwo;

			var character = client.Session.Character;
			_logger.LogInformation("Character-editor script callback: token={Token}, scryType={ScryType}, value={Value} (0x{ValueHex:X}), string={String}, clientInstance={ClientInstance:X8}.", token,
				scryType, value, value, valueString ?? "", clientInstance);

			// The beach cutscene's PlayOrSkipResultPlay (scryType 8) carries no name token. On the create-world connection
			// it is the cue to bind the cinematic actor: retail sends ThisIsKalanthes in response to PlayResult, after the
			// player commits to playing the cutscene, not before.
			//
			// The ProtoScryScriptIIR getter returns "" rather than null for an absent token, so the test must be for empty,
			// not null: a null test silently drops PlayResult, and with it the intro and the actor bind.
			if (string.IsNullOrEmpty(token) && scryType == ProtoScryScriptIIR.Type.PlayOrSkipResultPlay && IsCreateWorldConnection(client))
			{
				await SendCreateCutsceneStep(client, "PlayResult", 0);
				return;
			}

			switch (token)
			{
				// The editor sends class and race as twice the real id, verified against captured character lists:
				// editor 36/68/86/82 becomes class 0x12/0x22/0x2B/0x29, and editor race 2/6 becomes race 1/3. Store
				// the halved canonical id, so the character-list packet and everything else gets a valid class and
				// race; the raw doubled value makes the client show dashes for both on the select screen.
				case "ChangeSex":
				{
					// The same doubling convention as race and class: the halved, zigzag-decoded value is the wire sex
					// code, 2 for male and 3 for female (anchored on a male Hoxxling capture: ChangeSex 4 becomes 2). The
					// Sex enum is Male = 2 and Female = 3, so the decoded value casts straight to it.
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
					// Commit the chosen name to the character row so the later world-entry Hello - a separate
					// connection and session that re-loads the character from the database - sees a named
					// character and takes the normal world-entry path. The client may try several names in
					// succession; the last one that sticks wins.
					var commit = await _creation.CommitCharacterName(client, valueString);

					if (commit == CharacterCreationService.NameCommitResult.Committed && client.Session.Character.Name == valueString)
					{
						// Confirm the name (NicknameOk + CharCreationFinished) so the client advances to enter-world.
						await SendNicknameAccepted(client, valueString!);

						// The character is now named; its next GameServer connection, opened after the 0x2002
						// enter-world handoff, is the create-world entry and must get the intro cutscene. Mark it
						// pending so the world Hello recognises that connection and not a later normal login.
						PendingCreateWorldEntry.TryAdd(client.Session.Character!.Id, 0);
						_logger.LogInformation("Create-editor: character {CharacterId:X8} named '{Name}'; marked pending create-world entry.", client.Session.Account.ClientInstance, valueString);
					}
					else
					{
						// Reject the name so the client re-prompts instead of stalling with no loading screen.
						// NicknameTaken means already in use and NicknameInvalid means empty or unsaveable - both
						// retail callbacks, see SendNicknameRejected. Without this the create flow dead-ends on a
						// duplicate name.
						await CharacterCreationService.SendNicknameRejected(client, valueString!, commit == CharacterCreationService.NameCommitResult.Taken);
					}

					break;
				}

				case "Finish":
				{
					// The editor is done customizing; its SetMorphValue stream follows, carrying only changed
					// morphs, so the set varies and may be empty if nothing was morphed. Debounce the post-morph
					// trio off Finish and each morph so it fires once the stream goes quiet, regardless of which
					// morphs were sent.
					SchedulePostMorphBurst(client);
					break;
				}

				// Applies one editor SetMorphValue callback to the character's body. The value is a zigzag-encoded
				// signed slider value; CharacterMorphs decodes it and stores the signed value in the matching
				// CharacterBody column, and the spawn morph float is that value divided by 1000. Resource refs
				// (*hairmesh, *beardmesh, *texscript - the chosen hair, colours, tattoos and scars) are recorded in
				// editor send order and become the spawn scaffold's weighted entries through MorphTarget. Sliders not
				// yet seen in captures are logged and skipped.
				case "SetMorphValue":
				{
					// The retail creator acks every non-empty morph with a 0xBADF5A4B packet echoing the morph
					// value, confirmed from the dikketest capture. The post-morph trio is debounced off Finish
					// and each morph rather than keyed to a specific morph, because leaving the body default
					// means the thigh and body morphs are never sent.
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
					// On the post-creation world connection this is the last cutscene trigger - the beach
					// ApproachCasilda finale - so send the next chunk. On the editor connection it instead marks
					// the editor interactive, after which that connection drives itself.
					if (IsCreateWorldConnection(client)) // drive the cutscene Scry steps over the burst-less world
					{
						await SendCreateCutsceneStep(client, token, rawValue);
						break;
					}

					_logger.LogInformation("Character-editor: IntroDone - editor is interactive; the editor connection is now silent until enter-world.");
					break;
				}

				// The intro cutscene is a call and response: each client script trigger - ClientLoadingDone, then
				// StartScript(7088774), StartScript(7124502), Move and IntroDone - gets the matching Scry response, with
				// StartScript echoing the requested group id from field 3.
				case "ClientLoadingDone":
				case "StartScript":
				case "Move":
				{
					if (IsCreateWorldConnection(client)) // drive the cutscene Scry steps over the burst-less world
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

		/// <summary>
		///     The post-creation world entry: the handshake plus the burst-less live-character world. The intro cutscene
		///     follows separately, on CharInPlayIIR (SendCreateIntroCutscene).
		/// </summary>
		internal async Task SendCreateWorldWithCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// (a) The create-world handshake: the three records the verbatim scene burst opened with. The shared Hello
			// handshake is skipped for a create-world entry, so it is sent here.
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchReportDimesionID,
				(byte[])[0x00, 0x01, 0x31]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchReportServerID,
				(byte[])[0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x03]));
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchAckAuthenticate,
				(byte[])[0x00, 0x00, 0x00, 0x01]));

			// The one token push retail opens a new character with: `VeteranPoints` 0/0, a statement that the wallet is
			// empty rather than a credit. It is in five of the six character-create captures and in no login capture,
			// which is why it is here and not on the shared world entry.
			//
			// Retail's exact slot is between the entry teleport and the map change, with the three
			// `DispatchSetItemStat` channel registrations after it. This one goes immediately before the world burst,
			// whose first record is that map change, so retail's relative order is kept; the difference is the
			// registrations, which this create path sends on a different leg entirely.
			await _alternateAdvancement.StateWallet(client, TokenPool.VeteranPoints);

			// (b) The live-character world, constructed by the same path an existing-character login uses. The intro
			// cutscene does not follow here - see SendCreateIntroCutscene for why it waits for CharInPlayIIR.
			await _worldEntry.SendBurstlessWorldEntry(client, client.Session.Character.MapId);
		}

		/// <summary>
		///     Starts the destiny cutscene: Scry scripts only - the begin marker, the scene-load character_creation step,
		///     and the two opening script groups. The per-callback steps follow through SendCreateCutsceneStep.
		/// </summary>
		/// <remarks>
		///     Sent in response to the create-world CharInPlayIIR, after the ready burst, which is the order retail uses
		///     (Blablatest create: c2s CharInPlayIIR fr317, s2c 0x2024 fr324, s2c cutscene fr325). Sending it with the
		///     world entry instead puts the cutscene first, so the client's CharInPlayIIR - and therefore
		///     ReadyForPlayScreen - lands in the middle of the intro and the world snaps back to the un-ready state.
		/// </remarks>
		internal static async Task SendCreateIntroCutscene(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;

			// EnergyIIR (0x40749B8A) seed on the avatar, constructed through EnergyCodec and byte-exact-locked by
			// CreateEditorRecordTests. env=1, value=0x36B0.
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

		/// <summary>
		///     The live wire handle of the in-zone NPC named <paramref name="npcName" />, the cinematic actor to bind, or
		///     <paramref name="fallback" /> when it is not loaded. Used to re-address the cutscene's actor reference
		///     (ThisIsKalanthes f6) to the live entity.
		/// </summary>
		private uint LiveNpcHandle(INetworkClient client, string npcName, uint fallback)
		{
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
				return fallback;

			var npc = _simulation.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.Name == npcName);
			return npc?.Handle ?? fallback;
		}

		/// <summary>
		///     Re-persists the character's body after an appearance edit, so an edit that arrives after the name commit
		///     still survives.
		/// </summary>
		/// <remarks>
		///     Appearance edits - morph, resource, head, size, voice - mutate the in-memory <c>character.Body</c>, which is
		///     persisted once at name commit (CommitCharacterName into SaveCharacter). The client streams SetMorphValue
		///     after Finish and the naming can land first, or a morph can ride a later TCP segment, so an edit arriving
		///     afterwards would only touch the in-memory body and be lost at the spawn, which reloads from the database -
		///     showing the wrong hair, moustache or size intermittently. A no-op until the character is named, because the
		///     pre-name edits are flushed by the name-commit save, so the common path takes no extra write.
		///     <para>
		///         Saving the body is the edit landing: the spawn's appearance entries are derived from Body and
		///         BodyResources when the spawn is built, so there is no second copy to push the edit into.
		///     </para>
		/// </remarks>
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

			// A fallback only. The deterministic, retail-faithful trigger is FlushPostMorphBurstOnBatchDrained,
			// fired when the TCP receive that carried Finish and the SetMorphValue burst drains - retail sends
			// the trio once the client's customization batch is fully received, not after a wall-clock delay.
			// This timer catches the rare case where that burst is split across receives, so the editor can
			// never hang waiting for the trio.
			_ = Task.Run((Func<Task>)(async () =>
			{
				try
				{
					await Task.Delay(1500);

					// A newer morph or Finish arrived, so the burst is still streaming and a later task or the
					// batch drain will send it. TrySend also no-ops if it was already sent.
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

		/// <summary>
		///     One cutscene step per client callback, driving the beach and Kalanthes destiny sequence to IntroDone.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A call and response, decoded from Blablabla_* (port 7020); retail's world connection runs:
		///     </para>
		///     <code>
		///     C2S PlayResult                  -&gt; S2C ThisIsKalanthes
		///     C2S StartScript int=7088774     -&gt; S2C StartGroup 7088774  (then, ~2.7 s later, WelcomeTutorial)
		///     C2S StartScript int=7124502     -&gt; S2C StartGroup 7124502
		///     C2S Move                        -&gt; S2C Kneel
		///     C2S IntroDone                   -&gt; S2C StartFixed3d (plus StartGroup 7727158)
		///     </code>
		///     <para>
		///         The client requests each script group by id - StartScript carries it in field 3 - and waits for the
		///         server to echo back a StartGroup with the same id before it runs the group. Group 7088774 is
		///         Kalanthes's welcome-dialogue group, so without that echoed confirmation the speech never plays.
		///     </para>
		/// </remarks>
		/// <param name="groupId">The requested id from the StartScript callback; 0 for the other lifecycle callbacks.</param>
		private async Task SendCreateCutsceneStep(INetworkClient client, string token, uint groupId)
		{
			var id = client.Session.Account.ClientInstance;
			switch (token)
			{
				case "PlayResult":
				{
					// Cinematic actor binding: ThisIsKalanthes's f6 is {0xC350 type, Kalanthes entity handle}, which
					// tells the cutscene which live entity is Kalanthes - the walk-up and speech actor. Decoded from
					// Blablabla_* f658, where f6Field2 = 0x2C1C is the live Kalanthes spawn handle and matches the
					// MoveTask handle. Binds the live Kalanthes streamed by EnsureZoneNpcs, or falls back if unloaded.
					// Retail sends this in response to PlayResult, after the player commits to playing the cutscene;
					// binding on ClientLoadingDone, before the cinematic starts, loses the actor when it initialises.
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, "ThisIsKalanthes", f6Field1: 50000, f6Field2: LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					// Position Kalanthes next to the washed-ashore player, which is his cutscene start, so the walk-up
					// reads; his database spawn is back by the rocks. The walk path follows on the Move callback.
					await TortageIntroService.SendKalanthesMoveTask(client, TortageIntroService.BuildKalanthesIntroMove(LiveNpcHandle(client, "Kalanthes", 0x2c1c)));
					break;
				}

				case "ClientLoadingDone":
				{
					// World finished loading. Retail's beach binds the actor on PlayResult, above, so this is a no-op.
					break;
				}

				case "StartScript":
				{
					// Echo back the StartGroup the client just requested, with the same id: the confirmation that
					// runs the group on the client. Retail sends exactly this in response to each StartScript.
					await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, groupId));
					// 7088774 is Kalanthes's welcome-dialogue group; retail follows its StartGroup with the
					// WelcomeTutorial step about 2.7 s later with no triggering callback, so it is bundled here.
					if (groupId == 7088774)
						await CreateFlowWire.SendScry(client, ScryScriptCodec.Build(id, "WelcomeTutorial"));
					break;
				}

				case "Move":
				{
					// Kalanthes walks up to the player along the f890 path, then kneels.
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

		/// <summary>
		///     Arms the enter-world handoff: armed at creation-finished, fired once, whatever the client does or does not
		///     put on its bar.
		/// </summary>
		/// <remarks>
		///     Retail's trigger is time, measured from CharCreationFinished, and is independent of the bar. Across all 13
		///     create captures that reach a redirect, CharCreationFinished to s2c 0x2002 is 8.90-9.52 s (median 9.21, with
		///     one 10.16 s outlier on a slow session), while the last c2s before the redirect scatters over 0.31-1.65 s and
		///     is not even the same message each time: AddShortcut in 11, a StatIIR PassBlob in one, and a bare Ping in
		///     another (Fwfnwfjwk_soldier-guardian_create). The handoff must not key off a client packet that is sometimes
		///     absent - a character whose starting kit is empty bars nothing, sends no AddShortcut, and would sit on a
		///     black screen waiting for a message the client has no reason to send.
		/// </remarks>
		private void ScheduleEnterWorldHandoff(INetworkClient client)
		{
			ScheduleEnterWorldHandoff(client, EnterWorldHandoffDelay);
		}

		// The delay is a parameter only so the tests can arm it at zero; production always uses the retail-measured
		// EnterWorldHandoffDelay above.
		internal void ScheduleEnterWorldHandoff(INetworkClient client, TimeSpan delay)
		{
			// Arming is a claim too. Writing the latch unconditionally would leave the atomic claim below stopping only
			// two tasks that share one arm: arm, claim, send, arm again, claim, send again walks straight past it, and a
			// second 0x2002 re-redirects a client that is already reconnecting.
			//
			// A refusal here means the creation-finished path ran twice on one connection - a retried or duplicated
			// message - so it is logged rather than swallowed. It is not an error: refusing is the correct outcome, and
			// the client is already on its way to the world.
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
					// becomes legitimate more than once, and reading the flag then clearing it is not single-shot -
					// two readers can both pass before either writes. This compare-and-swap settles it and moves the
					// connection to the terminal `Sent` state.
					if (!client.Session.TryClaimEnterWorldHandoff())
						return;

					await _sendEnterWorldHandoff(client);
				}
				catch (Exception e)
				{
					// Never silent: a failure here looks to the player like a client waiting forever on a black screen,
					// so the reason has to be in the log.
					_logger.LogError(e, "Create-editor: failed to send the enter-world handoff; the client will hang on the loading screen.");
				}
			});
		}
		// Verbatim replay of the retail create-editor initial download from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (GameServer port 7030), rows 30-65.
		// This stage front-loads MapChange, SpawnObject/Spawn, AccessWindow, GameTime and the first nine 0xF98E10B3
		// records before the client sends its first 0x205F.

		// The deterministic trigger: the client sends Finish and its SetMorphValue batch in one burst, and retail
		// sends the post-morph trio once that receive batch is fully received. Called from the listener's
		// batch-drained hook (ReceivedBatchComplete). A no-op until Finish or a morph has been seen this session, and
		// it sends exactly once.
		internal static Task FlushPostMorphBurstOnBatchDrained(INetworkClient client)
		{
			return TrySendPostMorphBurstOnce(client);
		}

		// The retail create-editor character-creation script burst, from
		// Captures/account-stricken998-character-create-no-worldenter.tshark.json (GameServer port 7030), rows
		// 274-302 and 324-331.
		//
		// This is the burst that starts the character-creation cutscene. The key packets are the two ScryScriptIIR
		// remoteformats: token "playorskip(72)|...|NEW_character_creation Slave Galley (server-side)" then
		// "PlayOrSkip". Without them the playfield loads, with ambient audio, but the Slave Galley scene never
		// launches and the screen stays black. The preceding StatUpdate, BehaviorControlCenter, MagicBuff, Energy and
		// Stat packets set up the initial character and world state the script expects; the trailing SetMorphValue
		// packets are part of the same setup. All packets ride one conversation - sender hash("GameAgent") sub-0x59 to
		// receiver hash("GameInterface") plus instance varint - and reuse the editor temp instance, so a byte-for-byte
		// replay reproduces retail exactly, locked by BurstGoldenTests. The 32-bit values are the client's IIR
		// message-type hashes (see IirTypes and docs/re/aoc-opcode-ids.md); the bodies are kept verbatim, being opaque
		// editor state - entity handles 0x0009FFxx, morph mesh names, stat blobs - the client choreography expects.

		// The world-prep scene burst the creator sends after CharCreationFinished, before the client sends 0x206E.
		// Verbatim from Captures/Dikketest_rogue-barbarian_create_random-actions.tshark.json (creator gs013:7016,
		// idx 364-438, excluding the 0x2082/0x2009 keepalive replies, which are answered naturally), with the client
		// instance retargeted from the capture's 0x01316D2B to the local temp instance 0x021662C8 (receiver varint
		// AB DA C5 09 becomes C8 C5 D9 10, body instance 01 31 6D 2B becomes 02 16 62 C8). Each packet is
		// re-serialized as an Rpc so its CRC and length are recomputed for the retargeted bytes.

		/// <summary>
		///     Consumes a pending create-world-entry marker for <paramref name="clientInstance" />, returning true if one
		///     was set. The listener's world Hello calls this to recognise the post-creation world connection, skip the
		///     helper auth and take the create-world entry path; consuming it stops a later normal login re-triggering the
		///     intro.
		/// </summary>
		internal bool TryConsumePendingCreateWorldEntry(uint clientInstance)
		{
			return PendingCreateWorldEntry.TryRemove(clientInstance, out _);
		}

		/// <summary>
		///     Lazily creates and tracks the character's body row so the editor's morph, head-mesh, size and voice edits
		///     have somewhere to persist. The body is saved together with the character when the name is committed.
		/// </summary>
		private static CharacterBody EnsureBody(Character character)
		{
			// In-memory only: the body accumulates editor edits on the cached session character and is persisted as
			// part of the character graph at name commit (SaveCharacter). No database context is held during editing.
			character.Body ??= new CharacterBody
			{
				CharacterId = character.Id
			};
			return character.Body;
		}

		// The post-creation world connection: the second connection, opened after the 0x2002 enter-world handoff and
		// flagged in the Hello handler. It distinguishes that connection from the editor connection and from a normal
		// existing-character login, so the intro and cutscene bursts fire only there.
		private static bool IsCreateWorldConnection(INetworkClient client)
		{
			return client.Session is { EntryMode: WorldEntryMode.CreateWorld, Character.Name: not null };
		}

		// Appends the resource reference to the character's chosen-appearance list, deduplicated because the editor
		// can resend the set when a creation session is resumed. Persisted with the character graph at name commit.
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

		/// <summary>
		///     Sends the SetMorphValueIIR ack for one SetMorphValue; SetMorphValueCodec owns the layout.
		/// </summary>
		/// <remarks>
		///     The weight is a server-side default not present in the client's message: textures, whose values start with
		///     '*', use 1.0, and body morphs use -0.03. Those are the only two values seen in retail.
		/// </remarks>
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

		// The fixed trio the server sends after the last morph; the tails are verbatim from the dikketest capture with
		// the client instance substituted. This is what prompts the client to send the TheNameIs callback.
		private static async Task SendPostMorphBurst(INetworkClient client)
		{
			var instance = client.Session.Account.ClientInstance;
			var character = client.Session.Character;

			await CreateFlowWire.SendCreateEditor2000Record(client, StatCodec.Build(StatIirs.ForCharacter(instance, 0x0578, 0x00)));
			await CreateFlowWire.SendCreateEditor2000Record(client, ScryScriptCodec.Build(instance, 35820)); // StartScriptGroupOnClient

			// The editor's class-base push: stats 0x0001, 0x01F9 and 0x01FB for the class the player just chose. Retail
			// sends this character's own base - the Blablabla guardian create carries 12000/17250/0 at f2613, which is
			// RDB 1000511's Guardian row (12000, 17250) to the unit - so the values are re-derived per class rather than
			// replayed from one capture. A single captured triple would be one class's row handed to every class alike,
			// giving an Assassin (9000 / 23000) a health base 20 too high and a stamina base 58 too low. The class curve
			// is the same source the snapshot's own max slots use.
			var (health, mana, stamina) = CharacterMaxStats.ClassBaseHundredths(character.Class, character.Level);
			// Stat ids in retail's order - health, stamina, mana, per the captured f2613 body. The record is
			// StatUpdateCodec's and is sent whole, head included, rather than hand-writing the
			// `[u32 0][u8 count][id][value]*` tail.
			var classBase = new CharacterStatBlock()
				.SetRaw(CharacterStatId.HealthMax, health)
				.SetRaw(CharacterStatId.StaminaMax, stamina)
				.SetRaw(CharacterStatId.ManaMax, mana);

			await CreateFlowWire.SendCreateEditor2000Record(client, StatUpdateCodec.Build(new StatUpdateIIR(instance, classBase)));
		}

		private static async Task TrySendPostMorphBurstOnce(INetworkClient client)
		{
			// A MorphActivitySequence above 0 means Finish, or a morph, has been processed, i.e. the customization is
			// being committed. Guarded single-send so the batch-drain trigger and the fallback timer cannot double it.
			if (client.Session.MorphActivitySequence == 0 || client.Session.PostMorphBurstSent)
				return;

			client.Session.PostMorphBurstSent = true;
			await SendPostMorphBurst(client);
		}


		// Answers the client's TheNameIs callback so it knows the name was accepted and advances to enter-world. The
		// sequence, from the dikketest capture at idx 359-363, is 0xADCE0CDA echoing the name, an 0xA36D3B74
		// "NicknameOk" callback echoing the name, a fixed 0xF508F4C1, and an 0xA36D3B74 "CharCreationFinished".
		private async Task SendNicknameAccepted(INetworkClient client, string name)
		{
			var instance = client.Session.Account.ClientInstance;

			await CreateFlowWire.SendIirRecord(client, CreateFlowWire.MorphAckSender, CreateFlowWire.Receiver(client), SetNameCodec.Build(SetNames.Accepted(instance, name)));

			await CreateFlowWire.SendA36DCallback(client, "NicknameOk", name);
			// The fixed post-name GameActionList for dynel 0xCFEF; the header is GameActionListCodec's and only the
			// still-opaque action tail rides verbatim.
			await CreateFlowWire.SendCreateEditor2000Record(client, GameActionListCodec.Build(new GameActionListIIR(instance, 0x0000CFEF, 0,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
			])));
			await CreateFlowWire.SendA36DCallback(client, "CharCreationFinished", "");

			// Grant the class's starting spells so the client places them on the starting bar. Retail sends the class
			// spells as DispatchLearnSpell (0x200A) on the GameCharInterface framing, during create and right before the
			// client's 0x206E bar drags (Blablatest necromancer f239: Ice Strike R1 and Frozen Hatred). Class-aware, so
			// casters get spells and melee classes get none. The client bars only what it was granted, so without this a
			// fresh caster's bar has abilities but no spells.
			//
			// This hands the client the kit its bar is built from; it answers each grant with an AddShortcut (0x206E),
			// which the shortcut handler persists. It is the character's starting bar and not what moves the flow
			// forward - see ScheduleEnterWorldHandoff.
			await CharacterLoadout.GrantToClient(client, _database, _logger, CreateFlowWire.CreateGrantSender);

			// Creation is finished; the world handoff runs on a timer rather than on the client barring something.
			ScheduleEnterWorldHandoff(client);
		}
	}
}
