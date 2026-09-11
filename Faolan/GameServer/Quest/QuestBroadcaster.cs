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
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Quest
{
	/// <summary>
	///     What the client is told about quests: the journal packets (full update, log list, goal, state IIR, the
	///     reward window) and the overhead '!' / '?' markers.
	/// </summary>
	/// <remarks>
	///     One of three pieces, and the one that says which packet goes to whom:
	///     <see cref="QuestProgressionService" /> owns the supersession engine (what the quest state becomes),
	///     <see cref="QuestMarkerResolver" /> owns the marker decision (which byte an NPC deserves for this player),
	///     and this owns the wire.
	///     <para>
	///         The dependency on <see cref="World.WorldBroadcaster" /> must stay one-way: this class holds nothing of
	///         the broadcaster's and the broadcaster calls in (<c>SendQuestMarkerIfGiver</c>, on every streamed-in
	///         NPC). The three <see cref="IQuestOutbound" /> members that genuinely need the world
	///         (RefreshQuestVisibility, ScheduleQuestAmbush, SetNpcFollowByTemplate) therefore stay on the
	///         broadcaster, and <see cref="QuestOutbound" /> reassembles the two halves behind the one port.
	///     </para>
	/// </remarks>
	public sealed class QuestBroadcaster
	{
		private readonly ICharacterQuestRepository _quests;
		private readonly IWorldRepository _world;
		private readonly ILogger _logger;
		private readonly QuestMarkerResolver _questMarkers;
		private readonly WorldSimulation _sim;

		public QuestBroadcaster(WorldSimulation sim, ICharacterQuestRepository quests, IWorldRepository world, QuestMarkerResolver questMarkers, ILogger logger)
		{
			_sim = sim;
			_quests = quests;
			_world = world;
			_questMarkers = questMarkers;
			_logger = logger;
		}

		// Applies the after-quest world-object effects for this player: every object on their map whose EffectQuestId
		// they hold active or completed gets its SimpleDynel visual change pushed (e.g. the beach chain-wall collision
		// dropping when the escort begins). Data-driven through WorldObject.EffectQuestId/EffectDynelState, with no
		// hard-coded lookup. Called at world entry, so a returning player is not re-blocked by the burst, and on quest
		// supersession or completion, so the change fires the instant the player reaches the quest state.
		public async Task ApplyQuestWorldEffects(INetworkClient client)
		{
			var character = client.Session.Character;
			var player = _sim.PlayerWorldEntity(client);
			if (character == null || player == null)
				return;

			var held = (await _quests.GetCharacterQuests(character.Id)).Where(q => q.State is QuestState.Active or QuestState.Completed).Select(q => q.QuestId).ToHashSet();
			if (held.Count == 0)
				return;

			foreach (var obj in _sim.World.ZoneForEntity(player).WorldObjects)
			{
				if (obj.Row.EffectQuestId is not { } eq || !held.Contains(eq))
					continue;

				// The column stores the visual-change kind, and the one value any row ships is SetKdTree - the chain
				// decor dropping its collision. The kind selects a typed payload, so an unknown one cannot be built
				// blind and is reported rather than guessed at.
				if ((DynelVisualChange)obj.Row.EffectDynelState != DynelVisualChange.SetKdTree)
				{
					_logger.LogWarning("Quest world effect: '{Obj}' 0x{Handle:X8} asks for visual-change kind {Kind}, which carries a payload we have no source for - skipped.",
						obj.Row.Name, obj.Handle, obj.Row.EffectDynelState);
					continue;
				}

				await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetKdTree(WorldObjectColumns.TypeTagForClass(obj.SubOpcode), obj.Handle, 0)));
			}
		}

		// Drops the reach-goal cache so the next move reloads it: a supersession may have granted a stage carrying a
		// new ReachArea goal. A no-op when the player has no live entity yet.
		public void InvalidateReachGoals(INetworkClient client)
		{
			var player = _sim.PlayerWorldEntity(client);
			player?.Knowledge.PendingReachGoals = null!;
		}

		// Re-sends one NPC's quest marker in place (handles are session-volatile, so the NPC is resolved in the
		// player's zone rather than by handle). Unlike RefreshQuestGiverMarker this does not gate on the quest-giver
		// map, so it also drives the '?' talk marker for an NPC the player must talk to next - e.g. Casilda after
		// Tuut's key, which activates the talk stage while she is out of the conversation and stream-in paths that
		// would otherwise refresh her marker.
		//
		// Resolved by template where the goal has one, name only as the fallback: a marker put on the wrong same-named
		// NPC points the player at a creature whose greeting will not advance anything.
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

		public Task<QuestMarkerContext> MarkerContextFor(Character character)
		{
			return _questMarkers.ContextFor(character);
		}

		// Call after any write to a character's quests: the cached marker context is stale from that moment.
		public void ForgetMarkerContext(uint characterId)
		{
			_questMarkers.Forget(characterId);
		}

		// Sends a QuestFullUpdate (one quest's full detail: description, summary, objective + map marker) built from
		// its seeded definition.
		//
		// Nothing per-character rides along: the goal record's reserved (marker, 4CC-tag) pair is quest data and comes
		// off the goal row.
		public async Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
		{
			// The per-quest level is a constant harvested into the definition; the captured default applies only when
			// this quest had no full update in the captures (Level == 0).
			var levelValue = level ?? (def.Level != 0
				? def.Level
				: QuestFullUpdateCodec.DefaultQuestLevel);
			// The record's four reward lists: the quest's own harvested set and the line's catalog reward, each element
			// carrying its item's TEA key - so the Item rows are loaded here, keyed by template.
			var templates = def.Rewards.Select(r => r.ItemTemplateId).Concat(def.Line?.Rewards.Select(r => r.ItemTemplateId) ?? []).ToArray();
			var items = await _world.GetItemsByTemplate(templates);
			var rewards = QuestRewardLists.ForQuest(def, def.Line, client.Session.Character.Id, id => items.GetValueOrDefault(id));
			var body = QuestFullUpdateCodec.Build(QuestFullUpdates.ForQuest(client.Session.Character.Id, def, levelValue, rewards));

			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// Sends a QuestGoal (objective progress) to the player.
		public static Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestGoalCodec.Build(client.Session.Character.Id, questId, goalId, update))));
		}

		// Sends a QuestIIR (add/update a quest in the player's journal) to the player.
		public static Task SendQuestIir(INetworkClient client, uint questId, uint state)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestCodec.Build(client.Session.Character.Id, questId, state))));
		}

		// Sends the pick-one-of-N reward offer. The addressed instance is the character id, the same slot every other
		// quest packet here uses.
		public static Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestRewardNotifyCodec.Build(client.Session.Character.Id, questId, options))));
		}

		// The confirm: the same IIR with no options. Retail's carries the chosen index in the trailing block's fourth
		// word; the rest of that block is undecoded, so it is placed exactly where the capture put it.
		public static Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index)
		{
			var confirm = new QuestRewardNotifyIIR(client.Session.Character.Id, 0x01, questId, [], [0, 0, 0, index, 0]);
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(QuestRewardNotifyCodec.Build(confirm))));
		}

		// The quest-marker state byte for a handle the client polled via 0x2020, resolving the entity in the player's
		// zone. 0x00 when it is not a quest-giver in view. It is what lets the poll reply (SendSpawnCheckResponse)
		// carry the state-driven marker.
		public async Task<byte> QuestMarkerStateForHandle(INetworkClient client, uint handle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player != null && _sim.World.ZoneForEntity(player).TryGet(handle, out var npc) && !npc.IsPlayer)
				return await _questMarkers.MarkerStateFor(client, npc);

			return QuestMarkerResolver.None;
		}

		// Re-sends a quest-giver's marker to one viewer after their quest state changed (accept or complete) while the
		// giver is already in view. Retail updates it in place, not only on stream-in (capture frame 698: Casilda ->
		// 0x00 the instant the quest is accepted). Resolves the entity in the player's zone, then recomputes.
		public async Task RefreshQuestGiverMarker(INetworkClient client, uint npcHandle)
		{
			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			if (_sim.World.ZoneForEntity(player).TryGet(npcHandle, out var npc) && !npc.IsPlayer)
				await SendQuestMarkerIfGiver(client, npc);
		}

		// Drives a quest-giver NPC's marker from the viewer's quest state when it enters view. Recomputed each time the
		// NPC is streamed in, so the '!' transitions to '?' and clears as the player accepts and completes the quest -
		// e.g. Casilda shows '!' before, nothing while held, '?' once the kill goal is done and it is time to turn in.
		// `context` is the character's quest state read once for a batch of NPCs; a caller with one NPC may leave it
		// null and one is built for that call.
		public async Task SendQuestMarkerIfGiver(INetworkClient client, WorldEntity npc, QuestMarkerContext? context = null)
		{
			if (npc.Name == null || client.Session.Character == null)
				return;

			// The gate is "quest-relevant", not "giver": an NPC that grants nothing can still wear a marker - retail
			// lights 38 templates that grant nothing at all - so it has to admit the goal-target relation too, or the
			// silver rule below it can never fire for the population it was written for.
			if (!await _questMarkers.IsQuestRelevant(npc.NpcTemplateId))
				return; // no quest touches this NPC -> no marker

			context ??= await _questMarkers.ContextFor(client.Session.Character);
			await SendQuestMarker(client, npc.Handle, await _questMarkers.MarkerStateFor(client, npc, context));
		}

		// Sends the QuestJournal list (one title line per quest). The shared area header (region/map names) is taken
		// from the first definition. The trailing "tracked quest" pointer is the captured no-track block; pinning the
		// multi-quest tracked variant needs more decode.
		// Currently uncalled, and deliberately kept: retail bundles a QuestJournal with QuestIIR x2 + QuestFullUpdate
		// in a single frame when a quest is accepted in-session, which the accept path here does not emit yet.
		//
		// Before reusing it, note that it takes the area header from `definitions[0]` and files every entry under that
		// one area, which is only correct for a single-area call - otherwise quests from many areas render as one
		// collapsed group.
		public static Task SendQuestJournal(INetworkClient client, IReadOnlyList<QuestDefinition> definitions)
		{
			var body = QuestJournalCodec.Build(QuestJournals.ForQuests(client.Session.Character.Id, definitions));
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// Sends the active-quest list (0x504B7924) that populates the J-panel: without it an accepted quest never
		// appears in the log even though the client has its QuestFullUpdate detail (decoded from the grant capture).
		// `trackedDef` is the highlighted, just-accepted quest; `activeQuestIds` are all the player's active quests
		// with the tracked one first. The area-title slot comes from the tracked quest's definition.
		public static Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
		{
			// The line row supplies the two per-line trailer words - the same MainQuest_c slots the login snapshot's
			// index block fills, so the grant and the snapshot have to agree or the client's quest tracker sorts one
			// way at login and another after an accept. `Line` is an Included navigation on the paths that reach here;
			// lazy loading is off, so a null one means "not loaded" and the codec falls back to the solo values.
			var body = MainQuestUpdateCodec.Build(client.Session.Character.Id, trackedDef.QuestLineId, trackedDef.QuestId, activeQuestIds,
				QuestTextTokens.Record(trackedDef, QuestTextSlot.QuestLineTitle), trackedDef.Line);
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// The J-panel remove: the same `MainQuestUpdateIIR` with `Operation = Remove`, naming only the line whose
		// section is going away. Retail sends it as the third packet of a quest drop, and its body is this very record
		// with an empty list rather than a shorter variant.
		//
		// `--report iir-bodies --report-arg MainQuestUpdate` finds six of them across five captures, and they are
		// byte-identical but for the handle and the line id:
		//
		//   504b7924 0000c350 <handle> 01 00000002 <lineId> + 24 zero bytes      (45 bytes)
		//
		// Which is exactly what `MainQuestUpdateCodec` writes for `Remove` with no entries, no title and zero
		// trailers: operation(4) + line(4) + tracked 0(4) + count 0(4) + empty string(2) + three trailers(12) +
		// leaving(2). The client's own reader settles it too - `FUN_00918560` has no branch on the operation word: it
		// reads three scalars, a count, that many pairs, the title string and the trailers, unconditionally. The
		// "leading constant 1" in that position is the operation, and it is 2 on every captured empty list.
		public static Task SendQuestLogRemove(INetworkClient client, uint questLineId)
		{
			var body = MainQuestUpdateCodec.Build(new MainQuestUpdateIIR(client.Session.Character.Id, MainQuestUpdateOp.Remove, questLineId, 0, [], ""));
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
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
