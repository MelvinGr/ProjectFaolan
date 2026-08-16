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
		// The active dialogue's NPC type id, or null when no conversation is open.

		// The map id of the ~Playfield chat channel this client is currently subscribed to, or 0 for none. Held per
		// SESSION rather than read off Character.MapId, because those are two different facts: the character's map is
		// where they ARE, this is what the chat tier last told them to listen to. Comparing the two is what makes
		// RefreshPlayfieldChannel idempotent and tells it which channel to drop (#562).
		uint PlayfieldChannelId { get; set; }

		// The guild id of the ~Guild chat channel this client is currently subscribed to, or 0 for none. Same reason
		// as PlayfieldChannelId above: the character's guild is what they ARE in, this is what the chat tier last told
		// them to listen to, and comparing the two is what makes RefreshGuildChannel idempotent (#663).
		uint GuildChannelId { get; set; }

		// The team id of the ~Team chat channel this client is currently subscribed to, or 0 for none - the same
		// per-session slot as PlayfieldChannelId/GuildChannelId, for the third channel that follows a membership (#668).
		uint TeamChannelId { get; set; }

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

		List<uint> WeaveComponents { get; }
		// Where the client PARKED the seated character. Null until the first move after sitting reports it - a bench has
		// several seats (3 on the Old Tarantia ones) so the sit snaps the character up to ~1.3 units from where the
		// player clicked, and measuring "has he left the seat" from the click point is wrong. Null also means
		// "not anchored yet", which is why no separate flag is needed.
		Vector3? SeatedPosition { get; set; }

		// The role this GameServer connection plays in the world-entry handshake, decided ONCE at
		// GameAgent.Authenticate2 and the single source of truth for routing every later handshake signal.
		// See docs/subsystems/world-entry-flow.md.
		//
		// ROUTING SWITCHES ON THIS, EXHAUSTIVELY (#432). A handshake signal that means different things per role -
		// Authenticate2's world entry, CharInPlayIIR, the 0x205F arming, the post-0x205F flush - covers all four modes
		// with a default that says so out loud. It used to be if/else chains over the derived booleans below, whose
		// final `else` was the login path, so any role nobody had thought about silently became a login: that is how a
		// create-world connection was told it was a "changemap/teleport reconnect" in the middle of character creation,
		// three times in two weeks.
		//
		// There are no derived IsCreateEditorConnection / IsCreateWorldSession booleans any more: two ways to ask one
		// question is what let the routing drift in the first place, and every call site - routing or content - now
		// names the mode it means. IsUnnamedCreatePhase is NOT one of these: it asks a different question (name-now,
		// which flips mid-connection) and stays.
		WorldEntryMode EntryMode { get; set; }

		// The enter-world handoff is a THREE-state machine per connection - Idle -> Armed -> Sent - and `Sent` is
		// TERMINAL (#706). A second 0x2002 re-redirects a client that is already reconnecting, so the whole point is
		// that one connection hands off at most once.
		//
		// ⚠️ It was two states, and that was only half the property. The claim below was made an atomic compare-and-swap
		// to stop two armed tasks both sending - but arming itself wrote `true` unconditionally, so
		// arm -> claim -> send -> arm again -> claim -> send again sailed straight through it. That is what kept
		// `EnterWorldHandoffTriggerTests.TheHandoffIsSingleShot` intermittently failing after the "fix".
		//
		// Terminal is safe because a session is PER CONNECTION and the world entry uses a NEW one: the client reconnects
		// in answer to the handoff (GameServerListener.Inbound - "this is the SECOND GameServer connection"). So there is
		// no legitimate second handoff on a given session, and a second ARM means the creation-finished path ran twice -
		// a retried or duplicated message, exactly the case that must not produce a second redirect.

		// True while this connection is armed and has not yet sent. Read-only: arming and clearing are the explicit
		// methods below, so no caller can re-open a terminal state by assigning to a property.
		bool AwaitingEnterWorld { get; }

		// Arm the handoff: true for the FIRST caller on this connection, false once it is armed or already sent. A
		// refusal is a duplicate creation-finish and the caller says so out loud rather than scheduling a second send.
		bool TryArmEnterWorldHandoff();

		// Atomically claim the armed handoff: true for exactly ONE caller, false for every other, and moves the state
		// to the terminal `Sent` in the same step. Several tasks can be in flight (the arm schedules one each), so
		// "read the flag then clear it" is not enough - two can both pass the read before either writes.
		bool TryClaimEnterWorldHandoff();

		// Put the connection back to Idle. The world-entry path calls it while resetting the rest of the per-connection
		// handshake state; on the world connection it is a no-op (that session never armed), and it is what a genuine
		// re-entry of the create flow would need if one ever exists.
		void ResetEnterWorldHandoff();
		bool EnergyLoopActive { get; set; } // guards against spawning more than one energy-mirror loop

		bool HasPendingPost205FFollowUp { get; set; }

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
		// the TRANSIENT "the editor UI is active" question, distinct from EntryMode == CreateEditor (the role at
		// auth): the create connection names the character mid-connection (CommitCharacterName), after which
		// this flips to false while the MODE stays CreateEditor. Handshake steps that must behave
		// differently before vs after the name is committed (the 0x205F create branch, the create-vs-login
		// SendReadyForPlayScreen body) key on THIS, not the role flag.
		bool IsUnnamedCreatePhase { get; }
		bool PostMorphBurstSent { get; set; }
		bool SeatedHoldConfirmed { get; set; }

		// #780: the achievement VISTA the player is currently watching, or null. Set when a vista object is used and
		// cleared when the CLIENT reports it has left the cinematic - the whole scene is bracketed by the client's own
		// CinematicTransition pair, so this is what the leave half resolves against.
		ActiveVistaScene? ActiveVista { get; set; }


		// True once this connection has completed world entry (the burst-less world entry, the only entry path).
		// Gates the in-world behaviours that must not fire on a connection that hasn't entered the world: the
		// 0x2020 -> 0x2008 SpawnCheck responder, the NPC conversation system, and the dialogue-close fallback.
		// (The create-world flow tests EntryMode == CreateWorld alongside this.)
		//
		// ⚠️ It is set EARLY in the entry - before the entry packets are built, because the client starts polling
		// (0x2020) the spawns in the burst while the burst is still flushing, and the responder above is gated on
		// this. So it does NOT mean "the client has the world"; for that, see WorldEntryDelivered.
		bool WorldEntered { get; set; }

		// True once the world-entry packets have actually been FLUSHED to this client - MapChange first, then the
		// world objects / player spawn / snapshot / NPC burst. Distinct from WorldEntered, which is set several
		// seconds earlier (see above).
		//
		// It gates the world tick's interest sweep (#778), which is the one sender that reaches a client on nobody's
		// schedule but its own. Streaming an NPC before the MapChange lands hands the client a spawn for a playfield
		// it has not loaded, stamped with the zone's minted fallback token rather than the map's - the client drops
		// every one of them, and because the sweep SEEDS them into the player's Known set the entry burst then finds
		// nothing in range and never corrects it. That is what made every NPC in the entry radius permanently
		// invisible while remaining fully alive server-side (damage landed, AoE killed them).
		bool WorldEntryDelivered { get; set; }

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

		// The melee combo the player is part-way through (#139), or null between attempts. The CLIENT names the
		// combo and the step on every swing (CommandMessage.combo_id / combo_attack_num), so this is not a
		// reconstruction of the key sequence - it is the running attempt's bookkeeping: which combo the next swing
		// must continue, the step it must carry, and the animation time accumulated so far (the sum of the steps'
		// `activation_time`, which is what the stamina cost scales by). See ComboService.
		uint? ActiveComboId { get; set; }

		// The step the NEXT swing of the running combo must carry (so 0 means "no step taken yet").
		int ActiveComboStep { get; set; }

		// The running attempt's accumulated `activation_time`, in centiseconds.
		uint ActiveComboCentiseconds { get; set; }

		// The Spellweaving(1) buff STACK level: each time the component picker reappears a fresh stack is applied at
		// an incremented buff id (37e5a8 -> 37e5a9 -> 37e5aa), overlapping the previous (retail stacks to ~x2).
		int SpellweaveBuffStack { get; set; }

		// The SCREEN the player is currently looking at - the runtime's position in the NPC's dialogue GRAPH (#642).
		// Null = no active conversation, which is the one authoritative "is a dialogue open" test.
		//
		// It replaced an answer-index PATH string. The path had to be extended and re-looked-up on every pick, and it
		// could not name a screen a loop returns to; a node id names one screen, and the branch out of it is a row.
		uint? ConversationNodeId { get; set; }

		// The option buttons on the player's display, in the order the client drew them - the LDB string id of each.
		// A KnubotAnswer carries an INDEX into this list, and the index is only meaningful against the screen actually
		// on screen (the same screen offers different lists in different quest states), so it is resolved here and
		// never persisted. Empty when the served screen offered no options.
		List<uint> ConversationOptions { get; set; }

		// The NPC of the active conversation, by NAME - the stable identity the runtime routes on (the handle is
		// session-volatile and is only what the screen packets are re-addressed to).
		string ConversationNpcName { get; set; }

		// Invalidation token for the in-flight spell cast (#236 interrupt handling). Bumped on every cast BEGIN (the
		// deferred effect resolve captures the value it was started with) and bumped AGAIN whenever the cast is
		// cancelled - the client's cancel BCC (CastSpell with state=2) or the player moving mid-cast - so a resolve
		// that fires after a cancel sees a mismatched token and aborts. This is what makes "move cancels the cast".
		uint ActiveCastToken { get; set; }

		// #271: when each recast group comes off cooldown, as an Environment.TickCount64 stamp. Keyed by the group
		// the client itself arms - the spell's GUI stacking line where it has one, so every rank shares the timer,
		// and the spell id otherwise. Session state by nature: a relog re-arms nothing, which is what retail does.
		Dictionary<uint, long> SpellReadyAt { get; }

		// #623: the same thing for melee COMBOS, keyed by the combo id. Separate from SpellReadyAt because a combo's
		// reuse time is the catalog's own `Combo.Cooldown` (seconds) rather than the payload spell's recast, and the
		// two are not the same number - `Grim Corruption II` has a 0 s combo cooldown and its spell has a recast.
		// Session state by nature, exactly as SpellReadyAt: a relog re-arms nothing.
		Dictionary<uint, long> ComboReadyAt { get; }

		// The GameAgent sender instance the active conversation's packets use (retail varies it per conversation,
		// e.g. 0x0a). The close must be sent from this SAME instance or the client won't match it to the dialogue
		// and re-fires the greeting (#217). Captured from the served screens; 0 = none (fall back to 0x08).
		// The GameAgent instance the active dialogue's screens ride (#217), or null when there is none - callers fall
		// back to 0x08.
		uint? ConversationAgentInstance { get; set; }

		// #217: the NPC whose conversation was just closed. The client keeps the NPC targeted and reflexively
		// re-fires the greeting TalkToNpc the instant we close (with NO new SelectDeselect) - which would re-open
		// the dialogue. Suppress greetings for this handle until the player re-selects the NPC (a SelectDeselect
		// clears it = fresh intent to talk). 0 = nothing suppressed.
		// The NPC whose dialogue we just closed, so the client's reflexive post-close greeting is absorbed (#217).
		// Null once the player re-selects an NPC = genuine intent to talk again.
		uint? ConversationEndedNpcHandle { get; set; }
		// The NPC of the ACTIVE dialogue, or null when no conversation is open.
		uint? ConversationNpcHandle { get; set; }

		// The world OBJECT that opened the active dialogue - a door or gate whose use triggered it - or null when the
		// player opened it themselves. Kept because a script event fired at the END of the conversation may need the
		// place it was opened at: Casilda's `RunToTortage` sends her to the city gate the player just used, and by
		// then the door handler that knew which one is long gone (#839).
		uint? ConversationTriggerObjectId { get; set; }

		// Handles whose APPROACH-triggered conversation has already opened this session (#523) - the latch that stops
		// it re-firing on every step taken near the NPC. Per session, like the rest of the conversation state.
		HashSet<uint> ApproachedConversationNpcs { get; }

		// Every NPC TEMPLATE whose dialogue window this session has already opened, so the second and later greetings
		// get retail's terse line rather than the full welcome (#645 - ConversationEntry.RepeatGreeting).
		HashSet<uint> GreetedConversationNpcs { get; }

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
		// The entity the player last clicked (LookAt / SelectDeselect), or null when nothing is selected.
		uint? SelectedTargetHandle { get; set; }

		// The base id of the Shield of the Risen protection buff currently shown to this client (#221), or null if
		// none. Tracked so a playfield change removes the prior shield's icon (retail keeps exactly one active at a
		// time) before applying the new one. Session-only (client buff state resets on relog).
		uint? ActiveShieldSpellBase { get; set; }

		// The token embedded in a pending feat-untrain confirm dialog (DispatchFeatUntrainMessageBox); the client
		// echoes it in FeatUntrainAnswer, so the reset only fires when the answer matches an outstanding request.
		uint? PendingFeatUntrainToken { get; set; }

		// #637: the zone-line trip waiting on the player's answer to a travel confirmation box
		// (DispatchScryShowMessageBox), or null when no box is outstanding. The client echoes the box's
		// (ThreadCtxId, Cookie) pair back verbatim in ScryMessageBoxAnswer, so this is what a confirm resumes - which
		// is why retail needs no server-side script thread for it, and neither do we. Cleared on the answer, and
		// replaced (not queued) if a second box goes out: only the newest box can be the one being answered.
		PendingTravelConfirm? PendingTravelConfirm { get; set; }

		PendingAcgTeleport? PendingAcgTeleport { get; set; }
	}

	// A zone-line trip a client has been asked to confirm: the correlation pair the box carried, plus where the trip
	// goes. Position is null for a door whose arrival no capture states - the teleport then falls back to the
	// destination map's default spawn, exactly as it does without a box.
	public sealed record PendingTravelConfirm(uint ThreadCtxId, ulong Cookie, uint DestinationMapId, Vector3? ArrivalPosition);

	// The ACG instance bind box currently on the player's screen (#821). Held because `RequestACGTeleport` carries only
	// the destination proxy and no character or door - so without this, any client could name any map and be moved
	// there. The pending offer is what the request is checked AGAINST.
	//
	// ⚠️ There is no decline message: a player who says no sends nothing at all, so this is cleared on the next offer
	// and on world exit rather than by an answer.
	public sealed record PendingAcgTeleport(uint DestinationMapId, Vector3? ArrivalPosition);

	// The achievement vista scene a player is inside (#780): the catalog row plus the LIVE handle of the object they
	// used. The handle is what the camera action attaches to and what the lid records address, and it is per-zone-load
	// state, so it belongs on the session rather than on the catalog row.
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

		// The claim ids in the order last sent to this client (see the interface), so "Claim All"'s per-row redeems
		// resolve their original 1-based index even as rows are deleted mid-batch.
		public IReadOnlyList<uint>? ClaimSlots { get; set; }
		public List<uint> WeaveComponents { get; } = [];

		// The player's position at the moment they sat: standup only fires once they've moved away from it (the client
		// keeps streaming position syncs while seated - and a small seat-snap - which must NOT be read as leaving).
		public Vector3? SeatedPosition { get; set; }
		public WorldEntryMode EntryMode { get; set; } = WorldEntryMode.Login;

		// Idle -> Armed -> Sent, backed by an int so both transitions are real Interlocked compare-and-swaps. `Sent` is
		// terminal for this connection: see INetworkClientSession.AwaitingEnterWorld for why (#706).
		private const int HandoffIdle = 0;
		private const int HandoffArmed = 1;
		private const int HandoffSent = 2;

		private int _enterWorldHandoff = HandoffIdle;

		public bool AwaitingEnterWorld => Volatile.Read(ref _enterWorldHandoff) == HandoffArmed;

		public bool TryArmEnterWorldHandoff() => Interlocked.CompareExchange(ref _enterWorldHandoff, HandoffArmed, HandoffIdle) == HandoffIdle;

		public bool TryClaimEnterWorldHandoff() => Interlocked.CompareExchange(ref _enterWorldHandoff, HandoffSent, HandoffArmed) == HandoffArmed;

		public void ResetEnterWorldHandoff() => Volatile.Write(ref _enterWorldHandoff, HandoffIdle);

		public bool EnergyLoopActive { get; set; }
		public bool HasPendingPost205FFollowUp { get; set; }
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

		// The world-object (bench) handle the player is currently seated on (0 = standing). Set when the player
		// interacts with a bench (the sit animation); cleared when they move off it (standup). See WorldBroadcaster
		// TrySitOnObject / StandUpIfMoved.
		public uint? SeatedOnHandle { get; set; }
		public uint? SelectedTargetHandle { get; set; }

		public uint? ActiveShieldSpellBase { get; set; }

		// The outstanding feat-untrain confirm token (see the interface); null when no untrain dialog is pending.
		public uint? PendingFeatUntrainToken { get; set; }
		public PendingTravelConfirm? PendingTravelConfirm { get; set; }

		public PendingAcgTeleport? PendingAcgTeleport { get; set; }
	}
}
