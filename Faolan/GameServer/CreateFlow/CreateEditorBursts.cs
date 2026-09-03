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
	// The three retail CREATE-EDITOR bursts (the initial scene, the creation-script burst, the post-ready burst) and the
	// editor's scene tables and stat blocks they are built from - record for record what the captures show the
	// editor connection receiving. Pure functions of the client (and the DB, for the scene dynels), so a static class;
	// they were static members of CreateFlowService until #928 item 8.
	public static class CreateEditorBursts
	{
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
				return CreateFlowWire.SendIirRecord(client, sender, receiver, record);
			}
		}

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
			await Record(ScryScriptCodec.Build(id, CreateFlowService.CreateWorldScriptToken, 7, resources: CreateFlowService.CreateWorldResourceManifest, f6Field1: 6177372, f6Field2: 9422303));
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
				return CreateFlowWire.SendIirRecord(client, sender, receiver, record);
			}
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
			await CreateFlowWire.SendIirRecord(client, Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x59), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), CharInPlayCodec.Build(new CharInPlayIIR(id)));

			// The racial CHARACTER-TEMPLATE choice list the creation editor reads (Male/Female/StygianSlaver ->
			// template ids 50000:65525x), delivered on two GameActionListIIR (0xF508F4C1) records for dynel 0xD00C.
			// The two captured variants differ ONLY in the leading action-id word; the property-map action-tail
			// grammar is still opaque (#190) so it rides verbatim, while the record HEADER is constructed via
			// GameActionListCodec.Build (construct-don't-replay - see CreateFlowWire.SendGameActionListRecord).
			await CreateFlowWire.SendGameActionListRecord(client, 0x59, id, 0xD00Cu,
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x39, 0xBF, 0x93, 0x00, 0x50, 0x4D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65,
				0x3D, 0x35, 0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x32, 0x3B, 0x46, 0x65, 0x6D, 0x61, 0x6C, 0x65, 0x54, 0x65, 0x6D, 0x70, 0x6C, 0x61, 0x74, 0x65, 0x3D, 0x35,
				0x30, 0x30, 0x30, 0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x33, 0x3B, 0x53, 0x74, 0x79, 0x67, 0x69, 0x61, 0x6E, 0x53, 0x6C, 0x61, 0x76, 0x65, 0x72, 0x3D, 0x35, 0x30, 0x30, 0x30,
				0x30, 0x3A, 0x36, 0x35, 0x35, 0x32, 0x35, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00
			]);
			await CreateFlowWire.SendGameActionListRecord(client, 0x59, id, 0xD00Cu,
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
	}
}
