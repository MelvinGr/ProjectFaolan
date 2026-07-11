using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.Core.Network
{
	public interface INetworkClientSession
	{
		uint ConversationTypeId { get; set; }
		uint? LastGameServer2020RequestId { get; set; }
		Account Account { get; set; }

		// The base id of the Shield of the Risen protection buff currently shown to this client (#221), or null if
		// none. Tracked so a playfield change removes the prior shield's icon (retail keeps exactly one active at a
		// time) before applying the new one. Session-only (client buff state resets on relog).
		uint? ActiveShieldSpellBase { get; set; }

		// Set after CharCreationFinished + the world-prep burst: the creator is now waiting for the client's
		// 0x206E enter-world signal, which we answer with the 0x2002 world-address handoff.
		bool AwaitingEnterWorld { get; set; }
		bool EnergyLoopActive { get; set; } // guards against spawning more than one energy-mirror loop

		bool HasPendingPost205FFollowUp { get; set; }

		// The role this GameServer connection plays in the world-entry handshake, decided ONCE at
		// GameAgent.Authenticate2 and the single source of truth for routing every later handshake signal.
		// See docs/subsystems/world-entry-flow.md. IsCreateEditorConnection / IsCreateWorldSession are derived
		// views of it (kept for the call sites that only care about one role).
		WorldEntryMode EntryMode { get; set; }

		// True on the character-creator (editor) GameServer connection. A derived view of EntryMode (role at
		// auth); NOT the same as IsUnnamedCreatePhase (name-now) - see that member and WorldEntryMode.
		bool IsCreateEditorConnection { get; }

		// True on the post-creation WORLD GameServer connection (the second connection, opened after the
		// 0x2002 enter-world handoff). Gates the intro/cutscene bursts so they only fire on the first world
		// entry, not on normal existing-character logins. A derived view of EntryMode.
		bool IsCreateWorldSession { get; }

		// True ONLY while the character-creator connection is still pre-name (Character.Name == null). This is
		// the TRANSIENT "the editor UI is active" question, distinct from IsCreateEditorConnection (the role at
		// auth): the create connection names the character mid-connection (CommitCharacterName), after which
		// this flips to false while IsCreateEditorConnection stays true. Handshake steps that must behave
		// differently before vs after the name is committed (the instance-echo route, the 0x205F create branch,
		// the create-vs-login ReadyForPlayScreen/AccessWindow bodies) key on THIS, not the role flag.
		bool IsUnnamedCreatePhase { get; }

		// Spellweaving trance state (docs/subsystems/combos.md): true between the activate and deactivate PerformSpecialAction
		// (0x205A) toggles; WeaveComponents accumulates the componentIndex of each CombatReactiveIIR add while the
		// trance is open, and is fired + cleared on deactivate.
		bool IsSpellweaving { get; set; }
		bool IsSprinting { get; set; }

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

		// Index of the next post-creation world cutscene chunk to send. Advanced once per client script
		// trigger (ClientLoadingDone, StartScript, Move, IntroDone) so each chunk is sent in order.
		int CreateWorldChunkIndex { get; set; }

		// Monotonic counter stamped as field3 (the "phase") into each NormalAttack BehaviorControlCenter ack. In the
		// capture it increments by 1 per player swing-ack; the client uses it to sequence the behaviour responses.
		int NormalAttackAckPhase { get; set; }

		// The Spellweaving(1) buff STACK level: each time the component picker reappears a fresh stack is applied at
		// an incremented buff id (37e5a8 -> 37e5a9 -> 37e5aa), overlapping the previous (retail stacks to ~x2).
		int SpellweaveBuffStack { get; set; }
		List<uint> CreateEditorObservedRequestIds { get; }

		List<uint> WeaveComponents { get; }

		string ConversationAnswerPath { get; set; }

		// The active NPC conversation's position in the dialogue TREE. The runtime serves the screen for the current
		// AnswerPath ("" = greeting, "0", "0>1", ...); each KnubotAnswer appends the picked option index and serves the
		// child screen. NpcName/TypeId identify the dialogue; NpcHandle is the live clicked handle the captured screen
		// packets are re-addressed to. Null AnswerPath = no active conversation. (Replaces the old stage/Mode/Field3
		// model - Mode and Field3 were session-volatile counters, not branch ids; memory protocol_conversation_routing.)
		string ConversationNpcName { get; set; }

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

		// The universal ENERGY pool (the yellow sprint bar, DispatchUpdateSprintbar 0x200E). Tracked server-side so
		// spellweaving drains from the CURRENT value (not always full) and the server can apply Exhausted at 0. The
		// client owns the visual during sprint; the server mirrors the drain/regen silently. EnergyMax 0 = uninitialised.
		uint Energy { get; set; }
		uint EnergyMax { get; set; }

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
	}

	public sealed class NetworkClientSession : INetworkClientSession
	{
		public uint ConversationTypeId { get; set; }

		public uint? LastGameServer2020RequestId { get; set; }
		public uint? ActiveShieldSpellBase { get; set; }
		public Account Account { get; set; }

		public bool AwaitingEnterWorld { get; set; }
		public bool EnergyLoopActive { get; set; }
		public bool HasPendingPost205FFollowUp { get; set; }
		public WorldEntryMode EntryMode { get; set; } = WorldEntryMode.Login;
		public bool IsCreateEditorConnection => EntryMode == WorldEntryMode.CreateEditor;
		public bool IsCreateWorldSession => EntryMode == WorldEntryMode.CreateWorld;
		public bool IsUnnamedCreatePhase => Character?.Name == null;
		public bool IsSpellweaving { get; set; }
		public bool IsSprinting { get; set; }
		public bool IsTeleportingOut { get; set; }
		public bool PostMorphBurstSent { get; set; }
		public bool WorldEntered { get; set; }
		public byte[] ClientConversation { get; set; }
		public Character Character { get; set; }

		public Dictionary<uint, string> SpawnedNpcNamesByHandle { get; } = new();

		public int CreateWorldChunkIndex { get; set; }
		public int NormalAttackAckPhase { get; set; }
		public int SpellweaveBuffStack { get; set; }
		public List<uint> CreateEditorObservedRequestIds { get; } = [];
		public List<uint> WeaveComponents { get; } = [];
		public string ConversationAnswerPath { get; set; }
		public string ConversationNpcName { get; set; }
		public uint ConversationAgentInstance { get; set; }
		public uint ConversationEndedNpcHandle { get; set; }
		public uint ConversationNpcHandle { get; set; }
		public uint Energy { get; set; }
		public uint EnergyMax { get; set; }
		public uint MorphAckCounter { get; set; }
		public uint MorphActivitySequence { get; set; }
		public uint SelectedTargetHandle { get; set; }
	}
}
