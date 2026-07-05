using System.Collections.Generic;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network
{
	public interface INetworkClientSession
	{
		Account Account { get; set; }

		// Set after CharCreationFinished + the world-prep burst: the creator is now waiting for the client's
		// 0x206E enter-world signal, which we answer with the 0x2002 world-address handoff.
		bool AwaitingEnterWorld { get; set; }
		bool HasFlushedCreateEditorPostReady { get; set; }

		bool HasFlushedCreateWorldEntryBurst { get; set; }
		bool HasPendingCreateEditorReadyFollowUp { get; set; }
		bool HasPendingPost205FFollowUp { get; set; }

		// True on the character-creator (editor) GameServer connection: set in the Hello handler when a
		// not-yet-named character connects.
		bool IsCreateEditorConnection { get; set; }

		// True on the post-creation WORLD GameServer connection (the second connection, opened after the
		// 0x2002 enter-world handoff). Gates the intro/cutscene bursts so they only fire on the first world
		// entry, not on normal existing-character logins.
		bool IsCreateWorldSession { get; set; }

		// True on the OLD connection while a teleport/changemap handoff is in flight: DoTeleport has already
		// persisted the authoritative destination position, so this connection's disconnect must NOT save its
		// stale pre-teleport live position back over it (it would clobber the bind/destination). See #28.
		bool IsTeleportingOut { get; set; }
		bool PostMorphBurstSent { get; set; }

		// True once this connection has completed world entry (the burst-less world entry, the only entry path).
		// Gates the in-world behaviours that must not fire on a connection that hasn't entered the world: the
		// 0x2020 -> 0x2008 SpawnCheck responder, the NPC conversation system, and the dialogue-close fallback.
		// (The create-world flow uses IsCreateWorldSession alongside this.)
		bool WorldEntered { get; set; }

		// The client's conversation id for this connection (the value bytes of the incoming packet's sender
		// field), captured per packet. Auth-handoff responses (UniverseAgent/PlayerAgent) echo this back as the
		// receiver, exactly as retail does, instead of a hardcoded capture-derived id.
		byte[] ClientConversation { get; set; }

		Character Character { get; set; }

		// Live NPC entity handle -> name, captured from the world-burst spawns (FA015807) actually sent this session.
		// NPC dynel handles are session-volatile, so this is how a clicked handle is resolved to the stable NPC name
		// that conversation routing keys on. Populated when the burst is replayed.
		Dictionary<uint, string> SpawnedNpcNamesByHandle { get; }

		// Live NPC entity handle -> max health (player-facing = the spawn's CharacterStats.health / 100), captured
		// from the world-burst spawns. Combat seeds an NPC's vitals from this so its health bar matches what the
		// client rendered from the spawn, instead of a flat placeholder. Populated alongside SpawnedNpcNamesByHandle.
		Dictionary<uint, uint> SpawnedNpcMaxHealthByHandle { get; }

		// Index of the next post-creation world cutscene chunk to send. Advanced once per client script
		// trigger (ClientLoadingDone, StartScript, Move, IntroDone) so each chunk is sent in order.
		int CreateWorldChunkIndex { get; set; }

		// Monotonic counter stamped as field3 (the "phase") into each NormalAttack BehaviorControlCenter ack. In the
		// capture it increments by 1 per player swing-ack; the client uses it to sequence the behaviour responses.
		int NormalAttackAckPhase { get; set; }
		List<uint> CreateEditorObservedRequestIds { get; }
		List<uint> CreateEditorReadyRequestIds { get; }
		string ConversationAnswerPath { get; set; }

		// The active NPC conversation's position in the dialogue TREE. The runtime serves the screen for the current
		// AnswerPath ("" = greeting, "0", "0>1", ...); each KnubotAnswer appends the picked option index and serves the
		// child screen. NpcName/TypeId identify the dialogue; NpcHandle is the live clicked handle the captured screen
		// packets are re-addressed to. Null AnswerPath = no active conversation. (Replaces the old stage/Mode/Field3
		// model - Mode and Field3 were session-volatile counters, not branch ids; memory protocol_conversation_routing.)
		string ConversationNpcName { get; set; }

		// Name chosen in the character-creation editor (from the "TheNameIs" script callback).
		// Stored separately from Character.Name so the create-editor flow (which gates on
		// Character.Name == null) is not disturbed mid-stream. The eventual enter-world/commit step
		// will promote this to the persisted character.
		string PendingCharacterName { get; set; }

		// The GameAgent sender instance the active conversation's packets use (retail varies it per conversation,
		// e.g. 0x0a). The close must be sent from this SAME instance or the client won't match it to the dialogue
		// and re-fires the greeting (#217). Captured from the served screens; 0 = none (fall back to 0x08).
		uint ConversationAgentInstance { get; set; }

		// #217: the NPC whose conversation was just closed. The client keeps the NPC targeted and reflexively
		// re-fires the greeting TalkToNpc the instant we close (with NO new SelectDeselect) - which would re-open
		// the dialogue. Suppress greetings for this handle until the player re-selects the NPC (a SelectDeselect
		// clears it = fresh intent to talk). 0 = nothing suppressed.
		uint ConversationEndedNpcHandle { get; set; }
		uint ConversationNpcHandle { get; set; }
		uint ConversationTypeId { get; set; }
		uint CreateEditorTempInstanceCount { get; set; }

		// Incrementing sequence number stamped into each 0xBADF5A4B morph-ack the creator sends back while
		// the editor streams its SetMorphValue callbacks. Retail starts this at 2.
		uint MorphAckCounter { get; set; }

		// Debounce for the post-morph trio: after Finish the editor streams its SetMorphValue callbacks (only
		// for morphs that were changed, so the exact set/last one varies). MorphActivitySequence is bumped on
		// Finish and every morph; the trio is sent once activity goes quiet. PostMorphBurstSent guards it.
		uint MorphActivitySequence { get; set; }

		// The wire handle of the entity the player currently has selected (last LookAtIIR/SelectDeselect), or 0 when
		// nothing is selected (deselect sends a zeroed handle). Session-volatile, like every entity handle. Used by
		// the .kill GM command to target whatever the player is looking at (the player's own ClientInstance when they
		// select themselves).
		uint SelectedTargetHandle { get; set; }
		uint? LastGameServer2020RequestId { get; set; }
	}

	public sealed class NetworkClientSession : INetworkClientSession
	{
		public Account Account { get; set; }
		public Character Character { get; set; }

		public bool AwaitingEnterWorld { get; set; }
		public bool HasFlushedCreateEditorPostReady { get; set; }
		public bool HasFlushedCreateWorldEntryBurst { get; set; }
		public bool HasPendingCreateEditorReadyFollowUp { get; set; }
		public bool HasPendingPost205FFollowUp { get; set; }
		public bool IsCreateEditorConnection { get; set; }
		public bool IsCreateWorldSession { get; set; }
		public bool IsTeleportingOut { get; set; }
		public bool PostMorphBurstSent { get; set; }
		public bool WorldEntered { get; set; }
		public byte[] ClientConversation { get; set; }

		public Dictionary<uint, string> SpawnedNpcNamesByHandle { get; } = new Dictionary<uint, string>();
		public Dictionary<uint, uint> SpawnedNpcMaxHealthByHandle { get; } = new Dictionary<uint, uint>();

		public int CreateWorldChunkIndex { get; set; }
		public int NormalAttackAckPhase { get; set; }
		public List<uint> CreateEditorObservedRequestIds { get; } = [];
		public List<uint> CreateEditorReadyRequestIds { get; } = [];
		public string ConversationAnswerPath { get; set; }
		public string ConversationNpcName { get; set; }
		public string PendingCharacterName { get; set; }
		public uint ConversationAgentInstance { get; set; }
		public uint ConversationEndedNpcHandle { get; set; }
		public uint ConversationNpcHandle { get; set; }
		public uint ConversationTypeId { get; set; }
		public uint CreateEditorTempInstanceCount { get; set; }
		public uint MorphAckCounter { get; set; }
		public uint MorphActivitySequence { get; set; }
		public uint SelectedTargetHandle { get; set; }

		public uint? LastGameServer2020RequestId { get; set; }
	}
}
