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
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The cast pipeline: one client <c>CastSpell</c> behaviour, from begin to effect. Acks the begin so the client
	///     renders its cast bar, holds the cast time, then resolves - server-side effects, ground AoE, the buff or
	///     debuff that lands, mana, the spell's instant areas and the offensive-cast damage - or aborts cleanly when
	///     the cast is interrupted.
	/// </summary>
	/// <remarks>
	///     A cast acks immediately and resolves later (after <c>castMs</c>), and everything between those two moments
	///     can be invalidated by a move or a cancel. The invalidation token (<c>Session.ActiveCastToken</c>) ties them
	///     together: captured at begin, re-checked at resolve.
	/// </remarks>
	public sealed class CastService
	{
		// The BCC behaviourType that means the client is cancelling a cast rather than beginning one. State values are
		// capture-derived (actions-described-in-chat: begin=3 x2, cancel=2 x1; every cast in the
		// ice-strike/casilda/casting captures is begin=3).
		private const ushort CastStateCancel = 2;

		private readonly BuffService _buffs;

		// The begin BCC is relayed to viewers by the listener, but the complete (and the interrupt abort) are
		// server-timed and sent from here - without relaying them too, an observer's client holds the cast pose for
		// ever.
		private readonly World.WorldBroadcaster _broadcaster;
		private readonly CombatService _combat;
		private readonly ICatalogRepository _database;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		// A ground AoE's tick applies through the same path the world tick uses for an aura area.
		private readonly BuffTickService _buffTicks;

		// The spell's instant self-centred areas, and the chain edges the buff engine walks.
		private readonly SpellAreaCatalog _spellAreas;

		// A cast whose effect is something the server does (a pet summon, an armed proc).
		private readonly SpellEffectService _spellEffects;

		// The per-cast overrides a caster's trained feats and live buffs put on this spell.
		private readonly SpellModifierService _spellModifiers;

		// The procs the caster's feats hang off this spell's line, fired when the cast completes.
		private readonly ProcService _procs;

		public CastService(ICatalogRepository database, WorldSimulation sim, BuffTickService buffTicks, CombatService combat, BuffService buffs,
			SpellEffectService spellEffects, SpellModifierService spellModifiers, SpellAreaCatalog spellAreas, ProcService procs, ILogger logger, WorldBroadcaster broadcaster)
		{
			_procs = procs;
			_database = database;
			_sim = sim;
			_buffTicks = buffTicks;
			_combat = combat;
			_buffs = buffs;
			_spellEffects = spellEffects;
			_spellModifiers = spellModifiers;
			_spellAreas = spellAreas;
			_logger = logger;
			_broadcaster = broadcaster;
		}

		public async Task HandleCast(INetworkClient client, byte[] rawPayload, CommandMessage args, ushort behaviorType, uint target, byte[] actionTrailer)
		{
			// The BCC behaviorType tells begin from cancel: 3 = begin, 2 = cancel. The client sends the cancel when the
			// cast is interrupted (moving mid-cast), so honor it by invalidating any in-flight resolve - bumping the
			// token makes the deferred Resolve below abort - and acking the cancel.
			if (behaviorType == CastStateCancel)
			{
				await Cancel(client, rawPayload, args);
				return;
			}

			// The real spell id rides the action trailer (`target`, the uint32 read after the protobuf args), not the
			// args message. Decoded from the ice-strike capture (c2s 0x642CD3D6 cast): trailer = [u32 from=1][u32
			// spellId], with 0x125D9B / 0x42CA77 real learned-spell ids. Field 10 is a ProtoID32 {type, instance}
			// (CommandMessage.target) whose .Instance is the target entity; its .Type is only a fallback spell id.
			var spellId = target != 0
				? target
				: args.Target?.Type ?? args.BccActionId;
			var casterHandle = client.Session.Character.Id;
			var targetHandle = args.Target?.Instance ?? 0;

			// A ground-targeted AoE (Deathly Blizzard, say) is aimed at a reticle position carried in the trailer
			// instead of a locked entity: it has no spell target and the trailer's kind byte is 0. The scan reads the
			// reticle; a null Target confirms there is no locked entity.
			var isGroundCast = CastGroundTargetScan.TryReadPosition(actionTrailer, out var groundTarget) && args.Target == null;

			// There is no server-side recast gate, because retail does not have one. Measured over the dumps, filtering
			// c2s 0x642cd3d6 to begins (state word 3; state 2 is the cancel): the corpus holds 2,131 begins, 9 of them
			// sent while the spell was still on its recast, and retail's reply to six of the nine is `(4, 10)` -
			// begin-echo then complete, byte-identical to an ordinary cast. Dread Archmage: Blood Arcanist (22 s
			// recast) completed at 5.29 s, Deathless Acolyte: Magus (22 s) at 4.75 s, Ancient Soul (60 s) at 9.59 s.
			//
			// The recast is therefore a client concern: the client reads it from its own RDB and blocks its own button,
			// and on the rare occasion it sends anyway, the server obliges. Refusing in silence also breaks the client
			// outright - with no echo-ack the cast bar sticks at 100%, the client stays in the channel state, and every
			// later cast is dropped.
			//
			// The recast is still armed below, because the timer itself is real and drives the client-side countdown
			// signal; it simply does not gate anything here. The spell's base recast is `Spell.RecastCentiseconds`, off
			// the record's SetSpellRecastTime / SetSpellLineRecastTime element (docs/re/spell-recast-source.md).
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
				// Logged, not refused - see above. It is also how a working client-side countdown can be told from a
				// stalled one.
				_logger.LogInformation("CastSpell spell=0x{Spell:X} ({Name}) arrived {Remaining} ms into its {Recast} cs recast - retail casts it, so we do too (#767).",
					spellId, spell!.Name, readyAt - Environment.TickCount64, spell.RecastCentiseconds);
			}

			// This cast's invalidation token: a later cancel (or a new cast) bumps ActiveCastToken so the
			// deferred Resolve below aborts if the cast was interrupted before its cast time elapsed.
			var castToken = ++client.Session.ActiveCastToken;

			// When this cast started. The recast is measured from here, not from when the spell goes off - see the
			// arming site in Resolve() below.
			var castBeganAt = Environment.TickCount64;

			_logger.LogInformation($"CastSpell spell=0x{spellId:X} target=0x{targetHandle:X}{(isGroundCast ? $" ground={groundTarget}" : "")} {args}");

			// Cast-begin ack: retail answers the cast behavior with a BCC echo right away, which is what makes the
			// client enter the channel state and render the cast bar; without it the client never channels and the cast
			// looks instant. Every cast packet is acked (begin and channel ticks); the effect itself resolves once,
			// below, after the cast time.
			await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

			// Shield of the Risen breaks the moment an aggressive cast is committed (cast begin), not when the cast time
			// elapses - a ground AoE (Deathly Blizzard) or an offensive cast at another entity. A self-buff or
			// self-target cast never breaks it. Done here at begin so the protection drops as the player starts to
			// attack, matching retail; the effect itself still resolves after castMs below.
			if (isGroundCast || (targetHandle != 0 && targetHandle != casterHandle))
				await _buffs.BreakShieldOnAggression(client);

			// Honor the spell's cast time: retail resolves the effect only after the cast time elapses (ice-strike
			// capture: cast at frame 377, ApplySpell impact around frame 441). CastTime 0 - an instant spell, or an
			// un-populated row - resolves immediately. Movement mid-cast interrupts it (the cancel branch above, or the
			// move handler's token bump) and the abort BCC drops the client bar.
			//
			// A trained feat or a live buff can shorten the cast. The client states these as
			// `SpellStatModifier`/`SpellLineStatModifier` records on both sources, and `SpellModifierService` folds
			// them. The absolute delta on `Casting Time (Seconds)` is in the property-run field's own centiseconds -
			// the client divides that field by 100 and `CastTime` is built from it by x10.
			var castModifiers = await _spellModifiers.For(client.Session.Character.Id, _sim.PlayerWorldEntity(client), spellId);
			var castMs = castModifiers.Apply(spell?.CastTime ?? 0, CharacterStatId.CastingTimeSeconds);

			if (castMs == 0)
			{
				await Resolve();
				return;
			}

			// Hold the begin's args so the cast-cancel handler can build the state=0x0007 abort that stops the client's
			// cast bar; the rest of the abort comes from the cancel packet. Cleared on resolve or cancel.
			client.Session.PendingCastArgs = BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint))).Args;
			client.Session.PendingCastMode = args.CommandqueueClientId;

			// A channeled ground AoE (Deathly Blizzard) ticks damage during the channel, not as one hit at the end -
			// kick the tick loop now; it stops itself if the cast is interrupted, because the castToken it captured is
			// bumped. The complete-ack and mana still fire from Resolve at the channel's end.
			if (isGroundCast)
				_ = _combat.ChannelGroundAoe(client, casterHandle, groundTarget, spellId, castMs, castToken);

			Task.RunAfter(castMs, async () => // resolve the effect after the cast time
			{
				// A caster who logged out during the cast resolves nothing: no damage, XP or loot for a session that has gone.
				if (!client.IsStopped)
					await Resolve();
			}, _logger);

			return;

			// Resolves the cast: play the ApplySpell (caster -> target, or self when no target), drain mana, and on an
			// offensive cast apply damage and push the target's vitals.
			async Task Resolve()
			{
				// Interrupted mid-cast (the player moved, or sent the cancel BCC): ActiveCastToken was bumped after
				// castToken was captured, so skip the effect entirely - no complete-ack, no damage. This is what makes
				// the move-interrupt stick.
				if (client.Session.ActiveCastToken != castToken)
					return;

				client.Session.PendingCastArgs = null!; // cast completed; no abort needed

				// An interrupted cast costs no cooldown, which is why the recast is armed here, in the branch that only
				// runs when the spell actually goes off - but it is measured from when the cast began, so a spell locks
				// out for max(castTime, recast) rather than castTime + recast.
				//
				// The client is the authority on that, since it reads the recast out of its own RDB: Smite (Rank 1) is
				// a 2.0 s cast with a 1.0 s recast, and the client re-requests it 2.43 s after the begin - valid from
				// the begin (ready at 2.0 s), 0.57 s early from the completion (ready at 3.0 s). Corroborated in
				// capture: `Ice Strike (Rank 6)` is a 2.0 s cast with recast 0 and a captured begin->begin gap of
				// 2.11 s, so retail's client re-casts the instant the channel ends. No capture repeats a spell that
				// has a recast (both repeat-cast spells in the corpus carry 0), so the begin-relative reading rests on
				// the client's behaviour rather than on a measured gap.
				if (recastGroup != 0)
				{
					client.Session.SpellReadyAt[recastGroup] = castBeganAt + spell!.RecastCentiseconds * 10;
					await SendSpellCooldown(client, spell);
				}

				// Cast-complete: the cast bar closes on the form-0x0a cast behavior (retail's BEGIN2/completion BCC,
				// ice-strike idx481/534), not the form-4 begin, with phase=3 (cast-state complete). Without it the bar
				// fills and then hangs.
				await CombatService.SendBehaviorChannelCompleteAck(client, rawPayload, 3);
				// Observers got the relayed cast begin; without the complete they hold the cast pose for ever. Retail's
				// cross-player channel is the same BCC replication, so the identical complete body goes to every viewer
				// who already sees the caster.
				await _broadcaster.BroadcastBehaviorToViewers(client, BehaviorControlCenterCodec.Build(BehaviorAcks.ChannelCompleteAck(rawPayload, client.Session.Character.Id, 3)));

				// A cast whose effect is something the server does - a pet summon (creature + PetIIR owner-assign +
				// follow) - resolves as that instead of applying a target effect. Decoded from the necro-pet capture
				// (docs/subsystems/summon-pet-decode.md): cast (CastSpell) -> after the channel -> FA015807 pet spawn +
				// PetIIR. The spell-to-effect mapping is a SpellEffect row, so a new summon is an imported row rather
				// than a branch here.
				var castEffects = await _spellEffects.ForCast(spellId);
				if (await _spellEffects.RunAll(client, castEffects))
				{
					_logger.LogInformation("CastSpell 0x{Spell:X} resolved {Count} server-side effect(s): {Kinds}.", spellId, castEffects.Count,
						string.Join(", ", castEffects.Select(e => e.Kind)));
					return;
				}

				// Ground-targeted AoE: damage attackable NPCs around the reticle instead of applying a single-target
				// debuff or a self-buff. The AoE visual is rendered client-side from the cast behavior itself - retail
				// sends no ApplySpell on an empty-ground cast, only the complete-ack, the DispatchLockTemplate unlocks
				// and the mana push - so there is no ApplySpell carrier here.
				if (isGroundCast)
				{
					await _combat.SpendCasterMana(client, spellId, castModifiers);
					// A channeled AoE (castMs > 0) already ticked its damage during the channel via ChannelGroundAoe,
					// kicked at begin; only an instant ground cast resolves its single blast here.
					if (castMs == 0)
						await _combat.ResolveGroundAoe(client, casterHandle, groundTarget, spellId);
					return;
				}

				// The argument order matters: applyTo is the spell's subject (the target, or the caster for a targetless
				// self-cast) and applyFrom is the caster. Reversed, a hostile cast lands on the caster's own buff bar
				// and, because applyTo != applyFrom, is never registered for expiry - a permanent 0s icon per cast.
				//
				// ApplySpell takes the wire buff id ((base<<8)|rank), but the cast trailer carries the bare base id (the
				// Spells key), so it is shifted to rank 0 here. Unshifted, the client resolves base=spellId>>8 and
				// rank=spellId's low byte, neither of which is a real spell, and silently renders no icon.
				await _buffs.ApplySpell(client, targetHandle == 0
					? casterHandle
					: targetHandle, casterHandle, spellId << 8);
				await _combat.SpendCasterMana(client, spellId, castModifiers);

				// The spell's instant areas - "apply spell X to up to N entities within R metres". Distinct from the
				// ground-AoE branch above, which is aimed at a reticle; these are self-centred, which is what the
				// client's "Hostile Self Centered Area Spell" targeting line means. `Life Leech` is the whole mechanic
				// in one row (220980 to 12 targets within 5 m), because the castable carries no damage of its own.
				//
				// Applied through the simulation rather than straight at CombatService: an area is the damage and the
				// effect itself landing on each victim, and the buff registry that second half writes to belongs to the
				// world model.
				foreach (var area in await _spellAreas.InstantAreas(spellId))
				{
					var hit = await _buffTicks.ApplyAreaEffect(client, casterHandle, casterHandle, area);
					_logger.LogInformation("Cast {Spell} applied area effect {Effect} to {Hit} target(s) within {Radius}m.", spellId, area.EffectSpellId, hit, area.RadiusMetres);
				}

				var offensive = targetHandle != 0 && targetHandle != casterHandle;
				if (offensive)
					await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId, modifiers: castModifiers);

				// The procs the caster's feats hang off this spell's line - a Cast trigger on any cast ("after casting
				// the Wave of Life..."), a SpellDamage trigger only on one that landed on a hostile.
				await _procs.OnSpellCast(client, spellId, targetHandle, offensive);
			}
		}

		/// <summary>
		///     The client cancelled an in-flight cast (it moved, or the player pressed escape): invalidate the pending
		///     resolve and drop the client's cast bar.
		/// </summary>
		private async Task Cancel(INetworkClient client, byte[] rawPayload, CommandMessage args)
		{
			client.Session.ActiveCastToken++; // a pending Resolve() now sees a mismatched token and aborts
			var beginArgs = client.Session.PendingCastArgs;
			client.Session.PendingCastArgs = null!;

			// Retail answers the cancel with a state=0x0007 abort BCC, not an echo of the cancel - that is what drops
			// the cast bar, cancels the animation and pops the client's "Interrupted" text (cast-interupt idx1013). It
			// needs the stored begin args; fall back to the plain echo when they are missing.
			//
			// A cast the server opened (rest) is aborted differently - see ServerCastInterruptAbort. It is marked by
			// PendingCastMode 0, which no client cast can carry, because the mode is the client's command id.
			if (beginArgs != null && client.Session.PendingCastMode == 0)
				await CombatService.SendServerCastInterruptAbort(client, beginArgs, rawPayload, args.CommandqueueClientId);
			else if (beginArgs != null)
			{
				await CombatService.SendCastInterruptAbort(client, beginArgs, rawPayload, client.Session.PendingCastMode);
				// Same reason as the complete relay: a cancelled cast must end the pose on the observers' clients too.
				await _broadcaster.BroadcastBehaviorToViewers(client,
					BehaviorControlCenterCodec.Build(BehaviorAcks.CastInterruptAbort(beginArgs, rawPayload, client.Session.Character.Id, client.Session.PendingCastMode)));
			}
			else
				await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

			_logger.LogInformation("CastSpell CANCEL (interrupted/moved) - abort ack sent, cast bar cleared.");
		}

		/// <summary>
		///     Greys the spell's action-bar button and starts its cooldown sweep.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <c>DispatchLockTemplate</c> is how retail states a recast: there is no recast GameAction on the wire
		///         (0 across all 132 dumps), the cooldown is this message. The body is [u32 spellId][f32 seconds], and
		///         of the 741 captured bodies whose id resolves, 580 carry exactly the spell's own
		///         <c>RecastCentiseconds / 100</c> - the rest are partials, the time left on a refresh.
		///     </para>
		///     <para>
		///         One message per spell in the line, not one for the cast. A <c>Contract of Protection</c> cast emits
		///         five bodies in a row - every rank on line 12 - each carrying the same 20.0. That is the wire form of
		///         <c>SetSpellLineRecastTime</c>, and why all five ranks of a spell grey together. This therefore
		///         resolves the line's membership rather than sending the cast id alone; a spell with no line locks
		///         only itself, matching <c>recastGroup</c>.
		///     </para>
		///     <para>
		///         Whether to send one at all, and what value it carries, is <see cref="SpellCooldownPolicy" />'s
		///         decision, measured over all 687 captured casts.
		///     </para>
		/// </remarks>
		private async Task SendSpellCooldown(INetworkClient client, Spell spell)
		{
			if (!SpellCooldownPolicy.Locks(spell))
				return;

			var seconds = SpellCooldownPolicy.LockSeconds(spell);
			var locked = spell.GuiStackingLine != 0
				? await _database.SpellIdsOnLine(spell.GuiStackingLine)
				: new[] { spell.Id };

			foreach (var templateId in locked)
				await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchLockTemplate, s => s.WriteArray(LockTemplateCodec.Build(templateId, seconds))));

			_logger.LogDebug("Locked {Count} template(s) for {Seconds:F2}s after casting {Spell} ('{Name}').", locked.Length, seconds, spell.Id, spell.Name);
		}
	}
}
