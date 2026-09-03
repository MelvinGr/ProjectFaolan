using System;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Behavior
{
	// The client's ACTION switch: BehaviorControlCenterIIR is how the player does a thing to a thing - attack, use a
	// dynel, talk to / trade with a character, equip, mount, swap weapon sets - and this is the one place that
	// reads the verb and the target and hands the pair to the service that owns the outcome. The five handlers
	// moved off GameServerListener.Inbound verbatim (#928 item 11): they were the last inbound family whose body
	// was not a forward, and between them they touch 23 services, which is the whole reason the listener's
	// action case was 690 lines.
	public sealed class BehaviorService
	{
		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly WorldBroadcaster _broadcaster;
		private readonly BuffService _buffs;
		private readonly CastService _cast;
		private readonly CombatService _combat;
		private readonly ComboFinisher _comboFinisher;
		private readonly ComboService _combos;
		private readonly ConversationService _conversation;
		private readonly EconomyOutbound _economyOutbound;
		private readonly EquipmentService _equipment;
		private readonly ItemUseService _itemUse;
		private readonly LootBroadcaster _lootBroadcaster;
		private readonly MountService _mounts;
		private readonly QuestProgressionService _questProgression;
		private readonly ShopService _shop;
		private readonly WorldSimulation _simulation;
		private readonly TeleportService _teleport;
		private readonly TradeService _trade;
		private readonly TradepostService _tradepost;
		private readonly TradeskillService _tradeskill;
		private readonly TravelOutbound _travelOutbound;
		private readonly VistaService _vista;
		private readonly WorldObjectService _worldObjects;
		private readonly ILogger _logger;
		private readonly IDatabaseRepository _database;

		public BehaviorService(IChatServerDispatcher agentServerDispatcher, WorldBroadcaster broadcaster, BuffService buffs, CastService cast, CombatService combat,
			ComboFinisher comboFinisher, ComboService combos, ConversationService conversation, EconomyOutbound economyOutbound,
			EquipmentService equipment, ItemUseService itemUse, LootBroadcaster lootBroadcaster, MountService mounts,
			QuestProgressionService questProgression, ShopService shop, WorldSimulation simulation, TeleportService teleport, TradeService trade,
			TradepostService tradepost, TradeskillService tradeskill, TravelOutbound travelOutbound, VistaService vista, WorldObjectService worldObjects,
			ILogger logger, IDatabaseRepository database)
		{
			_agentServerDispatcher = agentServerDispatcher;
			_broadcaster = broadcaster;
			_buffs = buffs;
			_cast = cast;
			_combat = combat;
			_comboFinisher = comboFinisher;
			_combos = combos;
			_conversation = conversation;
			_economyOutbound = economyOutbound;
			_equipment = equipment;
			_itemUse = itemUse;
			_lootBroadcaster = lootBroadcaster;
			_mounts = mounts;
			_questProgression = questProgression;
			_shop = shop;
			_simulation = simulation;
			_teleport = teleport;
			_trade = trade;
			_tradepost = tradepost;
			_tradeskill = tradeskill;
			_travelOutbound = travelOutbound;
			_vista = vista;
			_worldObjects = worldObjects;
			_logger = logger;
			_database = database;
		}

		// The BehaviorControlCenter (0x642CD3D6) inbound: by a wide margin the biggest thing the client sends on the
		// world carrier - every action that goes through the command queue (attack swings, interacts, sit, mount,
		// loot, emotes) arrives as one of its actionTypes. It was 669 lines INSIDE the HandleIirCarrier switch, 60% of
		// that method's 1140; splitting it out leaves the switch readable as the routing table it is.
		//
		// Pure move, no behaviour change: the four early `return`s inside were already equivalent to the case's
		// `break` because nothing follows the switch in HandleIirCarrier.
		public async Task HandleBehaviorControlCenter(INetworkClient client, Rpc packet, FaolanStream body, byte[] rawPayload)
		{
			// Important capture observation:
			// world-object interactions appear to be the likely trigger path for the container-style
			// response family 0xF096251E, which carries names like "Chest [N7FU]" and "object_closed".
			// Representative samples split into a small object/position block, a 125-byte metadata block, 					// and a variable nested entry block that currently decodes as 2, 3, or 4 repeated entries.
			// New random-actions evidence suggests the player bag is more likely part of the giant 0x737A6DF9
			// initial-state dump, while 0xF096251E still looks like remote chest/container data.
			// [u32 entity class][u32 handle][u16 behaviorType] - the same head the REGISTRATION form of this IIR
			// uses (docs/re/behaviorcontrolcenter-grammar.md calls that u16 `form`; it is 0x0004/0x0007 there). The u16
			// is really [u8 0][u8 behaviorType] feeding the client's own `BehaviorControlCenterIIR_c::ReadSubClass`
			// switch (FUN_005e24e0): types 2,3,4,5,7,10,0xF,0x10,0x12 go on to read [u32 commandId], while 6,8,9,0xD,0x11
			// are TERMINAL and read nothing more. c2s census across the corpus: 1282x 0x0003, 187x 0x0002, 1x 0x0008 -
			// and that single 0x0008 packet is exactly 18 bytes, i.e. the head and nothing else. So the "short body"
			// bail below is not a defensive guess, it is the terminal half of the client's own switch.
			body.ReadUInt32(); // EntityClass.Character (0xC350)
			var clientInst = body.ReadUInt32();
			var behaviorType = body.ReadUInt16();
			// Reading actionType unconditionally threw EndOfStreamException on the terminal forms, which ReceivedPacket
			// turns into a hard client.Stop() (disconnect). Bail gracefully on a short body.
			if (body.Length - body.Position < sizeof(uint))
			{
				_logger.LogDebug("Interact: terminal behaviorType 0x{BehaviorType:X4}, no commandId - ignoring.", behaviorType);
				return; // was a `break` out of the HandleIirCarrier switch - same exit, now from this method
			}

			var actionType = body.ReadUInt32<BehaviorId>();
			var args = body.ReadProtobufUInt32Length<CommandMessage>();

			if (actionType == BehaviorId.SwapAlternateEquipmentCommand)
			{
				// #400: SwitchWeaponsButton is the weapon-SET switch (toggles the two swappable loadouts), NOT a draw/sheathe -
				// capture-confirmed: a character wearing both sets pressed it 3x and the newly-active weapon main slot alternated
				// 6->20->6 (Stihetfv armed-and-unarmed). Echo-ack to self, toggle+persist the active set, then RELAY the swap
				// behavior to nearby viewers so they re-render the drawn weapons (the cross-player render channel is the BCC
				// command replication - Ghidra FUN_005e1c70 - the same path the combat swing/cast relay below uses; the
				// SetItemStat retail also sends is owner-only inventory bookkeeping, so it is not broadcast). NEEDS in-client verification.
				await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
				var activeSet = await _broadcaster.TogglePlayerWeaponSet(client);

				var swapRelay = BehaviorAcks.Relay(client.Session.Account!.ClientInstance, rawPayload, args.CommandqueueClientId);
				if (swapRelay != null)
					await _broadcaster.BroadcastBehaviorToViewers(client, BehaviorControlCenterCodec.Build(swapRelay));

				_logger.LogInformation("SwitchWeaponsButton -> weapon set {Set}. {Args}", activeSet, args);
			}
			else if (actionType == BehaviorId.Tradeskill)
			{
				// TRADESKILL. Its tail is NOT the [from][target] pair the rest of the switch reads, and it is not one
				// shape either: `0x27` is ONE registered command class carrying TWO operations, discriminated by the
				// tail's leading byte (#754).
				//
				//   0x01  craft from a recipe          [u32 recipeId][u32 quantity]
				//   0x02  combine two inventory items  [u32 container][u32 charInstance][u32 slot] x2
				//
				// ⚠️ The args do NOT discriminate: bcc_action_id is 49340 on both and only commandqueue_client_id
				// differs, which matters because that IS how MoveEquippedToFromInventory splits its directions.
				// [V2: ..._annotated_5 fr17374 (craft) and ..._annotated_8 fr8215 (socket)]
				TradeskillCommand? request = null;
				try
				{
					request = BehaviorTailScan.Tradeskill(body.ReadRemainingArray());
				}
				catch (CodecParseException e)
				{
					_logger.LogInformation("Tradeskill: unreadable tail ({Message}). {Args}", e.Message, args);
				}

				await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

				// ⭐ The ack is ALL retail sends now (#793). A tradeskill is a timed command-queue command - the client
				// runs a ~5 s progress bar off its own recipe data - and retail holds the grant for exactly that long,
				// measured server-side on both a craft and a socket. So this STAGES the transaction and the world tick
				// completes it; nothing is checked, taken or granted here.
				//
				// Retail answers a SUCCESSFUL tradeskill and says nothing at all otherwise, so a refusal is silence
				// then too - for both operations. The client keeps its window open rather than being told it worked.
				if (request != null)
				{
					var due = _tradeskill.Stage(client, request, DateTimeOffset.UtcNow);
					if (request.Action == TradeskillAction.Craft)
						_logger.LogInformation("Tradeskill: craft recipe {Recipe} x{Count}, completing at {Due:HH:mm:ss.fff}. {Args}", request.RecipeId, request.Count, due, args);
					else
						_logger.LogInformation("Tradeskill: socket {Container}[{Gem}] into [{Target}], completing at {Due:HH:mm:ss.fff}. {Args}", request.Target.Container, request.Gem.Slot,
							request.Target.Slot, due, args);
				}
			}
			else
			{
				// Not every action carries from/target after the args (e.g. TalkToNpc only has a short
				// trailer - the NPC handle lives in args.Target.Instance). Read defensively to avoid overrunning.
				var from = body.Length - body.Position >= sizeof(uint)
					? body.ReadUInt32()
					: 0; // fromid or slotid depending on actionType, see below
				var target = body.Length - body.Position >= sizeof(uint)
					? body.ReadUInt32()
					: 0;
				// Whatever the command form left over, decoded per actionType below (ground-target vec3, equip handle, ...).
				var actionTrailer = body.ReadRemainingArray();

				// Set by the one branch that ABSORBS a record rather than acting on it (the seated sit re-send); an
				// absorbed record is a client artefact, so there is nothing for a viewer to render either.
				var absorbed = false;

				switch (actionType)
				{
					case BehaviorId.Mount:
					{
						await HandleBccMount(client, rawPayload, args, from);

						break;
					}

					case BehaviorId.UseItem:
					{
						_logger.LogInformation("RightClickItem: container={Container} slot={Slot} {Args}", (ContainerKind)from, target, args);

						// Every behavior gets an echo-ack (docs/subsystems/combat.md rule 1); the item-use domain logic
						// lives in ItemUseService.UseInventoryItem. The used item is identified by its inventory location - from =
						// container (0x68 = backpack), target = slot - decoded from the use_totem_of_origin
						// capture (using a Totem of Origins recalls to the RACIAL home city, #206; the recall's handoff
						// is the capture's "long loading screen" - that capture is what pins Stygian -> Khemi).
						await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
						await _itemUse.UseInventoryItem(client, (ContainerKind)from, (int)target);

						break;
					}

					case BehaviorId.MagicAttack:
					{
						await _cast.HandleCast(client, rawPayload, args, behaviorType, target, actionTrailer);

						break;
					}

					case BehaviorId.CombatMelee:
					{
						// The melee swing / auto-attack: ack the behavior + resolve the hit (see
						// SendNormalAttackResponse). The target is in the request args (SpellTarget.TargetHandle).
						await _buffs.BreakShieldOnAggression(client); // swinging a weapon is aggressive - drops the shield (#361)
						var targetHandle = args.Target?.Instance ?? 0;

						// #139: the swing NAMES its combo and step (CommandMessage.combo_id / combo_attack_num), so
						// the runtime reads them rather than matching directions. Every step is an ordinary damaging
						// swing; the finisher adds the combo's own spell (ComboFinisher.Deliver).
						var combo = await _combos.TrackSwing(client, args);
						// #696: the finisher only lands if the swing carrying it did. It used to run unconditionally, so a
						// swing the reach gate refused still delivered the combo's spell - melee stayed unbounded for
						// anyone finishing a combo. The combo's step tracking above is deliberately still advanced on a
						// miss: the client counts the step it pressed either way, and desyncing the two ends would be worse.
						var landed = await _combat.SendNormalAttackResponse(client, rawPayload, targetHandle, args.CommandqueueClientId, combo);
						if (landed && combo is { IsFinisher: true })
							await _comboFinisher.Deliver(client, combo, targetHandle);

						break;
					}

					case BehaviorId.CombatUnarmed:
					{
						// Unarmed swing (empty main hand; behavior TypeIds 1089xx, ability id in the tail).
						// Retail's reply is the plain behavior echo-ack - no result block (combat capture
						// 2026-06-12); targeted damage reuses the armed path (the capture's unarmed swings
						// hit air, so the damage part is an extension pending in-client validation).
						await _buffs.BreakShieldOnAggression(client); // an unarmed swing is aggressive - drops the shield (#361)
						await _combat.SendUnarmedAttackResponse(client, rawPayload, args.CommandqueueClientId, args.Target?.Instance ?? 0);

						break;
					}

					case BehaviorId.Emote:
					{
						// Nothing to resolve server-side - an emote is pure animation. It is NOT a dead end though:
						// the relay below is what other players see, and before #912 there was none, so this case
						// really did end here. Retail relays Emote heavily (24,764 foreign-actor records across 88
						// captures - `--report bcc-relay`), which is mostly NPC ambient barks arriving at the player.
						_logger.LogInformation($"Emote: {args}");

						break;
					}

					case BehaviorId.UseDynel:
					{
						await HandleBccUseDynel(client, behaviorType, target);

						break;
					}

					case BehaviorId.UseCharacter:
					{
						await HandleBccUseCharacter(client, rawPayload, args, from, target);

						break;
					}

					case BehaviorId.Equip:
					{
						await HandleBccEquip(client, rawPayload, args, from, target, actionTrailer);

						break;
					}

					default:
					{
						_logger.LogInformation($"UNKNOWN-{actionType.ToHex()}: {args}");

						// Behaviour 1 while SEATED is the client ASKING to stand up (#355) - getting up is
						// client-initiated, not movement-inferred as we had it. Retail's sit capture, matched pairs:
						//   6829 c2s bId=1 seq=0x0003  ->  6837 s2c bId=1 seq=0x0004
						//   6830 c2s bId=1 seq=0x0002  ->  6838 s2c bId=1 seq=0x000F  ->  6844 s2c STANDUP (bId=0x2A)
						// i.e. the SAME two-phase confirm the sit runs on behaviour 0x12, then the standup animation.
						// Because we inferred standup from movement instead, these requests fell into this
						// un-decoded fallback and got a bare echo - so the client asked to get up, was never
						// answered, and stayed stuck in the pose until it timed out and stood up on its own.
						// The seq mapping (3->4, 2->0x0F) lives in BehaviorAcks.AckSeq.
						if ((uint)actionType == 1 && client.Session.SeatedOnHandle is not null)
						{
							var requestSeq = rawPayload.Length >= 18
								? (ushort)((rawPayload[16] << 8) | rawPayload[17])
								: 0;
							await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

							// The phase-2 confirm is what commits it; the standup animation follows the ack.
							if (requestSeq == 0x0002)
							{
								_logger.LogInformation("Sit: client asked to stand up (behaviour 1 phase 2) - answering and standing.");
								await _worldObjects.StandUp(client);
							}

							break;
						}

						// ...EXCEPT a server-driven object-action ANIMATION while seated (#355). The client re-sends the
						// sit behaviour (actionType 0x2A, carrying "chair_sit_calm") after we seat it; echoing that back
						// makes the client play the SIT again, and its 2.6s blend IS the progress bar seen while trying
						// to stand - which is also what delayed the standup. Retail emits chair_sit_calm exactly ONCE per
						// sit, s2c only, and never echoes one back. The guard is scoped to being seated because retail's
						// client does send 0x2A otherwise (17 across the captures) and those ARE answered, so a blanket
						// skip would soft-lock other object actions - the very thing the ack below exists to prevent.
						if (actionType == BehaviorId.ObjectInteraction && client.Session.SeatedOnHandle is not null)
						{
							_logger.LogInformation("Sit: absorbed the client's re-sent sit behavior (not echoed - it would replay the sit).");
							absorbed = true;

							break;
						}

						// Robustness (#236 channeled cast 0x15, #238 dodge hop): an un-decoded behavior must
						// STILL get the generic behavior echo-ack (docs/subsystems/combat.md rule 1), or the client stays
						// stuck in the action's start state forever (cast bar never finishes / dodge never lands
						// back to idle) and the player soft-locks until relog. Acking with the request's Mode
						// returns control even when we don't yet model the action's effect.
						await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

						break;
					}
				}

				// ⭐ RELAY BY DEFAULT (#912). The cross-player render channel is the BCC command replication (Ghidra
				// FUN_005e1c70): the actor's behaviour record, re-framed to each nearby viewer, is what makes an action
				// visible to anyone but the actor. #48 introduced it for the combat swing/cast ("invisible to other
				// players"), and it was an explicit ALLOWLIST that then grew once per discovery - #428 the mounted jump,
				// #403's sibling `Equip`. `/sit` was the next instance of the same bug: it arrives as
				// `BehaviorId.Transition`, which was not in the list, so the emote played on the actor's screen and
				// nowhere else.
				//
				// ⭐ The corpus says the SHAPE was wrong, not merely the contents (`--report bcc-relay`). Split every
				// s2c BCC by whether its actor is the RECIPIENT - the recipient's own handle being the modal actor over
				// that capture's c2s records - and retail relays a foreign actor's behaviour on **19 of the 23 ids the
				// corpus carries**, including both ids this ticket is about: `Transition` 1,233 relayed records across
				// 81 captures (against 719 echo-acks) and `Emote` 24,764 across 88. Five of nineteen was never going to
				// converge by discovery, so the list inverts: everything relays unless the corpus says otherwise.
				//
				// ⚠️ Exactly ONE id earns the exclusion. `Mount` is 100 s2c records, every one an echo-ack, over 25
				// captures that ALL carried foreign BCC actors - and a miss needs that control, because a zero is
				// otherwise just "nobody else did it nearby". It also has a mechanism: the viewer's mount visual is the
				// ride buff + StatUpd(Mount, MountSpeed) that `MountService.SeatAndPersist` already broadcasts, so
				// relaying the BCC as well would double-send it.
				//
				// ⚠️ `Search` (n=4), `Climbing` (4) and `Tradeskill` (2) are zero-relay too and are deliberately NOT
				// excluded: at that n a zero is not a rule, and the base rate among ids that had a real opportunity is
				// 19 of 19. They are recorded in the report rather than encoded here. (`Tradeskill` never reaches this
				// block anyway - it has its own branch above, as does the weapon-set switch, which relays itself.)
				if (!absorbed && actionType is not BehaviorId.Mount)
				{
					var behaviorRelay = BehaviorAcks.Relay(client.Session.Character.Id, rawPayload, args.CommandqueueClientId);
					if (behaviorRelay != null)
						await _broadcaster.BroadcastBehaviorToViewers(client, BehaviorControlCenterCodec.Build(behaviorRelay));
				}
			}
		}

		// Split out of HandleBehaviorControlCenter's action switch (#200) - a pure move; the case is now a one-liner.
		private async Task HandleBccMount(INetworkClient client, byte[] rawPayload, CommandMessage args, uint from)
		{
			// SummonMount TOGGLES (pressing the same button while mounted dismounts) and BOTH directions are
			// SERVER-timed channels - they only differ in how long the server holds the state:
			//  - MOUNT: the client shows the cast bar + horse-runs-up and retail sends the ride buff 545ms after the
			//    press (SummonChannelMs; mount capture frame 498 -> 520). So we begin-echo-ack, then after the channel
			//    complete-echo-ack + ride buff 0x31EB1500 + StatUpd(Mount=id, MountSpeed=100).
			//  - DISMOUNT: retail acks the press immediately but keeps the player MOUNTED for the horse-runs-away
			//    animation, sending the end-twin 0x31EB1501 + StatUpd(Mount=0) DismountChannelMs after its own ack
			//    (same capture, press frame 1231 -> ack 1253 -> end-twin 1449). This used to fire immediately here on
			//    the reading that frame 1449 was the response to the press; it is 5.15s after it, and clearing the
			//    mount stat at once drops the player to run speed for the whole animation.
			// Retail sends NO summon ApplySpell / no horse entity - the client animates it all. IsMounted lives on
			// the player WorldEntity (without the toggle the press re-mounts and the char sticks).
			// KNOWN COSMETIC (accepted): during the dismount the char briefly dips into the floor and re-grounds at
			// animation end. Confirmed NOT server-side - retail sends nothing extra during the dismount window (only
			// this end-twin + StatUpd), and both immediate and timer-deferred dismount show it. It is a client-side
			// ground/height recalculation on the mount->ground transition; a fix would need live client RE. #27.
			var mount = await _database.GetMount(from) ?? throw new InvalidOperationException($"SummonMount: unknown mount id 0x{from:X8} (not in the Mounts catalog)");
			var playerEntity = _simulation.PlayerWorldEntity(client);
			var dismounting = playerEntity?.IsMounted ?? false;
			_logger.LogInformation("SummonMount: from=0x{From:X} -> {Mount} ({Action}). {Args}", from, mount.Spell.Name, dismounting
				? "dismount"
				: "mount", args);

			var self = client.Session.Character.Id;
			await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId); // cast begin: bar + animation

			if (dismounting)
			{
				await CombatService.SendBehaviorChannelCompleteAck(client, rawPayload, 3);
				Task.RunAfter(MountCatalog.DismountChannelMs, async () => // horse runs away during the channel, then unseat
				{
					if (client.Session.Account == null)
						return;

					await _mounts.Dismount(client); // end-twin + StatUpd(Mount=0), frame 1449
				}, _logger);
				return;
			}

			var mountCastPayload = (byte[])rawPayload.Clone();
			Task.RunAfter(MountCatalog.SummonChannelMs, async () => // horse runs up during the channel, then mount
			{
				if (client.Session.Account == null)
					return;

				await CombatService.SendBehaviorChannelCompleteAck(client, mountCastPayload, 3); // cast complete: mounts (form 0x0a closes the bar)

				// The seat itself (#26): the Ride buff + the Mount/MountSpeed pair, broadcast to viewers because the
				// horse renders from those stats (#428), then persisted so it survives a relog / a no-mount trip.
				// Cleared only on a voluntary dismount above.
				await _mounts.SeatAndPersist(client, mount);
			}, _logger);
		}

		// Split out of HandleBehaviorControlCenter's action switch (#200) - a pure move; the case is now a one-liner.
		private async Task HandleBccUseDynel(INetworkClient client, ushort behaviorType, uint target)
		{
			// Using a world object - `target` is the object handle (its typetag, 0xC87A for loot bags,
			// rides in `from`). Dispatch on what the object DOES (its InteractAction, data-driven from
			// the DB): a zone-line teleports to another playfield; everything else (a SimpleDynel door
			// that opens, a loot bag) is handled by the open/loot path.
			var interaction = await _worldObjects.ResolveUseObject(client, target);

			// #521/#523: using this object ENDS any escort it is the destination of. Retail's order is exactly
			// this - both beach-to-city captures show the door use, then QuestGoal 12407/3544 1/1 and
			// QuestIIR Active->Completed, and only ~30 frames LATER Casilda's farewell window - so the goal is
			// completed here, before the teleport, rather than out of the conversation.
			//
			// Data-driven, not a hard-coded door: the escortee is whoever this object opens a server-initiated
			// conversation for (ConversationNode.TriggerWorldObjectId), because the object that plays the
			// escortee's farewell is by definition the place the escort was leading to. Costs one indexed
			// lookup on a door use and yields nothing for every ordinary door.
			var playedFarewell = false; // TODO: verify in retail how the farewall works
			if (interaction.WorldObjectId is { } usedObjectId && behaviorType == 0x0003)
			{
				// #527: using this object may BE an objective - "Unlock the Acheronian gate" is solved by using the
				// gate, not by walking up to it. Same begin-phase gate as the escort hand-off below, so a door-use's
				// release phase cannot tick it twice, and ahead of the escort/teleport work for the same reason retail
				// puts it there: in the capture the goal ticks before anything else the use causes.
				await _questProgression.AdvanceUseObjectGoals(client, usedObjectId);

				foreach (var escortee in await _database.GetServerInitiatedConversationNpcs(usedObjectId))
				{
					// Gate on the escort actually being IN PROGRESS. That is what separates the one door use
					// that ends it from every later use of the same door: the farewell plays once, and the zone
					// line behaves normally afterwards.
					if (!await _questProgression.HasActiveEscortGoal(client.Session.Character.Id, escortee))
						continue;

					await _questProgression.AdvanceEscortGoals(client, escortee);
					playedFarewell |= await _conversation.TryOpenTriggeredConversation(client, escortee, usedObjectId);
				}
			}

			// ⚠️ The use that plays the farewell does NOT teleport. Retail's does not either: in BOTH
			// beach-to-city captures the door-use begin phase is followed by the escort completing and the
			// conversation opening, and then 1,200+ further packets with no playfield handoff at all. The
			// farewell also opens with {hidegoodbye}, which pins the player until it is done - teleporting
			// them mid-dialogue would strand the window on a map the NPC is not on. The next use goes through.
			//
			// ⚠️ So the player enters Tortage on a SECOND use of the gate, which is the maintainer's recollection of
			// retail - and it is the one point where the captures disagree with it (#844, needs-testing). Both show
			// the teleport arriving ~0.5 s after Casilda's despawn with no second interaction between: I dumped every
			// c2s packet in that window and it is movement and pings only. Two sessions are not the rulebook - a
			// trigger volume the moving player stepped into would look identical on the wire - so this stays as
			// reported until an in-client check settles it. If the captures win, the fix is here: schedule the
			// zone-line teleport after the escortee's exit instead of returning.
			if (playedFarewell)
				return;

			switch (interaction.Action)
			{
				case InteractAction.Teleport:
				case InteractAction.ProxyTeleport:
				{
					// The client sends a door-use as TWO BCC phases (begin 0x0003 then release 0x0002); retail teleports
					// only on the begin phase (Hoxxling inn: the IntendedTeleport follows the 0x0003 record). Acting on
					// both double-fires the teleport - a redundant relocation / extra loading flash. Ignore the release.
					if (behaviorType != 0x0003)
						break;

					// Hand the player off to the destination playfield - or, if this teleporter's
					// destination hasn't been captured yet, surface a message (like the missing-vendor
					// case) rather than a silent no-op.
					var dest = interaction.Teleport;
					var currentMap = _simulation.PlayerWorldEntity(client)?.MapId ?? client.Session.Character.MapId;
					if (dest.DestMapId == 0)
					{
						await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
							ChatMarkup.Color("red", $"Zone-line '{dest.DestinationName}' has no known destination yet."));
					}
					else if (await _broadcaster.DescribeMissingClassImport(dest.DestMapId, client.Session.Character.Class) is { } missingImport)
					{
						// The destination is a CLASS-SPECIFIC playfield (an armory) we hold no capture of for this
						// character's class - report it rather than teleporting into an empty, possibly exit-less room.
						await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Color("red", missingImport));
					}
					else if (dest.DestMapId == currentMap && dest.ArrivalPosition is { } samePos)
						// Same-playfield door (e.g. the Thirsty Dog Inn): relocate in place, no loading screen.
						await _simulation.RelocateSameMap(client, samePos);
					else if (dest.AcgPlayfieldClass != 0)
						// #821: an INSTANCED destination (the Tortage rowboat to White Sands). Retail raises its own
						// bind box - "this instance will be bound to you" - instead of teleporting, and the trip only
						// runs when the player accepts, which lands in the RequestACGTeleport handler. Checked BEFORE
						// the #637 scry box: this destination has its own confirmation and must not get two.
						await _travelOutbound.SendAcgTeleportChoice(client, dest.DestMapId, dest.AcgPlayfieldClass, dest.ArrivalPosition);
					else if (await _database.GetMap(dest.DestMapId) is { TravelConfirmKey: { IsEmpty: false } } confirming)
						// #637: retail asks "Do you want to travel to <map>?" before entering this destination. The
						// trip resumes in HandleScryMessageBoxAnswer when the player confirms; a decline drops it.
						await _travelOutbound.SendTravelConfirmation(client, confirming, dest.ArrivalPosition);
					else
						await _teleport.DoTeleport(client.Session.Character.Id, dest.DestMapId, dest.ArrivalPosition);

					break;
				}

				case InteractAction.None:
				default:
				{
					// #262: a quest-item PICKUP (QuestItemEffect dynel, e.g. a quarry Stone Brick) - grant the
					// collect goal's item and play the node's harvest visual (the node STAYS, retail-style: the
					// same brick is harvested again seconds later in the corpus), but ONLY while a matching
					// collect quest is active
					// (ResolveCollectPickupItem returns null otherwise, so the brick is inert for a player
					// without Shipwrecked V). Handled here before the generic open path. The goal is advanced by
					// the GRANT, not here - GrantItemToBackpack ticks it, so a pickup counts exactly once.
					if (await _worldObjects.IsQuestPickup(client, target))
					{
						if (await _questProgression.ResolveCollectPickupItem(client) is { } pickedItem)
						{
							// ⚠️ The node is marked harvested only if something ACTUALLY arrived (#824). This used to
							// ignore the grant's answer, so a pickup whose item could not be delivered played the whole
							// animation - fade out, "it grows back in 14s", regrow - and put nothing in the bag, which
							// is precisely how #824 presented. Both reasons a grant can fail want the node LEFT
							// STANDING: a full backpack means try again after making room, and a template we do not
							// hold means this node can never pay out and should not pretend to.
							if (await _economyOutbound.GrantItemToBackpack(client, pickedItem) != null)
								await _worldObjects.MarkPickupHarvested(client, target);
							else
								await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "You cannot carry that.");
						}

						break; // inert (no active collect quest) still swallows the use - don't open a window on a brick
					}

					// An achievement VISTA (#780) takes the whole interaction: it plays its own gesture + camera
					// fly-through and must not also run the door open-animation. Beside the harvest below because it
					// is the same shape - a column-driven object whose use is a whole scripted sequence.
					// 0x0003 is the client's BEGIN phase, the same one the teleport branch above acts on - a use that
					// arrives twice must not start two scenes.
					if (behaviorType == 0x0003 && _worldObjects.VistaAt(client, target) is { } vista && await _vista.TryStartAchievementVista(client, vista))
						break;

					// A resource NODE (#494) is harvested, not opened: it yields a stack of its matching resource
					// and must not also play the door open-animation, so it takes the whole interaction.
					// Handled here, beside the quest-pickup above, because the item grant lives on the listener.
					if (_worldObjects.HarvestNodeAt(client, target) is { } node)
					{
						await _tradeskill.Harvest(client, node);
						break;
					}

					// Not a column-driven teleporter: a BENCH seats the player (the sit animation), else the
					// animated-door open path (DB world object by CapturedHandle), else the loot-bag window.
					if (await _worldObjects.TrySitOnObject(client, target))
						break;
					if (await _worldObjects.OpenWorldObject(client, target))
						break;
					if (await _lootBroadcaster.OpenLootBag(client, target))
						break;

					// Unknown object: no bench/door/loot behavior. Surface the gap the same way as an
					// un-imported vendor/dialogue NPC (notify the player, never a silent no-op).
					// Float the bubble over the clicked object so the client shows it in place.
					var usedEntity = _simulation.PlayerWorldEntity(client);
					var usedName = usedEntity != null && _simulation.World.ZoneForEntity(usedEntity).TryGetWorldObject(target, out var usedObj)
						? usedObj.Row.Name
						: null;

					await _agentServerDispatcher.CharacterSay(target, $"({usedName ?? "object"}: not imported yet)",
						[client.Session.Character.Id]); // TODO: Why this this also print when looting the item? check InteractAction value

					break;
				}
			}
		}

		// Split out of HandleBehaviorControlCenter's action switch (#200) - a pure move; the case is now a one-liner.
		private async Task HandleBccUseCharacter(INetworkClient client, byte[] rawPayload, CommandMessage args, uint from, uint target)
		{
			// The clicked NPC's entity handle rides in the interact args (field 10 / field 2);
			// (TypeId, Mode, Field3) select the conversation node the client is requesting.
			var npcHandle = args.Target?.Instance ?? 0;
			_logger.LogInformation("TalkToNpc: from=0x{From:X}, target=0x{Target:X}, npcHandle=0x{NpcHandle:X}, args={Args}", from, target, npcHandle, args);

			// 0x16 is the generic "interact with a character", so the TARGET decides what it means: an NPC
			// opens its conversation (below), a PLAYER is a TRADE REQUEST (#101, capture f1251 -> f1260).
			// A player's handle IS its character id, which is what makes the distinction this cheap.
			if (npcHandle != client.Session.Character.Id && await _database.GetCharacter(npcHandle) is { })
			{
				await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
				await _trade.Request(client.Session.Character.Id, npcHandle);
				return;
			}

			// Vendor trade-open (#134): when the clicked NPC is a shop vendor (matched by NAME - the
			// stable key), retail answers the interact itself with the trade-select echo + bare BCC
			// echo + DispatchShopData riding this conversation (sell_items_to_trader frames
			// 1188-1193) - no knubot screens in between. Checked on the greeting click only
			// (Field3 == 0, like the captured open).
			// Only a shop VENDOR takes this path: gate on an actual shop row existing for the clicked NPC's
			// TEMPLATE (#244). Keying by template, not name, is what stops the "Halen van Dummy" collision -
			// the vendor templates (19265, ...) get their shop, while the same-named conversation NPC Atraxus
			// (template 12236) does NOT, so his click falls through to dialogue below. A non-vendor (Casilda,
			// a quest NPC, an un-imported vendor) yields a null shop and falls through - do NOT break here, or
			// the greeting is swallowed and dialogue never opens.
			if (npcHandle != 0 && args.CommandqueueServerId == 0 && client.Session.SpawnedNpcTemplatesByHandle.TryGetValue(npcHandle, out var vendorTemplate) &&
			    await _database.GetShopByTemplate(vendorTemplate) is { } vendorShop)
			{
				await _shop.HandleVendorTradeOpen(client, rawPayload, args.CommandqueueClientId, vendorShop, npcHandle);
				return;
			}

			// Auction-house open (the real trigger for the Tradepost - the `.ah` GM command is only a PoC
			// bootstrap). Retail opens the AH window when the player interacts with an auctioneer NPC
			// (capture `sell_items_to_trader-intract-with-auctionhouse`: FA015807 "Trader"); the player is
			// standing next to the auctioneer to click it, so their own position trivially satisfies the
			// 15-unit client proximity check.
			//
			// Keyed by the client's own ROLE tag (#647), like the vendor branch above is keyed by template.
			// This used to match the display name "Trader", which was the wrong key twice over: the string is
			// a role FOUR templates play, so it pinned none of them, and any other NPC carrying it would have
			// opened the auction house. See TradepostService.IsAuctioneerTemplate.
			if (npcHandle != 0 && args.CommandqueueServerId == 0 && client.Session.SpawnedNpcTemplatesByHandle.TryGetValue(npcHandle, out var auctioneerTemplate) &&
			    await _tradepost.IsAuctioneerTemplate(auctioneerTemplate) && _simulation.PlayerWorldEntity(client) is { } auctioneerPlayer)
			{
				await _tradepost.SendAuctionHouseOpen(client, auctioneerPlayer.Position);
				return;
			}

			// Real branching dialogue: replay the captured conversation node for whichever NPC/node
			// the client asked for, re-addressed to the clicked NPC.
			if (await _conversation.TryReplayConversation(client, args.CommandqueueServerId, npcHandle))
				return;

			// Only a GREETING click (Field3 == 0) may OPEN a dialogue. The client fires a second
			// TalkToNpc with Field3 != 0 the same instant it opens one (retail answers it with a bare
			// ack); if that duplicate isn't absorbed above (no active conversation - e.g. right after a
			// close) it must NOT re-open the intro/greeting, or the dialogue "triggers a second time"
			// (#217). A non-greeting click with nothing to absorb is a no-op.
			if (args.CommandqueueServerId != 0)
				return;

			// Beach login with no captured node for this (Mode, Field3): close the chat window so the
			// client doesn't hang waiting for a reply (the "advance a step -> chat locks, can't ESC"
			// bug). Conversation replay is coverage-bound, so an un-captured branch is expected.
			if (client.Session.WorldEntered || client.Session.EntryMode == WorldEntryMode.CreateWorld)
			{
				var routedName = client.Session.SpawnedNpcNamesByHandle.TryGetValue(npcHandle, out var rn)
					? rn
					: "<unmapped handle>";
				_logger.LogInformation(
					"TalkToNpc: no conversation node for npcHandle=0x{NpcTemplate:X} (name={Name}, typeId={TypeId}, mode={Mode}, field3={Field3}); closing the window.", npcHandle,
					routedName, args.BccActionId, args.CommandqueueClientId, args.CommandqueueServerId);

				// Surface the gap instead of silently closing (have them say so). A greeting click
				// that reaches here has NEITHER an imported shop (the vendor branch above didn't match)
				// NOR an imported conversation node - and we can't tell a vendor-without-shop from a plain
				// talk NPC (no merchant flag on the NPC), so one honest bubble covers both. Sender = the NPC
				// handle so the client floats it over the NPC; recipient = the clicking player (SayRecipients
				// excludes the actor, which would drop the clicker here). No-fallbacks rule: say what's missing.
				if (npcHandle != 0)
					await _agentServerDispatcher.CharacterSay(npcHandle, $"({routedName}: no dialogue or shop imported yet)", [client.Session.Character.Id]);

				await ConversationService.SendConversationClose(client, npcHandle); // #217: close the CLICKED NPC's window (re-address the camera release)
			}
		}

		// Split out of HandleBehaviorControlCenter's action switch (#200) - a pure move; the case is now a one-liner.
		private async Task HandleBccEquip(INetworkClient client, byte[] rawPayload, CommandMessage args, uint from, uint target, byte[] actionTrailer)
		{
			// bag slot starts top left, continue counting on next page

			var direction = (MoveItemInInventoryDirection)args.BccActionId;
			switch (direction)
			{
				// Into-slot moves: tail = [srcBagCell][destEquipSlot][item][item][0][flags].
				// 42011 and 41843 are the same grammar via different windows (capture + live).
				case MoveItemInInventoryDirection.InventoryToEquipped:
				case MoveItemInInventoryDirection.BagToEquipped:
				{
					var targetSlot = (CharacterEquipmentSlot)target;

					_logger.LogInformation($"{direction}: ({from} -> {targetSlot}) {args}");

					// The trailing struct (after from/target) carries the item's template handle as a
					// big-endian uint32; persist the equip into CharacterEquipment. Read by name out of the
					// behaviour tail, which the codec carries opaquely (#536).
					var equipHandle = BehaviorTailScan.MovedItemHandle(actionTrailer);
					var movedItem = equipHandle != 0
						? await _database.GetItem(equipHandle)
						: null;
					ItemInstance? justBound = null;
					if (equipHandle != 0)
					{
						justBound = await _equipment.PersistEquip(client, targetSlot, equipHandle, (int)from);
						// What is WORN changed, so the live gear aggregate is stale - see RefreshGearBonuses.
						await _buffs.RefreshGearBonuses(client);
					}

					// Retail's reply: the behavior ack carrying the item's identity quad + a 0x201C
					// entry (prefix 0x65, state = the destination slot). Capture frames 289-290.
					await CombatService.SendEquipMoveAck(client, rawPayload, args.CommandqueueClientId, movedItem!);
					await WorldEntryService.Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), 0x00000065, target,
						0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

					// ...and, when this equip BOUND the item, the cell's Flags word again with bit 26 set (#756). This
					// is retail's own last message of the sequence:
					//   [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:47:00.842 - after the two
					//    0x17 slot registrations, equipped(101) slot 2 stat 0 = 0x04000001, where the same item had
					//    arrived in the bag at 0x00000001 twelve seconds earlier]
					if (justBound != null)
						await WorldEntryService.SendItemBound(client, targetSlot, justBound);

					break;
				}

				// Out-of-slot moves: tail = [srcEquipSlot][destBagCell][0...].
				case MoveItemInInventoryDirection.EquippedToInventory:
				case MoveItemInInventoryDirection.EquippedToBag:
				{
					var fromSlot = (CharacterEquipmentSlot)from;

					_logger.LogInformation($"{direction}: ({fromSlot} -> {target}) {args}");

					// Unequip is identified by the source equip slot (the trailing handle is 0);
					// `target` is the bag cell the item lands in.
					await _equipment.PersistUnequip(client, fromSlot, (int)target);
					await _buffs.RefreshGearBonuses(client);

					// Retail's reply: the behavior ack with null identity + a 0x201C entry
					// (prefix 0x68, state 0). Capture frames 320-321.
					await CombatService.SendEquipMoveAck(client, rawPayload, args.CommandqueueClientId, null!);
					await WorldEntryService.Send0X201CEntry(client, GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), 0x00000068, 0,
						0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

					break;
				}

				default:
				{
					_logger.LogInformation($"MoveEquippedToFromInventory: unknown direction={direction} {args}");

					break;
				}
			}
		}
	}
}
