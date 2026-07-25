using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.Core.Network
{
	public interface INetworkClientSession
	{
		uint ConversationTypeId { get; set; }

		Account Account { get; set; }

		Character Character { get; set; }

		// Live NPC entity handle -> name, captured from the world-burst spawns (FA015807) actually sent this session.
		// NPC dynel handles are session-volatile, so this is how a clicked handle is resolved to the stable NPC name
		// that conversation routing keys on. Populated when the burst is replayed.
		Dictionary<uint, string> SpawnedNpcNamesByHandle { get; }

		// Live NPC entity handle -> NpcTemplateId, captured alongside SpawnedNpcNamesByHandle. The template is the stable
		// per-type key for shop/conversation lookup (names collide: 25 templates share "Halen van Dummy"), so a clicked
		// handle resolves to the right shop/dialogue by template, not the shared name.
		Dictionary<uint, uint> SpawnedNpcTemplatesByHandle { get; }

		// The AccountClaim ids in the order last sent in the claims list (CoreClaimIIR sub 0x0001), so a redeem
		// (sub 0x0003) resolves its 1-based index against a STABLE snapshot - "Claim All" fires one redeem per row
		// using the original indices, and deleting rows as they redeem would otherwise shift every later index.
		IReadOnlyList<uint>? ClaimSlots { get; set; }
		List<uint> CreateEditorObservedRequestIds { get; }

		List<uint> WeaveComponents { get; }
		// Where the client PARKED the seated character. Null until the first move after sitting reports it - a bench has
		// several seats (3 on the Old Tarantia ones) so the sit snaps the character up to ~1.3 units from where the
		// player clicked, and measuring "has he left the seat" from the click point is wrong. Null also means
		// "not anchored yet", which is why no separate flag is needed.
		Vector3? SeatedPosition { get; set; }

		// The role this GameServer connection plays in the world-entry handshake, decided ONCE at
		// GameAgent.Authenticate2 and the single source of truth for routing every later handshake signal.
		// See docs/subsystems/world-entry-flow.md. IsCreateEditorConnection / IsCreateWorldSession are derived
		// views of it (kept for the call sites that only care about one role).
		WorldEntryMode EntryMode { get; set; }

		// Set after CharCreationFinished + the world-prep burst: the creator is now waiting for the client's
		// 0x206E enter-world signal, which we answer with the 0x2002 world-address handoff.
		bool AwaitingEnterWorld { get; set; }
		bool EnergyLoopActive { get; set; } // guards against spawning more than one energy-mirror loop

		bool HasPendingPost205FFollowUp { get; set; }

		// True on the character-creator (editor) GameServer connection. A derived view of EntryMode (role at
		// auth); NOT the same as IsUnnamedCreatePhase (name-now) - see that member and WorldEntryMode.
		bool IsCreateEditorConnection { get; }

		// True on the post-creation WORLD GameServer connection (the second connection, opened after the
		// 0x2002 enter-world handoff). Gates the intro/cutscene bursts so they only fire on the first world
		// entry, not on normal existing-character logins. A derived view of EntryMode.
		bool IsCreateWorldSession { get; }

		// Spellweaving trance state (docs/subsystems/combos.md): true between the activate and deactivate PerformSpecialAction
		// (0x205A) toggles; WeaveComponents accumulates the componentIndex of each CombatReactiveIIR add while the
		// trance is open, and is fired + cleared on deactivate.
		bool IsSpellweaving { get; set; }
		bool IsSprinting { get; set; }

		// True on the OLD connection while a teleport/changemap handoff is in flight: DoTeleport has already
		// persisted the authoritative destination position, so this connection's disconnect must NOT save its
		// stale pre-teleport live position back over it (it would clobber the bind/destination). See #28.
		bool IsTeleportingOut { get; set; }

		// True ONLY while the character-creator connection is still pre-name (Character.Name == null). This is
		// the TRANSIENT "the editor UI is active" question, distinct from IsCreateEditorConnection (the role at
		// auth): the create connection names the character mid-connection (CommitCharacterName), after which
		// this flips to false while IsCreateEditorConnection stays true. Handshake steps that must behave
		// differently before vs after the name is committed (the instance-echo route, the 0x205F create branch,
		// the create-vs-login SendReadyForPlayScreen body) key on THIS, not the role flag.
		bool IsUnnamedCreatePhase { get; }
		bool PostMorphBurstSent { get; set; }
		bool SeatedHoldConfirmed { get; set; }


		// True once this connection has completed world entry (the burst-less world entry, the only entry path).
		// Gates the in-world behaviours that must not fire on a connection that hasn't entered the world: the
		// 0x2020 -> 0x2008 SpawnCheck responder, the NPC conversation system, and the dialogue-close fallback.
		// (The create-world flow uses IsCreateWorldSession alongside this.)
		bool WorldEntered { get; set; }

		// The in-flight cast's begin ARGS block + its Mode, held only while a timed cast is pending (#347). The client
		// cancels the cast on a mid-cast move (its own cancel BCC); the server answers with the state=0x0007 abort that
		// drops the cast bar - and that abort echoes the begin's args (with the phase=Mode field spliced in). Only the
		// args are begin-specific; the rest of the abort comes from the cancel packet. Cleared on resolve/cancel.
		byte[] PendingCastArgs { get; set; }

		// Index of the next post-creation world cutscene chunk to send. Advanced once per client script
		// trigger (ClientLoadingDone, StartScript, Move, IntroDone) so each chunk is sent in order.
		int CreateWorldChunkIndex { get; set; }

		// Monotonic counter stamped as field3 (the "phase") into each NormalAttack BehaviorControlCenter ack. In the
		// capture it increments by 1 per player swing-ack; the client uses it to sequence the behaviour responses.
		int NormalAttackAckPhase { get; set; }

		// The Spellweaving(1) buff STACK level: each time the component picker reappears a fresh stack is applied at
		// an incremented buff id (37e5a8 -> 37e5a9 -> 37e5aa), overlapping the previous (retail stacks to ~x2).
		int SpellweaveBuffStack { get; set; }

		string ConversationAnswerPath { get; set; }

		// The active NPC conversation's position in the dialogue TREE. The runtime serves the screen for the current
		// AnswerPath ("" = greeting, "0", "0>1", ...); each KnubotAnswer appends the picked option index and serves the
		// child screen. NpcName/TypeId identify the dialogue; NpcHandle is the live clicked handle the captured screen
		// packets are re-addressed to. Null AnswerPath = no active conversation. (Replaces the old stage/Mode/Field3
		// model - Mode and Field3 were session-volatile counters, not branch ids; memory protocol_conversation_routing.)
		string ConversationNpcName { get; set; }

		// Invalidation token for the in-flight spell cast (#236 interrupt handling). Bumped on every cast BEGIN (the
		// deferred effect resolve captures the value it was started with) and bumped AGAIN whenever the cast is
		// cancelled - the client's cancel BCC (CastSpell with state=2) or the player moving mid-cast - so a resolve
		// that fires after a cancel sees a mismatched token and aborts. This is what makes "move cancels the cast".
		uint ActiveCastToken { get; set; }

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
		uint PendingCastMode { get; set; }

		// The bench the player is sitting on, or null when standing. Nullable rather than a 0 sentinel: 0 is a legal
		// handle value and "not seated" is a genuinely absent state.
		uint? SeatedOnHandle { get; set; }

		// The wire handle of the entity the player currently has selected (last LookAtIIR/SelectDeselect), or 0 when
		// nothing is selected (deselect sends a zeroed handle). Session-volatile, like every entity handle. Used by
		// the .kill GM command to target whatever the player is looking at (the player's own ClientInstance when they
		// select themselves).
		uint SelectedTargetHandle { get; set; }

		// The base id of the Shield of the Risen protection buff currently shown to this client (#221), or null if
		// none. Tracked so a playfield change removes the prior shield's icon (retail keeps exactly one active at a
		// time) before applying the new one. Session-only (client buff state resets on relog).
		uint? ActiveShieldSpellBase { get; set; }

		// The token embedded in a pending feat-untrain confirm dialog (DispatchFeatUntrainMessageBox); the client
		// echoes it in FeatUntrainAnswer, so the reset only fires when the answer matches an outstanding request.
		uint? PendingFeatUntrainToken { get; set; }
	}

	public sealed class NetworkClientSession : INetworkClientSession
	{
		public uint ConversationTypeId { get; set; }
		public Account Account { get; set; } = null!;
		public Character Character { get; set; } = null!;

		public Dictionary<uint, string> SpawnedNpcNamesByHandle { get; } = [];
		public Dictionary<uint, uint> SpawnedNpcTemplatesByHandle { get; } = [];

		// The claim ids in the order last sent to this client (see the interface), so "Claim All"'s per-row redeems
		// resolve their original 1-based index even as rows are deleted mid-batch.
		public IReadOnlyList<uint>? ClaimSlots { get; set; }
		public List<uint> CreateEditorObservedRequestIds { get; } = [];
		public List<uint> WeaveComponents { get; } = [];

		// The player's position at the moment they sat: standup only fires once they've moved away from it (the client
		// keeps streaming position syncs while seated - and a small seat-snap - which must NOT be read as leaving).
		public Vector3? SeatedPosition { get; set; }
		public WorldEntryMode EntryMode { get; set; } = WorldEntryMode.Login;

		public bool AwaitingEnterWorld { get; set; }
		public bool EnergyLoopActive { get; set; }
		public bool HasPendingPost205FFollowUp { get; set; }
		public bool IsCreateEditorConnection => EntryMode == WorldEntryMode.CreateEditor;
		public bool IsCreateWorldSession => EntryMode == WorldEntryMode.CreateWorld;
		public bool IsSpellweaving { get; set; }
		public bool IsSprinting { get; set; }
		public bool IsTeleportingOut { get; set; }
		public bool IsUnnamedCreatePhase => Character?.Name == null;
		public bool PostMorphBurstSent { get; set; }

		// Whether we've already answered the client's SECOND UseObject with the object-action hold-assert (#355). The
		// bench sit is a TWO-phase handshake: request #1 gets the hold-echo + sit animation, request #2 (the client's
		// double-send) gets the hold-assert that COMMITS the pose - without answering request #2 the sit-down plays
		// then reverts after the ~2.6s blend. This gates the assert to fire exactly once per sit.
		public bool SeatedHoldConfirmed { get; set; }
		public bool WorldEntered { get; set; }
		public byte[] PendingCastArgs { get; set; } = null!;

		public int CreateWorldChunkIndex { get; set; }
		public int NormalAttackAckPhase { get; set; }
		public int SpellweaveBuffStack { get; set; }
		public string ConversationAnswerPath { get; set; } = null!;
		public string ConversationNpcName { get; set; } = null!;
		public uint ActiveCastToken { get; set; }
		public uint ConversationAgentInstance { get; set; }
		public uint ConversationEndedNpcHandle { get; set; }
		public uint ConversationNpcHandle { get; set; }
		public uint Energy { get; set; }
		public uint EnergyMax { get; set; }
		public uint MorphAckCounter { get; set; }
		public uint MorphActivitySequence { get; set; }
		public uint PendingCastMode { get; set; }

		// The world-object (bench) handle the player is currently seated on (0 = standing). Set when the player
		// interacts with a bench (the sit animation); cleared when they move off it (standup). See WorldBroadcaster
		// TrySitOnObject / StandUpIfMoved.
		public uint? SeatedOnHandle { get; set; }
		public uint SelectedTargetHandle { get; set; }

		public uint? ActiveShieldSpellBase { get; set; }

		// The outstanding feat-untrain confirm token (see the interface); null when no untrain dialog is pending.
		public uint? PendingFeatUntrainToken { get; set; }
	}
}
