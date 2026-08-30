using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	// What the client is TOLD about quests (#826): the journal packets (full update, log list, goal, state IIR, the
	// reward window) and the overhead '!' / '?' markers.
	//
	// It is the third piece of the quest split, and the one that says which packet goes to whom:
	// `QuestProgressionService` owns the supersession ENGINE (what the quest state becomes), `QuestMarkerResolver`
	// owns the marker DECISION (which byte an NPC deserves for this player), and this owns the WIRE.
	//
	// It came off `WorldBroadcaster`, whose subject is `WorldEntity` - spawn, despawn, move, interest. A quest packet
	// is addressed to a PLAYER about their journal, so it never belonged there. ⚠️ The dependency runs ONE WAY: this
	// class holds nothing of the broadcaster's, and the broadcaster calls IN (`SendQuestMarkerIfGiver`, on every
	// streamed-in NPC). Keep it that way - the three IQuestOutbound members that genuinely need the world
	// (RefreshQuestVisibility, ScheduleQuestAmbush, SetNpcFollowByTemplate) stay on the broadcaster, and
	// `QuestOutbound` is what puts the two halves back together behind the one port.
	public sealed class QuestBroadcaster
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly QuestMarkerResolver _questMarkers;
		private readonly WorldSimulation _sim;

		public QuestBroadcaster(WorldSimulation sim, IDatabaseRepository database, QuestMarkerResolver questMarkers, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_questMarkers = questMarkers;
			_logger = logger;
		}

		// #262: apply the AFTER-QUEST world-object effects for this player - every object on their map whose EffectQuestId
		// they hold ACTIVE or COMPLETED gets its SimpleDynel visual-change pushed (e.g. the beach chain-wall collision
		// dropping when the escort begins). Data-driven (WorldObject.EffectQuestId/EffectDynelState), no hard-coded
		// lookup. Called at world entry (a returning player isn't re-blocked by the burst) and on quest supersession /
		// completion (the change fires the instant the player reaches the quest state).
		public async Task ApplyQuestWorldEffects(INetworkClient client)
		{
			var character = client.Session.Character;
			var player = _sim.PlayerWorldEntity(client);
			if (character == null || player == null)
				return;

			var held = (await _database.GetCharacterQuests(character.Id)).Where(q => q.State is QuestState.Active or QuestState.Completed).Select(q => q.QuestId).ToHashSet();
			if (held.Count == 0)
				return;

			foreach (var obj in _sim.World.ZoneForEntity(player).WorldObjects)
			{
				if (obj.Row.EffectQuestId is not { } eq || !held.Contains(eq))
					continue;

				// The column stores the visual-change KIND, and the one value any row ships is SetKdTree - the chain
				// decor dropping its collision. Now that a kind selects a typed payload (#678) an unknown one cannot
				// be built blind, so it is reported rather than guessed at.
				if ((DynelVisualChange)obj.Row.EffectDynelState != DynelVisualChange.SetKdTree)
				{
					_logger.LogWarning("Quest world effect: '{Obj}' 0x{Handle:X8} asks for visual-change kind {Kind}, which carries a payload we have no source for - skipped.",
						obj.Row.Name, obj.Handle, obj.Row.EffectDynelState);
					continue;
				}

				await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetKdTree(WorldObjectColumns.TypeTagForClass(obj.SubOpcode), obj.Handle, 0)));
			}
		}

		// #262: drop the reach-goal cache so the next move reloads it (a supersession may have granted a stage carrying
		// a new ReachArea goal). No-op if the player has no live entity yet.
		public void InvalidateReachGoals(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			player?.PendingReachGoals = null!;
		}

		// #267: re-send one NPC's quest marker in-place (handles are session-volatile, so the NPC is resolved in the
		// player's zone rather than by handle). Unlike RefreshQuestGiverMarker this does NOT gate on the quest-GIVER map,
		// so it also drives the '?' talk marker for an NPC the player must talk to next (e.g. Casilda after Tuut's key,
		// which activates the talk stage while she's out of the conversation/stream-in path that would otherwise refresh
		// her marker).
		//
		// #521: resolved by TEMPLATE where the goal has one, name only as the fallback - a marker put on the wrong
		// same-named NPC points the player at a creature whose greeting will not advance anything.
		public async Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null || string.IsNullOrEmpty(npcName) && npcTemplateId is not > 0)
				return;

			var npc = npcTemplateId is > 0 and { } templateId
				? _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && e.NpcTemplateId == templateId)
				: _sim.World.ZoneForEntity(player).Entities.FirstOrDefault(e => !e.IsPlayer && string.Equals(e.Name, npcName, StringComparison.OrdinalIgnoreCase));

			if (npc != null)
				await SendQuestMarker(client, npc.Handle, await _questMarkers.MarkerStateFor(client, npc));
		}

		// Sends a QuestFullUpdate (one quest's full detail: description, summary, objective + map marker) built from
		// its seeded definition.
		//
		// Nothing per-character rides along: what used to be passed here as an "accept time" is the goal record's
		// reserved (marker, 4CC-tag) pair, which is quest DATA and now comes off the goal row (#537).
		public async Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			// The per-quest level is a constant harvested into the definition (#352); fall back to the captured default
			// only when this quest had no full-update in the captures (Level == 0).
			var levelValue = level ?? (def.Level != 0
				? def.Level
				: QuestFullUpdateCodec.DefaultQuestLevel);
			// The record's four reward lists (#893): the quest's own harvested set and the line's catalog reward, each
			// element carrying its item's TEA key - so the Item rows are loaded here, keyed by template.
			var templates = def.Rewards.Select(r => r.ItemTemplateId).Concat(def.Line?.Rewards.Select(r => r.ItemTemplateId) ?? []).ToArray();
			var items = await _database.GetItemsByTemplate(templates);
			var rewards = QuestRewardLists.ForQuest(def, def.Line, client.Session.Character.Id, id => items.GetValueOrDefault(id));
			var body = QuestFullUpdateCodec.Build(QuestFullUpdates.ForQuest(client.Session.Character.Id, def, levelValue, rewards));

			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// Sends a QuestGoal (objective progress) to the player.
		public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestGoalCodec.Build(client.Session.Character.Id, questId, goalId, update))));
		}

		// Sends a QuestIIR (add/update a quest in the player's journal) to the player.
		public Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestCodec.Build(client.Session.Character.Id, questId, state))));
		}

		// Sends the pick-one-of-N reward OFFER (#815). The addressed instance is the character id, the same slot every
		// other quest packet here uses.
		public Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options)
		{
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestRewardNotifyCodec.Build(client.Session.Character.Id, questId, options))));
		}

		// The confirm: the same IIR with NO options. Retail's carried the chosen index in the trailing block's fourth
		// word - undecoded, so it is placed exactly where the capture put it and nothing else is claimed about it.
		public Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index)
		{
			var confirm = new QuestRewardNotifyIIR(client.Session.Character.Id, 0x01, questId, [], [0, 0, 0, index, 0]);
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestRewardNotifyCodec.Build(confirm))));
		}

		// The quest-marker state byte for a handle the client polled via 0x2020 (resolves the entity in the player's
		// zone). 0x00 when it isn't a quest-giver in view. Lets the poll reply (SendSpawnCheckResponse) carry the
		// correct, state-driven marker instead of a hardcoded value.
		public async Task<byte> QuestMarkerStateForHandle(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(handle, out var npc) && !npc.IsPlayer)
				return await _questMarkers.MarkerStateFor(client, npc);

			return QuestMarkerResolver.None;
		}

		// #241: re-send a quest-giver's marker to one viewer after their quest state changed (accept/complete) while
		// the giver is ALREADY in view. Retail updates it in-place, not only on stream-in (capture frame 698: Casilda
		// -> 0x00 the instant the quest is accepted). Resolves the entity in the player's zone, then recomputes.
		public async Task RefreshQuestGiverMarker(INetworkClient client, uint npcHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			if (_sim.World.ZoneForEntity(player).TryGet(npcHandle, out var npc) && !npc.IsPlayer)
				await SendQuestMarkerIfGiver(client, npc);
		}

		// #241: drive a quest-giver NPC's marker from the viewer's quest state when it enters view. Recomputed each
		// time the NPC is streamed in, so the '!' transitions to '?' (and clears) as the player accepts/completes the
		// quest - e.g. Casilda shows '!' before, nothing while held, '?' once the kill goal is done (return to turn in).
		public async Task SendQuestMarkerIfGiver(INetworkClient client, WorldEntity npc)
		{
			if (npc.Name == null || client.Session.Character == null)
				return;

			// #857: "quest-relevant", not "giver". An NPC that grants nothing can still wear a marker - retail lights
			// 38 templates that grant nothing at all - so the gate has to admit the goal-target relation too, or the
			// silver rule below it can never fire for the population it was written for.
			if (!await _questMarkers.IsQuestRelevant(npc.NpcTemplateId))
				return; // no quest touches this NPC -> no marker

			await SendQuestMarker(client, npc.Handle, await _questMarkers.MarkerStateFor(client, npc));
		}

		// Sends the QuestJournal list (one title line per quest). The shared area header (region/map names) is taken
		// from the first definition. The trailing "tracked quest" pointer is the captured no-track block; pinning the
		// multi-quest tracked variant needs more decode.
		// ⚠️ CURRENTLY UNCALLED, and deliberately kept. Retail bundles a QuestJournal with QuestIIR x2 +
		// QuestFullUpdate in a SINGLE frame when a quest is accepted in-session; our accept path does not emit it yet.
		// Its former caller (RestorePlayerQuests) was a fabricated login burst and is gone (#389/#390).
		//
		// ⚠️ Before reusing it: it takes the area header from `definitions[0]` and files EVERY entry under that one
		// area. That is only correct for a single-area call - it is what rendered ~28 quests from 20 areas as one
		// collapsed group in the Completed tab.
		public static Task SendQuestJournal(INetworkClient client, IReadOnlyList<QuestDefinition> definitions)
		{
			var body = QuestJournalCodec.Build(QuestJournals.ForQuests(client.Session.Character.Id, definitions));
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// Sends the active-quest LIST (0x504B7924) that POPULATES the J-panel - WITHOUT it an accepted quest never
		// appears in the log even though the client got its QuestFullUpdate detail (decoded from the grant capture). trackedDef is the highlighted (just-accepted) quest; activeQuestIds are all the
		// player's active quests with the tracked one FIRST. The area-title slot comes from the tracked quest's def.
		public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			// The line row supplies the two per-LINE trailer words (#896) - the same MainQuest_c slots the login
			// snapshot's index block fills, so the grant and the snapshot have to agree or the client's quest tracker
			// sorts one way at login and another after an accept. `Line` is an Included navigation on the paths that
			// reach here; lazy loading is off, so a null one legitimately means "not loaded" and the codec falls back
			// to the SOLO values, which is what this path sent before the columns existed.
			var body = MainQuestUpdateCodec.Build(client.Session.Character.Id, trackedDef.QuestLineId, trackedDef.QuestId, activeQuestIds,
				QuestTextTokens.Record(trackedDef, QuestTextSlot.QuestLineTitle), trackedDef.Line);
			return client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// DispatchSetQuestMarker body: [u32 0xC350][u32 npcHandle][u8 state], on the GameCharInterface dispatch
		// (same framing as the static create-editor marker burst).
		private static Task SendQuestMarker(INetworkClient client, uint npcHandle, byte state)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(npcHandle).WriteByte(state)));
		}
	}
}
