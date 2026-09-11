using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;

namespace Faolan.Core.Network
{
	public interface INetworkClientSession
	{
		// The map id of the ~Playfield chat channel this client is currently subscribed to, or 0 for none. Held per
		// session rather than read off Character.MapId, because those are two different facts: the character's map is
		// where they are, this is what the chat tier last told them to listen to. Comparing the two is what makes
		// RefreshPlayfieldChannel idempotent and tells it which channel to drop.
		uint PlayfieldChannelId { get; set; }

		// The guild id of the ~Guild chat channel this client is currently subscribed to, or 0 for none. Same reason as
		// PlayfieldChannelId above: the character's guild is what they are in, this is what the chat tier last told
		// them to listen to, and comparing the two is what makes RefreshGuildChannel idempotent.
		uint GuildChannelId { get; set; }

		// The team id of the ~Team chat channel this client is currently subscribed to, or 0 for none - the same
		// per-session slot as PlayfieldChannelId/GuildChannelId, for the third channel that follows a membership.
		uint TeamChannelId { get; set; }

		Account Account { get; set; }

		Character Character { get; set; }

		// Live NPC entity handle -> name, captured from the world-burst spawns sent this session. NPC dynel handles are
		// session-volatile, so this is how a clicked handle resolves to the stable NPC name conversation routing keys
		// on.
		Dictionary<uint, string> SpawnedNpcNamesByHandle { get; }

		// Live NPC entity handle -> NpcTemplateId, captured alongside SpawnedNpcNamesByHandle. The template is the stable
		// per-type key for shop/conversation lookup (names collide: 25 templates share "Halen van Dummy"), so a clicked
		// handle resolves to the right shop/dialogue by template, not the shared name.
		Dictionary<uint, uint> SpawnedNpcTemplatesByHandle { get; }

		// The AccountClaim ids in the order last sent in the claims list, so a redeem resolves its 1-based index against
		// a stable snapshot: "Claim All" fires one redeem per row using the original indices, and deleting rows as they
		// redeem would otherwise shift every later index.
		IReadOnlyList<uint>? ClaimSlots { get; set; }

		List<uint> WeaveComponents { get; }
		// Where the client parked the seated character. Null until the first move after sitting reports it: a bench has
		// several seats (3 on the Old Tarantia ones) so the sit snaps the character up to ~1.3 units from where the
		// player clicked, and measuring "has he left the seat" from the click point is wrong. Null also means "not
		// anchored yet", which is why no separate flag is needed.
		Vector3? SeatedPosition { get; set; }

		// The role this GameServer connection plays in the world-entry handshake, decided once at
		// GameAgent.Authenticate2 and the single source of truth for routing every later handshake signal.
		// See docs/subsystems/world-entry-flow.md.
		//
		// Routing must switch on this exhaustively. A handshake signal that means different things per role -
		// Authenticate2's world entry, CharInPlayIIR, the 0x205F arming, the post-0x205F flush - covers all four modes
		// with a default that says so out loud, so a role nobody has thought about cannot silently fall through to the
		// login path. Do not reintroduce derived booleans such as IsCreateEditorConnection: two ways to ask one
		// question is what lets routing drift, and every call site names the mode it means. IsUnnamedCreatePhase is not
		// one of those - it asks a different question (name-now, which flips mid-connection).
		WorldEntryMode EntryMode { get; set; }

		// The enter-world handoff is a three-state machine per connection - Idle -> Armed -> Sent - and `Sent` is
		// terminal. A second handoff would re-redirect a client that is already reconnecting, so one connection must
		// hand off at most once. Both the arm and the claim are compare-and-swaps: arming unconditionally would let
		// arm -> claim -> send -> arm -> claim -> send through.
		//
		// Terminal is safe because a session is per connection and the world entry uses a new one: the client reconnects
		// in answer to the handoff. There is therefore no legitimate second handoff on a given session, and a second arm
		// means the creation-finished path ran twice - a retried or duplicated message, exactly the case that must not
		// produce a second redirect.

		// True while this connection is armed and has not yet sent. Read-only: arming and clearing are the explicit
		// methods below, so no caller can re-open a terminal state by assigning to a property.
		bool AwaitingEnterWorld { get; }

		// Arms the handoff: true for the first caller on this connection, false once it is armed or already sent. A
		// refusal is a duplicate creation-finish, and the caller says so rather than scheduling a second send.
		bool TryArmEnterWorldHandoff();

		// Atomically claims the armed handoff: true for exactly one caller, false for every other, moving the state to
		// the terminal `Sent` in the same step. Several tasks can be in flight, so "read the flag then clear it" is not
		// enough - two can both pass the read before either writes.
		bool TryClaimEnterWorldHandoff();

		// Puts the connection back to Idle. The world-entry path calls it while resetting the rest of the per-connection
		// handshake state; on the world connection it is a no-op, since that session never armed.
		void ResetEnterWorldHandoff();
		bool EnergyLoopActive { get; set; } // guards against spawning more than one energy-mirror loop

		bool HasPendingPost205FFollowUp { get; set; }

		// Spellweaving trance state (docs/subsystems/combos.md): true between the activate and deactivate
		// PerformSpecialAction toggles. WeaveComponents accumulates the componentIndex of each CombatReactiveIIR add
		// while the trance is open, and is fired and cleared on deactivate.
		bool IsSpellweaving { get; set; }

		// The sprint pair. `IsSprinting` is the state the server granted - what drains the energy pool and what
		// `DispatchSetIsSprinting` tells the client. `SprintInputRequested` is the last `sprintInput` the client asked
		// for, kept apart from it because the two legitimately disagree: retail refuses a sprint on an empty pool, and
		// the client keeps holding the key while refused. Retail answers a change of the request rather than every
		// frame (2,959 changes -> 2,624 answers over the corpus), which is why the request is remembered.
		bool IsSprinting { get; set; }
		bool SprintInputRequested { get; set; }

		// The tag the client says it has selected (c2s SetSelectedTag). UI state, not persisted: no capture shows a
		// selection surviving a relog. Open work tracked in Gitea #748.
		uint SelectedTagId { get; set; }

		// True on the outgoing connection while a teleport/changemap handoff is in flight. DoTeleport has already
		// persisted the authoritative destination position, so this connection's disconnect must not save its stale
		// pre-teleport live position back over it.
		bool IsTeleportingOut { get; set; }

		// True only while the character-creator connection is still pre-name (Character.Name == null). This is the
		// transient "the editor UI is active" question, distinct from EntryMode == CreateEditor, the role decided at
		// auth: the create connection names the character mid-connection (CommitCharacterName), after which this flips
		// to false while the mode stays CreateEditor. Handshake steps that behave differently before and after the name
		// is committed (the 0x205F create branch, the create-vs-login SendReadyForPlayScreen body) key on this rather
		// than on the role.
		bool IsUnnamedCreatePhase { get; }
		bool PostMorphBurstSent { get; set; }
		bool SeatedHoldConfirmed { get; set; }

		// The achievement vista the player is currently watching, or null. Set when a vista object is used and cleared
		// when the client reports it has left the cinematic: the whole scene is bracketed by the client's own
		// CinematicTransition pair, so this is what the leave half resolves against.
		ActiveVistaScene? ActiveVista { get; set; }


		// True once this connection has completed world entry. Gates the in-world behaviours that must not fire on a
		// connection that has not entered the world: the spawn-poll responder, the NPC conversation system, and the
		// dialogue-close fallback. The create-world flow tests EntryMode == CreateWorld alongside this.
		//
		// It is set early in the entry, before the entry packets are built, because the client starts polling the
		// spawns in the burst while the burst is still flushing and the responder is gated on this. It therefore does
		// not mean "the client has the world"; for that, see WorldEntryDelivered.
		bool WorldEntered { get; set; }

		// True once the world-entry packets have actually been flushed to this client - MapChange first, then the world
		// objects, player spawn, snapshot and NPC burst. Distinct from WorldEntered, which is set several seconds
		// earlier.
		//
		// It gates the world tick's interest sweep, the one sender that reaches a client on nobody's schedule but its
		// own. Streaming an NPC before the MapChange lands hands the client a spawn for a playfield it has not loaded,
		// stamped with the zone's minted fallback token rather than the map's; the client drops every one of them, and
		// because the sweep seeds them into the player's Known set the entry burst then finds nothing in range and
		// never corrects it, leaving those NPCs permanently invisible while fully alive server-side.
		bool WorldEntryDelivered { get; set; }

		// The in-flight cast's begin args block and its Mode, held only while a timed cast is pending. The client cancels
		// the cast on a mid-cast move with its own cancel BCC; the server answers with the state=0x0007 abort that drops
		// the cast bar, and that abort echoes the begin's args with the phase=Mode field spliced in. Only the args are
		// begin-specific; the rest of the abort comes from the cancel packet. Cleared on resolve or cancel.
		byte[] PendingCastArgs { get; set; }

		// Index of the next post-creation world cutscene chunk to send. Advanced once per client script
		// trigger (ClientLoadingDone, StartScript, Move, IntroDone) so each chunk is sent in order.
		int CreateWorldChunkIndex { get; set; }

		// Monotonic counter stamped as field3 (the "phase") into each NormalAttack BehaviorControlCenter ack. In the
		// capture it increments by 1 per player swing-ack; the client uses it to sequence the behaviour responses.
		int NormalAttackAckPhase { get; set; }

		// The melee combo the player is part-way through, or null between attempts. The client names the combo and the
		// step on every swing (CommandMessage.combo_id / combo_attack_num), so this is not a reconstruction of the key
		// sequence but the running attempt's bookkeeping: which combo the next swing must continue, the step it must
		// carry, and the animation time accumulated so far (the sum of the steps' `activation_time`, which is what the
		// stamina cost scales by). See ComboService.
		uint? ActiveComboId { get; set; }

		// The step the NEXT swing of the running combo must carry (so 0 means "no step taken yet").
		int ActiveComboStep { get; set; }

		// The running attempt's accumulated `activation_time`, in centiseconds.
		uint ActiveComboCentiseconds { get; set; }

		// The Spellweaving(1) buff STACK level: each time the component picker reappears a fresh stack is applied at
		// an incremented buff id (37e5a8 -> 37e5a9 -> 37e5aa), overlapping the previous (retail stacks to ~x2).
		int SpellweaveBuffStack { get; set; }

		// The screen the player is currently looking at - the runtime's position in the NPC's dialogue graph. Null means
		// no active conversation, which is the one authoritative "is a dialogue open" test. A node id names exactly one
		// screen, including one a loop returns to, and the branch out of it is a row.
		uint? ConversationNodeId { get; set; }

		// The option buttons on the player's display, in the order the client drew them - the LDB string id of each. A
		// KnubotAnswer carries an index into this list, and the index is only meaningful against the screen actually on
		// display (the same screen offers different lists in different quest states), so it is resolved here and never
		// persisted. Empty when the served screen offered no options.
		List<uint> ConversationOptions { get; set; }

		// The NPC of the active conversation, by name - the stable identity the runtime routes on. The handle is
		// session-volatile and is only what the screen packets are re-addressed to.
		string ConversationNpcName { get; set; }

		// Invalidation token for the in-flight spell cast. Bumped on every cast begin - the deferred effect resolve
		// captures the value it was started with - and bumped again whenever the cast is cancelled, by the client's
		// cancel BCC (CastSpell with state=2) or by the player moving mid-cast, so a resolve that fires after a cancel
		// sees a mismatched token and aborts. This is what makes movement cancel a cast.
		uint ActiveCastToken { get; set; }

		// When each recast group comes off cooldown, as an Environment.TickCount64 stamp. Keyed by the group the client
		// itself arms - the spell's GUI stacking line where it has one, so every rank shares the timer, and the spell
		// id otherwise. Session state by nature: a relog re-arms nothing, which is what retail does.
		Dictionary<uint, long> SpellReadyAt { get; }

		// The same thing for melee combos, keyed by the combo id. Separate from SpellReadyAt because a combo's reuse time
		// is the catalog's own `Combo.Cooldown` (seconds) rather than the payload spell's recast, and the two are not
		// the same number - `Grim Corruption II` has a 0 s combo cooldown and its spell has a recast. Session state by
		// nature, exactly as SpellReadyAt: a relog re-arms nothing.
		Dictionary<uint, long> ComboReadyAt { get; }

		// The GameAgent sender instance the active conversation's packets use; retail varies it per conversation. The
		// close must be sent from this same instance, or the client will not match it to the dialogue and re-fires the
		// greeting. Null when there is none, in which case callers fall back to 0x08.
		uint? ConversationAgentInstance { get; set; }

		// The NPC whose conversation was just closed. The client keeps the NPC targeted and reflexively re-fires the
		// greeting TalkToNpc the instant the dialogue closes, with no new SelectDeselect, which would re-open it.
		// Greetings for this handle are suppressed until the player re-selects the NPC - a SelectDeselect clears it and
		// counts as fresh intent to talk. Null when nothing is suppressed.
		uint? ConversationEndedNpcHandle { get; set; }
		// The NPC of the ACTIVE dialogue, or null when no conversation is open.
		uint? ConversationNpcHandle { get; set; }

		// The world object that opened the active dialogue - a door or gate whose use triggered it - or null when the
		// player opened it themselves. Kept because a script event fired at the end of the conversation may need the
		// place it was opened at: Casilda's `RunToTortage` sends her to the city gate the player just used, by which
		// time the door handler that knew which one is gone.
		uint? ConversationTriggerObjectId { get; set; }

		// Handles whose approach-triggered conversation has already opened this session - the latch that stops it
		// re-firing on every step taken near the NPC. Per session, like the rest of the conversation state.
		HashSet<uint> ApproachedConversationNpcs { get; }

		// Every NPC template whose dialogue window this session has already opened, so the second and later greetings get
		// retail's terse line (ConversationEntry.RepeatGreeting) rather than the full welcome.
		HashSet<uint> GreetedConversationNpcs { get; }

		// The universal energy pool (the yellow sprint bar, DispatchUpdateSprintbar). Tracked server-side so spellweaving
		// drains from the current value rather than always from full, and so the server can apply Exhausted at 0. The
		// client owns the visual during sprint; the server mirrors the drain and regen silently. EnergyMax 0 means
		// uninitialised.
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

		// The entity the player last clicked (LookAtIIR / SelectDeselect), or null when nothing is selected.
		// Session-volatile, like every entity handle. Used by GM commands that target whatever the player is looking
		// at; it is the player's own ClientInstance when they select themselves.
		uint? SelectedTargetHandle { get; set; }

		bool LogAllIirs { get; set; }

		// The base id of the Shield of the Risen protection buff currently shown to this client, or null if none.
		// Tracked so a playfield change removes the prior shield's icon - retail keeps exactly one active at a time -
		// before applying the new one. Session-only, since client buff state resets on relog.
		uint? ActiveShieldSpellBase { get; set; }

		// The token embedded in a pending feat-untrain confirm dialog (DispatchFeatUntrainMessageBox); the client
		// echoes it in FeatUntrainAnswer, so the reset only fires when the answer matches an outstanding request.
		uint? PendingFeatUntrainToken { get; set; }

		// The zone-line trip waiting on the player's answer to a travel confirmation box (DispatchScryShowMessageBox), or
		// null when no box is outstanding. The client echoes the box's (ThreadCtxId, Cookie) pair back verbatim in
		// ScryMessageBoxAnswer, so this is what a confirm resumes, and no server-side script thread is needed. Cleared
		// on the answer, and replaced rather than queued if a second box goes out: only the newest box can be the one
		// being answered. Open work tracked in Gitea #637.
		PendingTravelConfirm? PendingTravelConfirm { get; set; }

		PendingAcgTeleport? PendingAcgTeleport { get; set; }
	}

	/// <summary>
	///     A zone-line trip a client has been asked to confirm: the correlation pair the box carried, plus where the
	///     trip goes.
	/// </summary>
	/// <remarks>
	///     <c>ArrivalPosition</c> is null for a door whose arrival no capture states; the teleport then falls back to
	///     the destination map's default spawn, exactly as it does without a box.
	/// </remarks>
	public sealed record PendingTravelConfirm(uint ThreadCtxId, ulong Cookie, uint DestinationMapId, Vector3? ArrivalPosition);

	/// <summary>
	///     The ACG instance bind box currently on the player's screen.
	/// </summary>
	/// <remarks>
	///     Held because <c>RequestACGTeleport</c> carries only the destination proxy and no character or door, so
	///     without it any client could name any map and be moved there: the pending offer is what the request is
	///     checked against.
	///     <para>
	///         There is no decline message - a player who says no sends nothing - so this is cleared on the next offer
	///         and on world exit rather than by an answer.
	///     </para>
	/// </remarks>
	public sealed record PendingAcgTeleport(uint DestinationMapId, Vector3? ArrivalPosition);

	/// <summary>
	///     The achievement vista scene a player is inside: the catalog row plus the live handle of the object they used.
	/// </summary>
	/// <remarks>
	///     The handle is what the camera action attaches to and what the lid records address, and it is per-zone-load
	///     state, so it belongs on the session rather than on the catalog row.
	/// </remarks>
	public sealed record ActiveVistaScene(AchievementVista Vista, uint ObjectHandle, uint ObjectClass);

	public sealed class NetworkClientSession : INetworkClientSession
	{
		public uint PlayfieldChannelId { get; set; }
		public uint GuildChannelId { get; set; }
		public uint TeamChannelId { get; set; }
		public Account Account { get; set; } = null!;
		public Character Character { get; set; } = null!;

		public Dictionary<uint, string> SpawnedNpcNamesByHandle { get; } = [];
		public Dictionary<uint, uint> SpawnedNpcTemplatesByHandle { get; } = [];

		// The claim ids in the order last sent to this client, so "Claim All"'s per-row redeems resolve their original
		// 1-based index even as rows are deleted mid-batch.
		public IReadOnlyList<uint>? ClaimSlots { get; set; }
		public List<uint> WeaveComponents { get; } = [];

		// The player's position at the moment they sat. Standup only fires once they have moved away from it: the client
		// keeps streaming position syncs while seated, plus a small seat-snap, and neither is leaving.
		public Vector3? SeatedPosition { get; set; }
		public WorldEntryMode EntryMode { get; set; } = WorldEntryMode.Login;

		// Idle -> Armed -> Sent, backed by an int so both transitions are real Interlocked compare-and-swaps. `Sent` is
		// terminal for this connection; see INetworkClientSession.AwaitingEnterWorld for why.
		private const int HandoffIdle = 0;
		private const int HandoffArmed = 1;
		private const int HandoffSent = 2;

		private int _enterWorldHandoff = HandoffIdle;

		public bool AwaitingEnterWorld => Volatile.Read(ref _enterWorldHandoff) == HandoffArmed;

		public bool TryArmEnterWorldHandoff() => Interlocked.CompareExchange(ref _enterWorldHandoff, HandoffArmed, HandoffIdle) == HandoffIdle;

		public bool TryClaimEnterWorldHandoff() => Interlocked.CompareExchange(ref _enterWorldHandoff, HandoffSent, HandoffArmed) == HandoffArmed;

		public void ResetEnterWorldHandoff() => Volatile.Write(ref _enterWorldHandoff, HandoffIdle);

		// Retail's per-character packet trace (its /char command "flags the character so all IIRs will be logged"): with
		// it on, the world listener logs every inbound IIR for this session at information level instead of debug, so
		// one character can be traced on a busy server without turning the log up for everyone.
		public bool LogAllIirs { get; set; }

		public bool EnergyLoopActive { get; set; }
		public bool HasPendingPost205FFollowUp { get; set; }
		public bool IsSpellweaving { get; set; }
		public bool IsSprinting { get; set; }
		public bool SprintInputRequested { get; set; }
		public uint SelectedTagId { get; set; }
		public bool IsTeleportingOut { get; set; }
		public bool IsUnnamedCreatePhase => Character?.Name == null;
		public bool PostMorphBurstSent { get; set; }

		// Whether the client's second UseObject has already been answered with the object-action hold-assert. The bench
		// sit is a two-phase handshake: the first request gets the hold-echo and sit animation, the second - the
		// client's double-send - gets the hold-assert that commits the pose. Without answering the second, the sit-down
		// plays and then reverts after the ~2.6 s blend. This gates the assert to fire exactly once per sit.
		public bool SeatedHoldConfirmed { get; set; }
		public ActiveVistaScene? ActiveVista { get; set; }
		public bool WorldEntered { get; set; }
		public bool WorldEntryDelivered { get; set; }
		public byte[] PendingCastArgs { get; set; } = null!;

		public int CreateWorldChunkIndex { get; set; }
		public int NormalAttackAckPhase { get; set; }
		public uint? ActiveComboId { get; set; }
		public int ActiveComboStep { get; set; }
		public uint ActiveComboCentiseconds { get; set; }
		public int SpellweaveBuffStack { get; set; }
		public uint? ConversationNodeId { get; set; }
		public List<uint> ConversationOptions { get; set; } = [];
		public string ConversationNpcName { get; set; } = null!;
		public uint ActiveCastToken { get; set; }

		public Dictionary<uint, long> SpellReadyAt { get; } = [];
		public Dictionary<uint, long> ComboReadyAt { get; } = [];
		public uint? ConversationAgentInstance { get; set; }
		public uint? ConversationEndedNpcHandle { get; set; }
		public uint? ConversationNpcHandle { get; set; }
		public uint? ConversationTriggerObjectId { get; set; }
		public HashSet<uint> ApproachedConversationNpcs { get; } = [];
		public HashSet<uint> GreetedConversationNpcs { get; } = [];
		public uint Energy { get; set; }
		public uint EnergyMax { get; set; }
		public uint MorphAckCounter { get; set; }
		public uint MorphActivitySequence { get; set; }
		public uint PendingCastMode { get; set; }

		// The world-object (bench) handle the player is currently seated on, or null when standing. Set when the player
		// interacts with a bench (the sit animation) and cleared when they move off it. See
		// WorldObjectService.TrySitOnObject / StandUpIfMoved.
		public uint? SeatedOnHandle { get; set; }
		public uint? SelectedTargetHandle { get; set; }

		public uint? ActiveShieldSpellBase { get; set; }

		// The outstanding feat-untrain confirm token; null when no untrain dialog is pending.
		public uint? PendingFeatUntrainToken { get; set; }
		public PendingTravelConfirm? PendingTravelConfirm { get; set; }

		public PendingAcgTeleport? PendingAcgTeleport { get; set; }
	}
}
