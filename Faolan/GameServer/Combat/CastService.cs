using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The CAST PIPELINE: one client `CastSpell` behaviour, from begin to effect. Ack the begin so the client renders
	// its cast bar, hold the cast time, then resolve - server-side effects, ground AoE, the buff/debuff that lands,
	// mana, the spell's instant areas, and the offensive-cast damage - or abort cleanly when the cast is interrupted.
	//
	// Casting was the one major combat flow with no owner: it lived as a 260-line `HandleBccCastSpell` inside
	// `GameServerListener.Inbound.cs`, i.e. inside the opcode ROUTING switch, orchestrating five collaborators from
	// there (CombatService, BuffService, SpellEffectService, SpellModifierService, SpellAreaCatalog + the world
	// simulation). The Inbound case is now the one-liner every other handler in that file already is.
	//
	// The two-phase shape is the whole reason this is not a straight-line method: a cast ACKS immediately and RESOLVES
	// later (after `castMs`), and everything between those two moments can be invalidated by a move or a cancel. The
	// invalidation token (`Session.ActiveCastToken`) is what ties them together - captured at begin, re-checked at
	// resolve.
	public sealed class CastService
	{
		// The BCC behaviourType that means the client is CANCELLING a cast rather than beginning one. State values are
		// capture-derived (actions-described-in-chat: begin=3 x2, cancel=2 x1; every cast in the
		// ice-strike/casilda/casting captures is begin=3).
		private const ushort CastStateCancel = 2;

		private readonly BuffService _buffs;
		private readonly CombatService _combat;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		// #772: the spell's INSTANT self-centred areas, and the #784 chain edges the buff engine walks.
		private readonly SpellAreaCatalog _spellAreas;

		// #439: a cast whose effect is something the SERVER does (a pet summon, an armed proc).
		private readonly SpellEffectService _spellEffects;

		// #710: the per-cast overrides a caster's trained feats and live buffs put on this spell.
		private readonly SpellModifierService _spellModifiers;

		public CastService(IDatabaseRepository database, WorldSimulation sim, CombatService combat, BuffService buffs,
			SpellEffectService spellEffects, SpellModifierService spellModifiers, SpellAreaCatalog spellAreas, ILogger logger)
		{
			_database = database;
			_sim = sim;
			_combat = combat;
			_buffs = buffs;
			_spellEffects = spellEffects;
			_spellModifiers = spellModifiers;
			_spellAreas = spellAreas;
			_logger = logger;
		}

		public async Task HandleCast(INetworkClient client, byte[] rawPayload, CommandMessage args, ushort behaviorType, uint target, byte[] actionTrailer)
		{
			// The BCC behaviorType tells begin from cancel: 3 = begin, 2 = CANCEL. The client sends
			// the cancel when the cast is interrupted (moving mid-cast - "move cancels the cast", #346/#347),
			// so honor it by invalidating any in-flight resolve (bump the token so the deferred Resolve below
			// aborts) and acking the cancel.
			if (behaviorType == CastStateCancel)
			{
				await Cancel(client, rawPayload, args);
				return;
			}

			// The real spell id rides the action TRAILER (`target`, the uint32 read after the protobuf
			// args), NOT the args message. The old model called field 10 a "SpellTarget" whose field 1 was
			// a spell_id, so casts logged as "spell 50000" and ApplySpell carried a non-existent id (no
			// effect shown) - 50000 is 0xC350, an ENTITY CLASS. Field 10 is a ProtoID32 {type, instance}
			// (CommandMessage.target); the fallback below reads .Type only to preserve the old behaviour.
			// Decoded from the ice-strike capture (c2s 0x642CD3D6 cast): trailer = [u32 from=1][u32 spellId];
			// 0x125D9B / 0x42CA77 are real learned-spell ids. args.Target.Instance is the target. (#236)
			var spellId = target != 0
				? target
				: args.Target?.Type ?? args.BccActionId;
			var casterHandle = client.Session.Character.Id;
			var targetHandle = args.Target?.Instance ?? 0;

			// A GROUND-TARGETED AoE (e.g. Deathly Blizzard) is aimed at a reticle POSITION carried in the
			// trailer instead of a locked entity: it has NO SpellTarget and the trailer's kind byte is 0.
			// CastGroundTarget reads the reticle; SpellTarget == null confirms there's no locked entity. (#236)
			var isGroundCast = CastGroundTargetScan.TryReadPosition(actionTrailer, out var groundTarget) && args.Target == null;

			// #767: THERE IS NO SERVER-SIDE RECAST GATE, because retail does not have one. This used to refuse a cast
			// whose recast was still running, by sending NOTHING - and both halves of that were wrong.
			//
			// Measured over the dumps, filtering c2s 0x642cd3d6 to BEGINs (state word 3; state 2 is the cancel, and
			// counting those as re-casts is what made the first pass of this look the other way): the corpus holds
			// 2,131 begins, 9 of them sent while the spell was still on its recast. Retail's reply to SIX of the nine
			// is `(4, 10)` - begin-echo then COMPLETE, byte-identical to an ordinary cast. Dread Archmage: Blood
			// Arcanist (22 s recast) completed at 5.29 s, Deathless Acolyte: Magus (22 s) at 4.75 s, Ancient Soul
			// (60 s) at 9.59 s. Retail simply casts them.
			//
			// So the recast is a CLIENT concern: the client reads it from its own RDB and blocks its own button, and
			// on the rare occasion it sends anyway, the server obliges. The old comment's reasoning ("the client will
			// not SEND one, so there is no captured answer") had the first half roughly right - 9 in 2,131 - and the
			// second half exactly backwards: there are captured answers, and they say "cast it".
			//
			// ⚠️ And refusing in silence BREAKS THE CLIENT. No echo-ack means the cast bar sticks at 100%, the client
			// stays in the channel state, and every later cast is dropped: in-client, "after casting ancient soul, i
			// cant cast anymore" - the server log showing `CastSpell REFUSED ... 16764 ms of its 6000 cs recast left`
			// at that exact moment.
			//
			// The recast is still ARMED below, because the timer itself is real and #767 wants it for the client-side
			// signal that should start the countdown (the client's own timer is not running - "the countdown is not
			// even visible"). It just does not gate anything here.
			//
			// The spell's base recast is `Spell.RecastCentiseconds`, off the record's SetSpellRecastTime /
			// SetSpellLineRecastTime element (docs/re/spell-recast-source.md).
			var spell = await _database.GetSpell(spellId);
			// The group the client itself arms: a line-scoped recast covers every rank at once, which is what
			// SetSpellLineRecastTime means and why all five Chill ranks share one 4 s timer. 0 = the spell has no
			// recast, so nothing is tracked for it.
			var recastGroup = spell is { RecastCentiseconds: > 0 }
				? spell.GuiStackingLine != 0
					? spell.GuiStackingLine
					: spell.Id
				: 0;
			if (recastGroup != 0 && client.Session.SpellReadyAt.TryGetValue(recastGroup, out var readyAt) && Environment.TickCount64 < readyAt)
			{
				// Logged, not refused - see above. Worth keeping because it is how we will tell whether the
				// client-side countdown has started working once #767 lands.
				_logger.LogInformation("CastSpell spell=0x{Spell:X} ({Name}) arrived {Remaining} ms into its {Recast} cs recast - retail casts it, so we do too (#767).",
					spellId, spell!.Name, readyAt - Environment.TickCount64, spell.RecastCentiseconds);
			}

			// This cast's invalidation token: a later cancel (or a new cast) bumps ActiveCastToken so the
			// deferred Resolve below aborts if the cast was interrupted before its cast time elapsed.
			var castToken = ++client.Session.ActiveCastToken;

			// When this cast STARTED. The recast is measured from here, not from when the spell goes off - see the
			// arming site in Resolve() below (#271).
			var castBeganAt = Environment.TickCount64;

			_logger.LogInformation($"CastSpell spell=0x{spellId:X} target=0x{targetHandle:X}{(isGroundCast ? $" ground={groundTarget}" : "")} {args}");

			// Cast-begin ack (#236): retail answers the cast behavior with a BCC echo right away, which
			// is what makes the client enter the channel state + render the cast bar (without it the
			// client never channels - it sent only Mode 1, no progress ticks - and the cast looks
			// instant). Ack every cast packet (begin + channel ticks); the effect itself is resolved
			// once, below, after the cast time.
			await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

			// Shield of the Risen breaks the moment an AGGRESSIVE cast is COMMITTED (cast begin), not when
			// the cast time elapses (#361) - a ground AoE (Deathly Blizzard) or an offensive cast at another
			// entity. A self-buff / self-target cast never breaks it (BreakShieldOnAggression is a no-op with
			// no shield tracked anyway). Done here at begin so the protection drops as the player starts to
			// attack, matching retail; the effect itself still resolves after castMs below.
			if (isGroundCast || (targetHandle != 0 && targetHandle != casterHandle))
				await _buffs.BreakShieldOnAggression(client);

			// Honor the spell's cast time (#236): retail resolves the effect only AFTER the cast time
			// elapses (ice-strike capture: cast at frame 377, ApplySpell impact ~frame 441). Resolving on
			// receipt skipped the client's cast bar. CastTime 0 (instant spell, or an un-populated row)
			// resolves immediately. Movement mid-cast interrupts it (the cancel branch above / the move
			// handler's token bump); the abort BCC drops the client bar (#347).
			// #710: a trained FEAT or a live BUFF can shorten the cast. The client states these as
			// `SpellStatModifier`/`SpellLineStatModifier` records on both sources, and `SpellModifierService` folds
			// them. ⭐ BOTH halves apply since #787: the absolute delta on `Casting Time (Seconds)` is in the
			// property-run field's own CENTISECONDS - the client divides that field by 100 and `CastTime` is built
			// from it by x10 - so 99 rows that used to be read and dropped now shorten a cast.
			var castModifiers = await _spellModifiers.For(client.Session.Character.Id, _sim.PlayerWorldEntity(client), spellId);
			var castMs = castModifiers.Apply(spell?.CastTime ?? 0, CharacterStatId.CastingTimeSeconds);

			if (castMs == 0)
			{
				await Resolve();
				return;
			}

			// Hold the begin's args (only) so the cast-cancel handler can build the state=0x0007 abort that
			// stops the client's cast bar (#347). The rest of the abort comes from the cancel packet.
			// Cleared on resolve/cancel. The args come off the codec, not off offsets 22/26 (#530 wave 7).
			client.Session.PendingCastArgs = BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint))).Args;
			client.Session.PendingCastMode = args.CommandqueueClientId;

			// A channeled ground AoE (Deathly Blizzard) ticks damage DURING the channel, not one hit at
			// the end (#349) - kick the tick loop now; it stops itself if the cast is interrupted (the
			// castToken it captured gets bumped). The complete-ack + mana still fire from Resolve at the
			// channel's end.
			if (isGroundCast)
				_ = _combat.ChannelGroundAoe(client, casterHandle, groundTarget, spellId, castMs, castToken);

			Task.RunAfter(castMs, async () => // resolve the effect after the cast time (#236)
			{
				if (client.Session.Account != null)
					await Resolve();
			}, _logger);

			return;

			// Resolves the cast: play the ApplySpell (caster -> target, or self when no target), drain
			// mana, and on an offensive cast apply damage + push the target's vitals (#69).
			async Task Resolve()
			{
				// Interrupted mid-cast (the player moved / sent the cancel BCC): ActiveCastToken was bumped
				// after we captured castToken, so skip the effect entirely - no complete-ack, no damage. This
				// is what makes the move-interrupt stick (the "then moving to interrupt" cast in the capture).
				if (client.Session.ActiveCastToken != castToken)
					return;

				client.Session.PendingCastArgs = null!; // cast completed; no abort needed

				// #271: an interrupted cast costs no cooldown, which is why the recast is ARMED here, in the branch
				// that only runs when the spell actually goes off - but it is MEASURED from when the cast BEGAN.
				//
				// ⚠️ Those are two different questions and answering both with "now" made every spell lock out for
				// castTime + recast instead of max(castTime, recast). The CLIENT disagrees, and the client is the
				// authority here - it reads the recast out of its own RDB. Smite (Rank 1) is a 2.0 s cast with a
				// 1.0 s recast, and the client re-requested it 2.43 s after the begin: valid if the recast ran from
				// the begin (ready at 2.0 s), 0.57 s early if it ran from the completion (ready at 3.0 s). We refused
				// it - and because a refusal is answered with SILENCE, the client then sat waiting on a reply that
				// never came, which is what "i cant smite more than once" and "the second one goes off way later, by
				// itself" actually were.
				//
				// Corroborated in capture: `Ice Strike (Rank 6)` is a 2.0 s cast with recast 0, and its captured
				// begin->begin gap is 2.11 s - i.e. retail's client re-casts the instant the channel ends. There is
				// no captured repeat of a spell that HAS a recast (both repeat-cast spells in the corpus carry 0), so
				// the begin-relative reading rests on the client's own behaviour rather than on a measured gap.
				if (recastGroup != 0)
				{
					client.Session.SpellReadyAt[recastGroup] = castBeganAt + spell!.RecastCentiseconds * 10;
					await SendSpellCooldown(client, spell);
				}

				// Cast-complete (#236): the cast bar is the form-0x0a cast behavior (retail BEGIN2/completion BCC,
				// ice-strike idx481/534), not the form-4 begin, with phase=3 (cast-state complete). Without this the
				// bar fills then hangs. This used to reach form 0x0a by stamping seq=9 over offsets 16/17 of a CLONE of
				// the request, so the echo-ack's +1 would land on it - which built a record the codec cannot read
				// (phase 0x09 ends at the seq word) and, once the codec turned strict, THREW here inside the deferred
				// resolve. That threw away the complete-ack entirely and left the bar at 100%. The form is stated now.
				await CombatService.SendBehaviorChannelCompleteAck(client, rawPayload, 3);

				// #439/#307: a cast whose effect is something the SERVER does - today a PET SUMMON (creature +
				// PetIIR owner-assign + follow) - resolves as that instead of applying a target effect. This
				// is what hooks pet summoning to real spell casts, not just the .pet GM command. Decoded
				// from the necro-pet capture (docs/subsystems/summon-pet-decode.md): cast (CastSpell) ->
				// after the channel -> FA015807 pet spawn + PetIIR. The spell -> effect mapping is a
				// SpellEffect row, so a new summon is an imported row rather than a branch here.
				var castEffects = await _spellEffects.ForCast(spellId);
				if (await _spellEffects.RunAll(client, castEffects))
				{
					_logger.LogInformation("CastSpell 0x{Spell:X} resolved {Count} server-side effect(s): {Kinds}.", spellId, castEffects.Count,
						string.Join(", ", castEffects.Select(e => e.Kind)));
					return;
				}

				// GROUND-TARGETED AoE: damage attackable NPCs around the reticle instead of applying a single
				// target debuff or a self-buff. The blizzard/AoE VFX is rendered client-side from the cast
				// behavior itself (retail sent no ApplySpell on the empty-ground casts - only the complete-ack,
				// the DispatchLockTemplate unlocks, and the mana push), so there is no ApplySpell carrier here.
				if (isGroundCast)
				{
					await _combat.SpendCasterMana(client, spellId, castModifiers);
					// A channeled AoE (castMs > 0) already ticked its damage during the channel
					// (ChannelGroundAoe, kicked at begin); only an INSTANT ground cast resolves its
					// single blast here (#349).
					if (castMs == 0)
						await _combat.ResolveGroundAoe(client, casterHandle, groundTarget, spellId);
					return;
				}

				// applyTo = the spell's SUBJECT (the target, or the caster for a targetless self-cast),
				// applyFrom = the CASTER. Passing these the other way round applied every hostile cast
				// to the CASTER "from" the NPC: the debuff landed on the player's own buff bar, and
				// because applyTo != applyFrom SendMagicBuffIir never registered it, so it never
				// expired - a permanent 0s icon per cast. Invisible on a self-cast, where both
				// arguments are casterHandle. (#370)
				//
				// ApplySpell takes the WIRE buff id ((base<<8)|rank), but the cast trailer carries the
				// bare BASE id (the Spells key) - shift it to rank 0. Unshifted, the client resolved
				// base=spellId>>8 / rank=spellId's low byte, neither of which is a real spell, and
				// silently rendered no icon while the server-side lifecycle looked correct (#274).
				await _buffs.ApplySpell(client, targetHandle == 0
					? casterHandle
					: targetHandle, casterHandle, spellId << 8);
				await _combat.SpendCasterMana(client, spellId, castModifiers);

				// #772: the spell's INSTANT areas - "apply spell X to up to N entities within R metres". Distinct from
				// the ground-AoE branch above, which is aimed at a reticle; these are self-centred, which is what the
				// client's own "Hostile Self Centered Area Spell" targeting line means. `Life Leech` is the whole
				// mechanic in one row (220980 to 12 targets within 5 m) - the castable carries no damage of its own,
				// so until the area elements were decoded (#772) casting it did nothing to anyone.
				//
				// Through the SIMULATION rather than straight at CombatService: applying an area is the damage AND
				// the effect itself landing on each victim (#776), and the buff registry that second half writes to
				// belongs to the world model.
				foreach (var area in await _spellAreas.InstantAreas(spellId))
				{
					var hit = await _sim.ApplyAreaEffect(client, casterHandle, casterHandle, area);
					_logger.LogInformation("Cast {Spell} applied area effect {Effect} to {Hit} target(s) within {Radius}m.", spellId, area.EffectSpellId, hit, area.RadiusMetres);
				}

				if (targetHandle != 0 && targetHandle != casterHandle)
					await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId, modifiers: castModifiers);
			}
		}

		// The client cancelled an in-flight cast (it moved, or the player pressed escape). Invalidate the pending
		// resolve and drop the client's cast bar.
		private async Task Cancel(INetworkClient client, byte[] rawPayload, CommandMessage args)
		{
			client.Session.ActiveCastToken++; // a pending Resolve() now sees a mismatched token and aborts
			var beginArgs = client.Session.PendingCastArgs;
			client.Session.PendingCastArgs = null!;

			// Retail answers the cancel with a state=0x0007 abort BCC (NOT an echo of the cancel) - that
			// is what drops the cast bar, cancels the animation and pops the client's "Interrupted" text
			// (#347, cast-interupt idx1013). The old echo of the cancel payload left the bar running. Needs
			// the stored BEGIN args; fall back to the plain echo if it's missing.
			// A cast the SERVER opened (rest, #505) is aborted differently - see ServerCastInterruptAbort. It is
			// marked by PendingCastMode 0, which no client cast can carry: the mode IS the client's command id.
			if (beginArgs != null && client.Session.PendingCastMode == 0)
				await CombatService.SendServerCastInterruptAbort(client, beginArgs, rawPayload, args.CommandqueueClientId);
			else if (beginArgs != null)
				await CombatService.SendCastInterruptAbort(client, beginArgs, rawPayload, client.Session.PendingCastMode);
			else
				await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

			_logger.LogInformation("CastSpell CANCEL (interrupted/moved) - abort ack sent, cast bar cleared.");
		}

		// Greys the spell's action-bar button and starts its cooldown sweep (#767) - `DispatchLockTemplate`, which is
		// how retail states a recast and the reason ours never appeared: we sent none of the 992 the corpus carries.
		//
		// There is no recast GameAction on the wire (0 across all 132 dumps); the cooldown IS this message. Body is
		// [u32 spellId][f32 seconds], and of the 741 captured bodies whose id resolves, 580 carry exactly the spell's
		// own `RecastCentiseconds / 100` (the rest are partials - time LEFT, on a refresh).
		//
		// ⚠️ ONE PER SPELL IN THE LINE, not one for the cast. A `Contract of Protection` cast emits five bodies in a
		// row - every rank on line 12 - each with the same 20.0. That is the wire form of `SetSpellLineRecastTime`,
		// and it is why all five ranks of a spell grey together. So this resolves the line's membership rather than
		// sending the cast id alone; a spell with no line locks only itself, matching `recastGroup`.
		//
		// WHETHER to send one and WHAT value it carries are SpellCooldownPolicy's (#768), measured over all 687
		// captured casts. This used to send what was LEFT of the recast measured from the cast begin, which retail
		// does not do - and the two spells that exposed it are the two the policy now names.
		private async Task SendSpellCooldown(INetworkClient client, Spell spell)
		{
			if (!SpellCooldownPolicy.Locks(spell))
				return;

			var seconds = SpellCooldownPolicy.LockSeconds(spell);
			var locked = spell.GuiStackingLine != 0
				? await _database.SpellIdsOnLine(spell.GuiStackingLine)
				: new[] { spell.Id };

			foreach (var templateId in locked)
				await client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchLockTemplate, s => s.WriteArray(LockTemplateCodec.Build(templateId, seconds))));

			_logger.LogDebug("Locked {Count} template(s) for {Seconds:F2}s after casting {Spell} ('{Name}').", locked.Length, seconds, spell.Id, spell.Name);
		}
	}
}
