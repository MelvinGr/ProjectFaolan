using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private async Task Handle0X2000(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// Snapshot the entire 0x2000 sub-payload before consuming it, so every
			// case below can dump the raw bytes for offline reverse-engineering.
			// This is the data starting at the 4-byte pbLength header. ToArray() returns the whole body window
			// regardless of the cursor, so it is safe to take after the shared reader arrives positioned at 0.
			var rawPayload = body.ToArray();

			var pbLength = body.ReadUInt32();
			var pbOpcode = body.ReadUInt32<GameServer0X2000Opcodes>();

			// The shared `body` is now positioned PAST the [pbLength][pbOpcode] prefix. The sub-handlers that continue
			// reading (ScryScriptIIR -> HandleCommandPacket, the create-editor instance echo, and CoreClaim) read from
			// this position directly - the streaming cursor that the old ConanPacket.Body shared and that the removed
			// `subPacket` slice used to reconstruct (#407).

			// During the create-editor handshake the client echoes its own instance id as the 0x2000 subopcode.
			// Retail used a fixed id (0x021662C8, or Strie's 0x02165EFF); we hand out the character's real id, so
			// the subopcode is dynamic - match it against the session instance and route it to the echo handler.
			if (client.Session.IsUnnamedCreatePhase && client.Session.Account?.ClientInstance == (uint)pbOpcode)
			{
				await HandleCreateEditorInstanceEcho(client, body);
				return;
			}

			switch (pbOpcode)
			{
				case GameServer0X2000Opcodes.CombatReactiveIIR:
				{
					// Spellweave ADD-COMPONENT (docs/subsystems/combos.md). Each add is two packets: a 0x03-tagged carrier that
					// carries the componentIndex, and a 0x02-tagged ack twin (no index). rawPayload =
					// [u32 len][u32 sub][u32 senderType][u32 handle][u32 0][u8 tag][u32 componentIndex?].
					if (rawPayload.Length >= 25 && rawPayload[20] == 0x03)
					{
						var componentIndex = BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(21, 4));
						await _spellweave.AddComponentAsync(client, componentIndex);
					}

					break;
				}

				case GameServer0X2000Opcodes.StatIIR:
				{
					// Create-editor (unnamed) avatar stat stream: retail sends no response, we just observe it.
					if (client.Session.IsUnnamedCreatePhase)
					{
						Logger.LogInformation("Observed retail create-editor (uint)GameServer0X2000Opcodes.StatIIR while pendingPost205F={PendingPost205F}.",
							client.Session.HasPendingPost205FFollowUp);
					}

					break;
				}

				case GameServer0X2000Opcodes.ScryScriptIIR:
				{
					await _createFlow.HandleCommandPacket(client, packet, body);
					break;
				}

				case GameServer0X2000Opcodes.CharDCMoveIIR:
				{
					body.ReadUInt32(); // 0xC350 IIR marker
					body.ReadUInt32(); // the mover's handle - we key off the session, not the client's claim

					// [u8 env][u8 flag1] - a LAYOUT BITFIELD, not a movement-type enum: every value is legal, so there
					// is nothing to switch on (docs/re/chardcmove-grammar.md; switching dropped 24% of moves).
					var movingType = body.ReadUInt16();
					var moveFlags = (MoveFlag)(movingType & 0xFF);

					var sessionCharacter = client.Session.Character!;

					var movePosition = body.ReadVector3();

					// The floats after the position are the entity's rotation QUATERNION; bit4 says how many are on the
					// wire - set (most moves) sends only the (qy, qw) an upright yaw-only facing needs, clear sends all
					// four. Reading them as Euler degrees pinned every character's facing at ~identity.
					var moveRotation = moveFlags.HasFlag(MoveFlag.CollapsedRotation)
						? new Quaternion(0f, body.ReadFloat(), 0f, body.ReadFloat())
						: body.ReadQuaternion();

					// The WorldEntity owns the live transform (Gitea #404); the persisted Character is only the spawn
					// seed, so fall back to it only for a player not in the world model yet.
					var moveEntity = PlayerWorldEntity(client);

					// The pre-move position, so a real TRANSLATION below can interrupt an in-flight cast (#347).
					var previousPosition = moveEntity?.Position ?? sessionCharacter.Position; // TODO: Is this fallback needed??

					// FALLBACK only: a seated player who leaves the seat WITHOUT asking (a teleport, or a client that
					// skips the request) still gets stood up - the normal path is the client's own standup request
					// (behaviour 1, in the BCC branch). Gated on distance, because a seated client streams position
					// syncs that must not stand it up.
					await _broadcaster.StandUpIfMoved(client, movePosition);

					// Trance root (#141): the spellweave trance immobilises the caster, so while IsSpellweaving the
					// server PINS the position (rejects translation) but still accepts rotation - the move handler is
					// the enforcement point. Others then see the caster rooted; the relay below carries the pinned pos.
					var acceptedPosition = client.Session.IsSpellweaving
						? moveEntity?.Position ?? sessionCharacter.Position // TODO: Is this fallback needed??
						: movePosition;

					if (moveEntity != null)
					{
						moveEntity.Position = acceptedPosition;
						moveEntity.Rotation = moveRotation;
					}
					else
					{
						sessionCharacter.Position = acceptedPosition;
						sessionCharacter.Rotation = moveRotation;
					}

					var livePosition = moveEntity?.Position ?? sessionCharacter.Position; // TODO: Is this fallback needed??

					// #262: a move can satisfy a ReachArea quest goal (e.g. Shipwrecked's "Enter the jungle") - solve
					// any whose area this position falls in. In-memory distance test (targets cached on the entity).
					if (moveEntity != null)
						await CheckPlayerReachGoals(client, moveEntity, livePosition);

					// Moving cancels an in-flight cast (#347): a real TRANSLATION invalidates the deferred Resolve()
					// via the cast token. Turning in place leaves the position unchanged, so aiming a ground-target
					// reticle never self-cancels, and a pinned spellweave (delta 0) can't cancel its own trance. The
					// cast BAR is the client's own business - it sends the cancel BCC handled above.
					if (Vector3.DistanceSquared(previousPosition, livePosition) > CastMoveCancelEpsilonSq)
						client.Session.ActiveCastToken++;

					// NB moves are echoed even while SEATED, because retail does: in the sit capture the seated player's
					// c2s CharDCMove is answered with an s2c one (pkt 6812 -> 6815, 6841 -> 6842) and the standup
					// follows. This used to be suppressed on the observation that echoing "cancels the sit pose" -
					// true at the time, but the cause was the sit records themselves being REJECTED by the client
					// (the hold-assert's remoteformat length prefix, c6e015f3, and the over-padded ack tail,
					// 063a5a83), so the pose never committed and any move looked like it cancelled the sit.
					//
					// Suppressing it also deadlocked standing up: standup needs the player to have moved
					// SeatLeaveDistance from the seat, but with no echo the client got no answer to its first move
					// and stopped sending any, so the threshold could never be reached and only the client's own
					// local timeout got the player up. (It also skipped BroadcastPlayerMove entirely, so world
					// objects did not stream while seated either.)
					// Echo the move back VERBATIM with only the env byte cleared - retail's s2c CharDCMove IS the
					// c2s move with its env byte flipped 0x01 -> 0x00 (same length, same body), NOT a reconstruction.
					// Rebuilding it from the parsed pos/rot (round-tripping the rotation through a quaternion)
					// produced a body the client's CharDCMoveIIR reader REJECTS ("ReadSubClass failed ... Blob data
					// may be inconsistant" in the client log), so it fell back to prediction -> the movement jitter
					// (#272). While spellweaving the position is PINNED (trance root #141) - the client does NOT self-root
					// (verified: without this overwrite you can walk during a weave), so overwrite the echoed position
					// floats with the accepted (pinned) position; otherwise the client's own move rides back unchanged.
					var echoBody = rawPayload[4..]; // drop the u32 pbLength: [opcode][c350][inst][env][flag1][pos][...]

					// The movingType u16 is [u8 env][u8 flag1] (docs/re/chardcmove-grammar.md): the client streams
					// its move with env=0x01 (the "I am the controlling client" flag) and retail's echo AND relay
					// both carry env=0x00 - verified byte-exact in the other-players-visible capture, where the c2s
					// move and its s2c twin differ ONLY in this byte (c2s 0x011e -> s2c 0x001e). Forwarding the
					// client's 0x01 verbatim made OTHER viewers treat B's move as locally-controlled and
					// over-predict it from the velocity block, so B drifted/teleported even while standing still
					// (#48). flag1 (the layout bitfield, jump bit included) rides through untouched.
					echoBody[12] = 0x00;
					if (client.Session.IsSpellweaving && echoBody.Length >= 26)
					{
						BinaryPrimitives.WriteSingleBigEndian(echoBody.AsSpan(14, 4), acceptedPosition.X);
						BinaryPrimitives.WriteSingleBigEndian(echoBody.AsSpan(18, 4), acceptedPosition.Y);
						BinaryPrimitives.WriteSingleBigEndian(echoBody.AsSpan(22, 4), acceptedPosition.Z);
					}

					await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
						s => s.WriteArrayUInt32Length(echoBody)));

					// Interest-managed broadcast: spawn this player to newly-near players and forward the move to those
					// who already see them (Gitea #404), carrying the same body the echo just did. No-op solo.
					await _broadcaster.BroadcastPlayerMove(client, echoBody, livePosition);

					break;
				}

				case GameServer0X2000Opcodes.CharInPlayIIR: //peer0_160
				{
					// CharInPlayIIR ("ReadyForPlayScreen") has TWO senders, told apart by the connection role:
					//  - the create-editor (unnamed) connection sends it to launch the editor. Retail answers DIRECTLY
					//    with the post-ready burst - the 0x2024 ReadyForPlayScreen + CharInPlayIIR + 0xF508F4C1 + the
					//    creation script that launches the editor (Blablatest necro create: client CharInPlayIIR at f90
					//    -> server burst at f95).
					//  - a NAMED character on a changemap/teleport reconnect sends it too: that reconnect carries NO
					//    0x205F, so the client uses CharInPlayIIR as its ready signal instead of the 0x2020 poll the
					//    normal login flushes on. Treating it as an editor launch put the create-editor UI in-world
					//    after a teleport (and on any login that came in via this reconnect shape). So flush the armed
					//    post-0x205F follow-up here - the same ReadyForPlayScreen (+ abilities/combos) burst the 0x2020
					//    path sends - which drops the loading screen into the world. (This restores the named-character
					//    branch dropped in d43dd426 as "dead"; it isn't - the teleport reconnect exercises it.)
					if (client.Session.IsCreateEditorConnection)
					{
						Logger.LogInformation("Create-editor: CharInPlayIIR received; sending the retail post-ready burst to launch the editor.");
						await CreateFlowService.SendRetailCreateEditorPostReadyBurst(client, client.Session.CreateEditorObservedRequestIds);
					}
					else
					{
						Logger.LogInformation("Named-character CharInPlayIIR (changemap/teleport reconnect, no 0x205F): flushing the world post-0x205F follow-up.");
						await FlushPendingPost205FFollowUp(client);
					}

					break;
				}

				case GameServer0X2000Opcodes.KnubotAnswerIIR:
				{
					// The player picked a dialogue option. The chosen option INDEX is the last u32 of the body; it
					// selects the branch, so route to the child screen at the extended answer-path (AdvanceConversation).
					// No active conversation -> no-op.
					await _conversation.AdvanceConversation(client, KnubotCodec.ParseAnswerIndex(rawPayload));

					break;
				}

				case GameServer0X2000Opcodes.KnubotCloseChatWindowIIR:
				{
					// The player closed the NPC chat window (ESC/X). The client signals this with its OWN
					// KnubotCloseChatWindow (the client double-sends it); retail just accepts it. We must tear down the
					// conversation session state so the dialogue stays closed - leaving it set let the next interaction
					// re-enter a stale conversation, and the unknown-opcode default path also spammed a system message
					// on every close (#217). No reply: the window is already closed on the client.
					ConversationService.EndConversation(client);
					break;
				}

				case GameServer0X2000Opcodes.BehaviorControlCenterIIR:
				{
					// Important capture observation:
					// world-object interactions appear to be the likely trigger path for the container-style
					// response family 0xF096251E, which carries names like "Chest [N7FU]" and "object_closed".
					// Representative samples split into a small object/position block, a 125-byte metadata block, 					// and a variable nested entry block that currently decodes as 2, 3, or 4 repeated entries.
					// New random-actions evidence suggests the player bag is more likely part of the giant 0x737A6DF9
					// initial-state dump, while 0xF096251E still looks like remote chest/container data.
					var unk0 = body.ReadUInt32();
					var clientInst = body.ReadUInt32();
					var unk1 = body.ReadUInt16();
					// Some Interact variants are short and carry NO actionType/args (observed: a 10-byte trailer ending
					// `00 08` the client sends around loot/kill). Reading actionType unconditionally threw EndOfStreamException, 					// which ReceivedPacket turns into a hard client.Stop() (disconnect). Bail gracefully on a short body.
					if (body.Length - body.Position < sizeof(uint))
					{
						Logger.LogDebug("Interact: short body (unk1=0x{Unk1:X4}), no actionType - ignoring.", unk1);
						break;
					}

					var actionType = body.ReadUInt32<ActionType>();
					var args = body.ReadProtobufUInt32Length<CommandMessage>();

					if (actionType == ActionType.SwitchWeaponsButton)
					{
						// #400: SwitchWeaponsButton is the weapon-SET switch (toggles the two swappable loadouts), NOT a draw/sheathe -
						// capture-confirmed: a character wearing both sets pressed it 3x and the newly-active weapon main slot alternated
						// 6->20->6 (Stihetfv armed-and-unarmed). Echo-ack to self, toggle+persist the active set, then RELAY the swap
						// behavior to nearby viewers so they re-render the drawn weapons (the cross-player render channel is the BCC
						// command replication - Ghidra FUN_005e1c70 - the same path the combat swing/cast relay below uses; the
						// SetItemStat retail also sends is owner-only inventory bookkeeping, so it is not broadcast). NEEDS in-client verification.
						await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
						var activeSet = await _broadcaster.TogglePlayerWeaponSet(client);

						var swapRelay = BehaviorAckBody.BuildBehaviorRelayBody(client.Session.Account!.ClientInstance, rawPayload, args.CommandqueueClientId);
						if (swapRelay != null)
							await _broadcaster.BroadcastBehaviorToViewers(client, swapRelay);

						Logger.LogInformation("SwitchWeaponsButton -> weapon set {Set}. {Args}", activeSet, args);
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
						var unk2 = body.ReadRemainingArray();

						switch (actionType)
						{
							case ActionType.SummonMount:
							{
								// SummonMount TOGGLES (pressing the same button while mounted dismounts) and the two directions time
								// DIFFERENTLY:
								//  - MOUNT is a SERVER-timed channel: the client shows the cast bar + horse-runs-up, and retail sends
								//    the ride buff 545ms later (SummonChannelMs; capture frame 498->520). So we begin-echo-ack, then
								//    after the channel complete-echo-ack + ride buff 0x31EB1500 + StatUpd(Mount=id, MountSpeed=100).
								//  - DISMOUNT is CLIENT-driven: the client runs its OWN (longer) dismount channel + horse-runs-away, so
								//    the server responds IMMEDIATELY with the end-twin 0x31EB1501 (empty payload) + StatUpd(Mount=0)
								//    (capture frame 1449) rather than holding the mount state on a timer.
								// Retail sends NO summon ApplySpell / no horse entity - the client animates it all. IsMounted lives on
								// the player WorldEntity (without the toggle the press re-mounts and the char sticks).
								// KNOWN COSMETIC (accepted): during the dismount the char briefly dips into the floor and re-grounds at
								// animation end. Confirmed NOT server-side - retail sends nothing extra during the dismount window (only
								// this end-twin + StatUpd), and both immediate and timer-deferred dismount show it. It is a client-side
								// ground/height recalculation on the mount->ground transition; a fix would need live client RE. #27.
								var mount = MountCatalog.ForId(from) ?? throw new InvalidOperationException($"SummonMount: unknown mount id 0x{from:X8} (not in MountCatalog)");
								var playerEntity = PlayerWorldEntity(client);
								var dismounting = playerEntity?.IsMounted ?? false;
								Logger.LogInformation("SummonMount: from=0x{From:X} -> {Mount} ({Action}). {Args}", from, mount.CreatureName, dismounting
									? "dismount"
									: "mount", args);

								var self = client.Session.Character.Id;
								await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId); // cast begin: bar + animation

								if (dismounting)
								{
									// Immediate: don't hold the mount state during the client's dismount channel.
									var dismountAck = (byte[])rawPayload.Clone();
									if (dismountAck.Length > 17)
									{
										dismountAck[16] = 0x00;
										dismountAck[17] = 0x09;
									}

									await CombatService.SendBehaviorEchoAck(client, dismountAck, 3);
									await ApplySpell(client, self, self, MountCatalog.DismountSpell); // end-twin removes the ride buff
									var dismountStats = new CharacterStatBlock
									{
										[CharacterStatId.Mount] = 0 // clear the mount stat (frame 1449)
									};
									await SendStatUpdate(client, dismountStats, self);
									await _broadcaster.BroadcastStatsToViewers(client, dismountStats); // #428: the horse renders from THIS, so viewers need it too
									playerEntity?.IsMounted = false;
									playerEntity?.MountRideStat = 0;

									// Voluntary dismount -> forget the mount so it isn't restored on the next world entry.
									client.Session.Character.MountId = null;
									await Database.SaveCharacterMount(client.Session.Character.Id, null);
									break;
								}

								var mountCastPayload = (byte[])rawPayload.Clone();
								Task.RunAfter(MountCatalog.SummonChannelMs, async () => // horse runs up during the channel, then mount
								{
									if (client.Session.Account == null)
										return;

									if (mountCastPayload.Length > 17)
									{
										mountCastPayload[16] = 0x00;
										mountCastPayload[17] = 0x09; // form-0x0a cast-complete (closes the bar)
									}

									await CombatService.SendBehaviorEchoAck(client, mountCastPayload, 3); // cast complete: mounts
									await SendMagicBuffIir(client, self, self, MountCatalog.RideSpell, MountCatalog.RideBuffPayload()); // seat (#26)
									var mountStats = new CharacterStatBlock
									{
										[CharacterStatId.Mount] = mount.RideStat,
										[CharacterStatId.MountSpeed] = 100
									}; // the mount StatUpd (frame 520)
									await SendStatUpdate(client, mountStats, self);
									await _broadcaster.BroadcastStatsToViewers(client, mountStats); // #428: the horse renders from THIS, so viewers need it too
									playerEntity?.IsMounted = true;
									playerEntity?.MountRideStat = mount.RideStat; // so a viewer arriving LATER can be told (#428)

									// Persist the mount so it survives a relog / a trip through a no-mount area (restored on
									// world entry to a mount-allowed map). Cleared only on a voluntary dismount above.
									client.Session.Character.MountId = mount.Id;
									await Database.SaveCharacterMount(client.Session.Character.Id, mount.Id);
								}, Logger);

								break;
							}

							case ActionType.RightClickItem:
							{
								Logger.LogInformation("RightClickItem: container={Container} slot={Slot} {Args}", (ContainerKind)from, target, args);

								// Every behavior gets an echo-ack (docs/subsystems/combat.md rule 1); the item-use domain logic
								// lives in UseInventoryItem (GameServerListener.ItemUse.cs). The used item is identified by its inventory location - from =
								// container (0x68 = backpack), target = slot - decoded from the use_totem_of_origin
								// capture (using a Totem of Origin recalls to the bind point, #206; the recall's handoff
								// is the capture's "long loading screen").
								await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
								await UseInventoryItem(client, (ContainerKind)from, (int)target);

								break;
							}

							case ActionType.CastSpell:
							{
								// The BCC "state" word (unk1) tells begin from cancel: 3 = begin, 2 = CANCEL. The client sends
								// the cancel when the cast is interrupted (moving mid-cast - "move cancels the cast", #346/#347),
								// so honor it by invalidating any in-flight resolve (bump the token so the deferred Resolve below
								// aborts) and acking the cancel. State values are capture-derived (actions-described-in-chat:
								// begin=3 x2, cancel=2 x1; every cast in the ice-strike/casilda/casting captures is begin=3).
								const ushort castStateCancel = 2;
								if (unk1 == castStateCancel)
								{
									client.Session.ActiveCastToken++; // a pending Resolve() now sees a mismatched token and aborts
									var beginArgs = client.Session.PendingCastArgs;
									client.Session.PendingCastArgs = null!;

									// Retail answers the cancel with a state=0x0007 abort BCC (NOT an echo of the cancel) - that
									// is what drops the cast bar, cancels the animation and pops the client's "Interrupted" text
									// (#347, cast-interupt idx1013). The old echo of the cancel payload left the bar running. Needs
									// the stored BEGIN args; fall back to the plain echo if it's missing.
									if (beginArgs != null)
										await CombatService.SendCastInterruptAbort(client, beginArgs, rawPayload, client.Session.PendingCastMode);
									else
										await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
									Logger.LogInformation("CastSpell CANCEL (interrupted/moved) - abort ack sent, cast bar cleared.");
									break;
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
								var isGroundCast = CastGroundTarget.TryParse(unk2, out var groundTarget) && args.Target == null;

								// This cast's invalidation token: a later cancel (or a new cast) bumps ActiveCastToken so the
								// deferred Resolve below aborts if the cast was interrupted before its cast time elapsed.
								var castToken = ++client.Session.ActiveCastToken;

								Logger.LogInformation($"CastSpell spell=0x{spellId:X} target=0x{targetHandle:X}{(isGroundCast ? $" ground={groundTarget}" : "")} {args}");

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
									await BreakShieldOnAggression(client);

								// Honor the spell's cast time (#236): retail resolves the effect only AFTER the cast time
								// elapses (ice-strike capture: cast at frame 377, ApplySpell impact ~frame 441). Resolving on
								// receipt skipped the client's cast bar. CastTime 0 (instant spell, or an un-populated row)
								// resolves immediately. Movement mid-cast interrupts it (the cancel branch above / the move
								// handler's token bump); the abort BCC drops the client bar (#347).
								var spell = await Database.GetSpell(spellId);
								var castMs = spell?.CastTime ?? 0;
								if (castMs == 0)
									await Resolve();
								else
								{
									// Hold the begin's args (only) so the cast-cancel handler can build the state=0x0007 abort that
									// stops the client's cast bar (#347). The rest of the abort comes from the cancel packet.
									// Cleared on resolve/cancel.
									var castArgsLen = (int)BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(22, 4)); // TODO: just read the packet with FaolanStream instead of rawPayload
									client.Session.PendingCastArgs = rawPayload[26..(26 + castArgsLen)];
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
									}, Logger);
								}

								break;

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

									// Cast-complete (#236): the cast bar is the form-0x0a cast behavior (retail BEGIN2/completion BCC, 									// ice-strike idx481/534), not the form-4 begin. The echo-ack derives its form from the packet's
									// seq word (form = seq+1), so to emit form 0x0a we stamp seq=9 onto a clone, with phase=3
									// (cast-state complete). Without this the bar fills then hangs.
									var completePayload = (byte[])rawPayload.Clone();
									if (completePayload.Length > 17)
									{
										completePayload[16] = 0x00;
										completePayload[17] = 0x09;
									}

									await CombatService.SendBehaviorEchoAck(client, completePayload, 3);

									// #307: a PET-SUMMON spell spawns the pet (creature + PetIIR owner-assign + follow) instead
									// of applying a target effect - this is what hooks pet summoning to real spell casts, not
									// just the .pet GM command. Decoded from the necro-pet capture (docs/subsystems/summon-pet-decode.md):
									// cast (CastSpell) -> after the channel -> FA015807 pet spawn + PetIIR. PetSummonCatalog maps
									// the summon spell id -> pet template + kind.
									if (PetSummonCatalog.ForSpell(spellId) is { } petSummon)
									{
										var pet = await SummonPetForPlayer(client, petSummon.PetTemplate, petSummon.Combat);
										Logger.LogInformation("CastSpell 0x{Spell:X} pet-summon '{Template}' -> {Result}.", spellId, petSummon.PetTemplate, pet != null
											? $"0x{pet.Handle:X8}"
											: "no spawnable template (pet-creature import needed)");
										return;
									}

									// GROUND-TARGETED AoE: damage attackable NPCs around the reticle instead of applying a single
									// target debuff or a self-buff. The blizzard/AoE VFX is rendered client-side from the cast
									// behavior itself (retail sent no ApplySpell on the empty-ground casts - only the complete-ack,
									// the DispatchLockTemplate unlocks, and the mana push), so there is no ApplySpell carrier here.
									if (isGroundCast)
									{
										await _combat.SpendCasterMana(client, spellId);
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
									await ApplySpell(client, targetHandle == 0
										? casterHandle
										: targetHandle, casterHandle, spellId << 8);
									await _combat.SpendCasterMana(client, spellId);
									if (targetHandle != 0 && targetHandle != casterHandle)
										await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId);
								}
							}

							case ActionType.NormalAttack:
							{
								// The melee swing / auto-attack: ack the behavior + resolve the hit (see
								// SendNormalAttackResponse). The target is in the request args (SpellTarget.TargetHandle).
								await BreakShieldOnAggression(client); // swinging a weapon is aggressive - drops the shield (#361)
								await _combat.SendNormalAttackResponse(client, rawPayload, args.Target?.Instance ?? 0, args.CommandqueueClientId);

								break;
							}

							case ActionType.UnarmedAttack:
							{
								// Unarmed swing (empty main hand; behavior TypeIds 1089xx, ability id in the tail).
								// Retail's reply is the plain behavior echo-ack - no result block (combat capture
								// 2026-06-12); targeted damage reuses the armed path (the capture's unarmed swings
								// hit air, so the damage part is an extension pending in-client validation).
								await BreakShieldOnAggression(client); // an unarmed swing is aggressive - drops the shield (#361)
								await _combat.SendUnarmedAttackResponse(client, rawPayload, args.CommandqueueClientId, args.Target?.Instance ?? 0);

								break;
							}

							case ActionType.Emote:
							{
								Logger.LogInformation($"Emote: {args}");

								break;
							}

							case ActionType.UseObject:
							{
								// Using a world object - `target` is the object handle (its typetag, 0xC87A for loot bags,
								// rides in `from`). Dispatch on what the object DOES (its InteractAction, data-driven from
								// the DB): a zone-line teleports to another playfield; everything else (a SimpleDynel door
								// that opens, a loot bag) is handled by the open/loot path.
								var interaction = await _broadcaster.ResolveUseObject(client, target);
								switch (interaction.Action)
								{
									case InteractAction.Teleport:
									case InteractAction.ProxyTeleport:
									{
										// The client sends a door-use as TWO BCC phases (begin 0x0003 then release 0x0002); retail teleports
										// only on the begin phase (Hoxxling inn: the IntendedTeleport follows the 0x0003 record). Acting on
										// both double-fires the teleport - a redundant relocation / extra loading flash. Ignore the release.
										if (unk1 != 0x0003)
											break;

										// Hand the player off to the destination playfield - or, if this teleporter's
										// destination hasn't been captured yet, surface a message (like the missing-vendor
										// case) rather than a silent no-op.
										var dest = interaction.Teleport;
										var currentMap = PlayerWorldEntity(client)?.MapId ?? client.Session.Character.MapId;
										if (dest.DestMapId == 0)
										{
											await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
												$"<span style=\"color: red;\">Zone-line '{dest.DestinationName}' has no known destination yet.</span>");
										}
										else if (dest.DestMapId == currentMap && dest.ArrivalPosition is { } samePos)
											// Same-playfield door (e.g. the Thirsty Dog Inn): relocate in place, no loading screen.
											await _simulation.RelocateSameMap(client, samePos);
										else
											await DoTeleport(client.Session.Character.Id, dest.DestMapId, dest.ArrivalPosition); // TODO: Move DoTeleport into _simulation like RelocateSameMap

										break;
									}

									case InteractAction.None:
									default:
									{
										// #262: a quest-item PICKUP (QuestItemEffect dynel, e.g. a quarry Stone Brick) - grant the
										// collect goal's item + advance it + vanish the node, but ONLY while a matching collect quest
										// is active (TryAdvanceCollectPickup returns null otherwise, so the brick is inert for a player
										// without Shipwrecked V). Handled here before the generic open path.
										if (await _broadcaster.IsQuestPickup(client, target))
										{
											if (await _questProgression.TryAdvanceCollectPickup(client) is { } pickedItem)
											{
												await GrantItemToBackpack(client, pickedItem);
												await WorldBroadcaster.HidePickupObject(client, target);
											}

											break; // inert (no active collect quest) still swallows the use - don't open a window on a brick
										}

										// Not a column-driven teleporter: a BENCH seats the player (the sit animation), else the
										// animated-door open path (DB world object by CapturedHandle), else the loot-bag window.
										if (await _broadcaster.TrySitOnObject(client, target))
											break;
										if (await _broadcaster.OpenWorldObject(client, target))
											break;
										if (await _broadcaster.OpenLootBag(client, target))
											break;

										// Unknown object: no bench/door/loot behavior. Surface the gap the same way as an
										// un-imported vendor/dialogue NPC (notify the player, never a silent no-op).
										// Float the bubble over the clicked object so the client shows it in place.
										var usedEntity = PlayerWorldEntity(client);
										var usedName = usedEntity != null && _simulation.World.ZoneForEntity(usedEntity).TryGetWorldObject(target, out var usedObj)
											? usedObj.Row.Name
											: null;

										await _agentServerDispatcher.CharacterSay(target, $"({usedName ?? "object"}: not imported yet)",
											[client.Session.Character.Id]); // TODO: Why this this also print when looting the item? check InteractAction value

										break;
									}
								}

								break;
							}

							case ActionType.InteractWithCharacter:
							{
								// The clicked NPC's entity handle rides in the interact args (field 10 / field 2);
								// (TypeId, Mode, Field3) select the conversation node the client is requesting.
								var npcHandle = args.Target?.Instance ?? 0;
								Logger.LogInformation("TalkToNpc: from=0x{From:X}, target=0x{Target:X}, npcHandle=0x{NpcHandle:X}, args={Args}", from, target, npcHandle, args);

								// 0x16 is the generic "interact with a character", so the TARGET decides what it means: an NPC
								// opens its conversation (below), a PLAYER is a TRADE REQUEST (#101, capture f1251 -> f1260).
								// A player's handle IS its character id, which is what makes the distinction this cheap.
								if (npcHandle != client.Session.Character.Id && await Database.GetCharacter(npcHandle) is { })
								{
									await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);
									await _trade.Request(client.Session.Character.Id, npcHandle);
									break;
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
								    await Database.GetShopByTemplate(vendorTemplate) is { } vendorShop)
								{
									await _shop.HandleVendorTradeOpen(client, rawPayload, args.CommandqueueClientId, vendorShop, npcHandle);
									break;
								}

								// Auction-house open (the real trigger for the Tradepost - the `.ah` GM command is only a PoC
								// bootstrap). Retail opens the AH window when the player interacts with the "Trader" auctioneer
								// NPC (capture `sell_items_to_trader-intract-with-auctionhouse`: FA015807 "Trader"). The NPC NAME
								// is the stable key (like shop vendors above); the player is standing next to the auctioneer to
								// click it, so their own position trivially satisfies the 15-unit client proximity check.
								if (npcHandle != 0 && args.CommandqueueServerId == 0 && client.Session.SpawnedNpcNamesByHandle.TryGetValue(npcHandle, out var talkedNpcName) &&
								    talkedNpcName == AuctioneerNpcName && PlayerWorldEntity(client) is { } auctioneerPlayer)
								{
									await SendAuctionHouseOpen(client, auctioneerPlayer.Position);
									break;
								}

								// Real branching dialogue: replay the captured conversation node for whichever NPC/node
								// the client asked for, re-addressed to the clicked NPC.
								if (await _conversation.TryReplayConversation(client, args.BccActionId, args.CommandqueueServerId, npcHandle))
									break;

								// Only a GREETING click (Field3 == 0) may OPEN a dialogue. The client fires a second
								// TalkToNpc with Field3 != 0 the same instant it opens one (retail answers it with a bare
								// ack); if that duplicate isn't absorbed above (no active conversation - e.g. right after a
								// close) it must NOT re-open the intro/greeting, or the dialogue "triggers a second time"
								// (#217). A non-greeting click with nothing to absorb is a no-op.
								if (args.CommandqueueServerId != 0)
									break;

								// Beach login with no captured node for this (Mode, Field3): close the chat window so the
								// client doesn't hang waiting for a reply (the "advance a step -> chat locks, can't ESC"
								// bug). Conversation replay is coverage-bound, so an un-captured branch is expected.
								if (client.Session.WorldEntered || client.Session.IsCreateWorldSession)
								{
									var routedName = client.Session.SpawnedNpcNamesByHandle.TryGetValue(npcHandle, out var rn)
										? rn
										: "<unmapped handle>";
									Logger.LogInformation(
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

								break;
							}

							case ActionType.MoveEquippedToFromInventory:
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

										Logger.LogInformation($"{direction}: ({from} -> {targetSlot}) {args}");

										// The trailing struct (after from/target) carries the item's template handle as a
										// big-endian uint32; persist the equip into CharacterEquipment.
										var equipHandle = unk2.Length >= 4
											? BinaryPrimitives.ReadUInt32BigEndian(unk2.AsSpan(0, 4))
											: 0;
										var movedItem = equipHandle != 0
											? await Database.GetItem(equipHandle)
											: null;
										if (equipHandle != 0)
											await _equipment.PersistEquip(client, targetSlot, equipHandle, (int)from);

										// Retail's reply: the behavior ack carrying the item's identity quad + a 0x201C
										// entry (prefix 0x65, state = the destination slot). Capture frames 289-290.
										await CombatService.SendEquipMoveAck(client, rawPayload, args.CommandqueueClientId, movedItem!);
										await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), 0x00000065, target,
											0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

										break;
									}

									// Out-of-slot moves: tail = [srcEquipSlot][destBagCell][0...].
									case MoveItemInInventoryDirection.EquippedToInventory:
									case MoveItemInInventoryDirection.EquippedToBag:
									{
										var fromSlot = (CharacterEquipmentSlot)from;

										Logger.LogInformation($"{direction}: ({fromSlot} -> {target}) {args}");

										// Unequip is identified by the source equip slot (the trailing handle is 0);
										// `target` is the bag cell the item lands in.
										await _equipment.PersistUnequip(client, fromSlot, (int)target);

										// Retail's reply: the behavior ack with null identity + a 0x201C entry
										// (prefix 0x68, state 0). Capture frames 320-321.
										await CombatService.SendEquipMoveAck(client, rawPayload, args.CommandqueueClientId, null!);
										await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), 0x00000068, 0,
											0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

										break;
									}

									default:
									{
										Logger.LogInformation($"MoveEquippedToFromInventory: unknown direction={direction} {args}");

										break;
									}
								}

								break;
							}

							default:
							{
								Logger.LogInformation($"UNKNOWN-{actionType.ToHex()}: {args}");

								// Behaviour 1 while SEATED is the client ASKING to stand up (#355) - getting up is
								// client-initiated, not movement-inferred as we had it. Retail's sit capture, matched pairs:
								//   6829 c2s bId=1 seq=0x0003  ->  6837 s2c bId=1 seq=0x0004
								//   6830 c2s bId=1 seq=0x0002  ->  6838 s2c bId=1 seq=0x000F  ->  6844 s2c STANDUP (bId=0x2A)
								// i.e. the SAME two-phase confirm the sit runs on behaviour 0x12, then the standup animation.
								// Because we inferred standup from movement instead, these requests fell into this
								// un-decoded fallback and got a bare echo - so the client asked to get up, was never
								// answered, and stayed stuck in the pose until it timed out and stood up on its own.
								// The seq mapping (3->4, 2->0x0F) lives in BehaviorAckBody.AckSeq.
								if ((uint)actionType == 1 && client.Session.SeatedOnHandle is not null)
								{
									var requestSeq = rawPayload.Length >= 18
										? (ushort)((rawPayload[16] << 8) | rawPayload[17])
										: 0;
									await CombatService.SendBehaviorEchoAck(client, rawPayload, args.CommandqueueClientId);

									// The phase-2 confirm is what commits it; the standup animation follows the ack.
									if (requestSeq == 0x0002)
									{
										Logger.LogInformation("Sit: client asked to stand up (behaviour 1 phase 2) - answering and standing.");
										await _broadcaster.StandUp(client);
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
								if ((uint)actionType == BehaviorAnimationCodec.ObjectActionBehaviorId && client.Session.SeatedOnHandle is not null)
								{
									Logger.LogInformation("Sit: absorbed the client's re-sent sit behavior (not echoed - it would replay the sit).");

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

						// #48: the combat responses above are framed to the ACTOR only - so the swing/cast was invisible
						// to other players. Relay the actor's behavior (the BCC animation) to nearby viewers so the attack
						// renders on their clients too (retail relays this exact echo body, only the frame receiver
						// differs - other-players-visible capture). The cross-player render channel is the BCC command replication
						// (Ghidra FUN_005e1c70), so it also carries the APPEARANCE-changing behaviors: equipping/unequipping an item
						// (#400 sibling - a new chestpiece/weapon on another player) rides the same relay; the weapon-SET switch is
						// relayed in its own branch above. NEEDS in-client verification for the equip case (no multiplayer capture
						// contains a remote player equipping - single-player traces only).
						if (actionType is ActionType.NormalAttack or ActionType.UnarmedAttack or ActionType.CastSpell or ActionType.MoveEquippedToFromInventory)
						{
							var behaviorRelay = BehaviorAckBody.BuildBehaviorRelayBody(client.Session.Character.Id, rawPayload, args.CommandqueueClientId);
							if (behaviorRelay != null)
								await _broadcaster.BroadcastBehaviorToViewers(client, behaviorRelay);
						}
					}

					break;
				}

				case GameServer0X2000Opcodes.CharacterActionIIR: // Also received when Activating feat point ??
				{
					var unk0 = body.ReadUInt32();
					var clientInst = body.ReadUInt32();

					Logger.LogInformation("GameServer close request for character {CharacterId}", clientInst.ToHex());

					// is handled in ClientDisconnected
					//account.Character.SaveDataToDatabase(Database);
					break;
				}

				case GameServer0X2000Opcodes.LookAtIIR:
				{
					// LookAtIIR (0x0D155695): the client selects/deselects an entity. Body (after the 0x2000 framing):
					//   [u32 0x0000C350 type][u32 selectorInstance=self] [u8 00 00 00 pad]
					//   [u16 0xC350 targetTag][u32 targetHandle] [trailing counters]
					// On deselect the target tag+handle are zeroed. Decoded from
					// Hoxxling_mage-heraldofxotli_login_gameplay (frames 259 select / 362 deselect).
					var data = body.ReadRemainingArray();

					uint targetHandle = 0;
					if (data.Length >= 17)
						targetHandle = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(13, 4));
					client.Session.SelectedTargetHandle = targetHandle;
					// #217: a (re-)select is genuine intent to interact - lift the post-close greeting suppression so a
					// deliberate re-talk opens (the reflexive post-close greeting carries no SelectDeselect).
					client.Session.ConversationEndedNpcHandle = null;

					Logger.LogInformation("SelectDeselect: target 0x{Target:X8} ({Data})", targetHandle, data.ToHex());

					break;
				}

				case GameServer0X2000Opcodes.DeleteItemIIR:
				{
					// ProtoDeleteItemIIR, NOT the sibling move message: { container_id, item_pos, template_id }. The
					// two alias on the wire for fields 1-3, so this parsed as a move for a long time and quietly read
					// the item TEMPLATE id as a destination container - see InventoryDeleteCodec.
					var (_, drop) = InventoryDeleteCodec.Parse(body);

					Logger.LogInformation($"DropItem: {drop}");

					// Remove the dropped item from the persisted bag so it doesn't reappear on relog.
					await _equipment.PersistDropItem(client, drop);

					// Push the LIVE remove so the open bag updates without a relog (#230): the DropItem opcode has no
					// implicit client-side removal, so without this the dropped item stays visible until next login.
					// Same DispatchRemoveItem (0x201F) the .removeitem GM path / RemoveItemFromBackpack use.
					if (drop?.ContainerId?.Type == (uint)ContainerKind.Bag)
						await SendRemoveItem(client, (ContainerKind)drop.ContainerId.Type, (uint)drop.ItemPos);

					break;
				}

				case GameServer0X2000Opcodes.MoveInventoryItemIIR:
				{
					// The recovered client schema names this message ProtoMoveInventoryItemIIR:
					//   { source_container_id, source_item_pos, destination_container_id, destination_item_pos }
					// where each container id is a ProtoID32 { type, instance }. This resolves the old research note
					// that the reference's second value (reused across sibling container blocks 101/104/112/113 in
					// the 0x737A6DF9 field-6 chain) "behaves like a container-owner handle" - it is the container's
					// ProtoID32.instance, not a per-item id. No server->client packet follows these moves.

					var (_, move) = InventoryMoveCodec.Parse(body);

					Logger.LogInformation($"MoveItemInInventory: {move}");

					// Persist the new slot positions so the player's bag arrangement survives logout (the world-entry
					// snapshot rebuilds the bag from these CharacterInventory rows).
					await _equipment.PersistInventoryMove(client, move);

					// A move that touches the personal bank (kind 105) gets no client-side render otherwise (the bank
					// tab isn't refreshed), so a deposit looks like the item vanished - push the live bank refresh.
					await _bank.UpdateBankAfterMove(client, move);

					break;
				}

				case GameServer0X2000Opcodes.QuestIIR:
				{
					var unk0 = body.ReadUInt32();
					var clientInst = body.ReadUInt32();
					var unk1 = body.ReadByte();
					var unk2 = body.ReadUInt32();
					var unk3 = body.ReadUInt32();
					var questId = body.ReadUInt32(); // probably
					var unk4 = body.ReadUInt32();

					/*
						// Shadows of the past
						0x00, 0x00, 0xC3, 0x50, 						0x11, 0x01, 0xB4, 0xF5, // clientInst
						0x00, 						0x00, 0x00, 0x00, 0x01, 						0x00, 0x00, 0x00, 0x04, 						0x00, 0x00, 0x4B, 0xA4, // questId?
						0x00, 0x00, 0x00, 0x00

						// Summoned
						0x00, 0x00, 0xC3, 0x50, 						0x11, 0x01, 0xB4, 0xF5, // clientInst
						0x00, 						0x00, 0x00, 0x00, 0x01, 						0x00, 0x00, 0x00, 0x04, 						0x00, 0x00, 0x46, 0x20, // questId?
						0x00, 0x00, 0x00, 0x00

						// Storming the gate
						0x00, 0x00, 0xC3, 0x50, 						0x11, 0x01, 0xB4, 0xF5, // clientInst
						0x00, 						0x00, 0x00, 0x00, 0x01, 						0x00, 0x00, 0x00, 0x04, 						0x00, 0x00, 0x36, 0x47, // questId?
						0x00, 0x00, 0x00, 0x00
					 */

					break;
				}

				case GameServer0X2000Opcodes.CoreClaimIIR:
				{
					// The Claims/Redeem window: sub 0x0007 requests the list, sub 0x0003 redeems a claim. See
					// GameServerListener.Claims.cs / docs/subsystems/item-claims.md.
					await _claims.HandleCoreClaim(client, body);

					break;
				}

				case GameServer0X2000Opcodes.FeatIIR:
				{
					// Feat TRAIN: state 0x04 [feat ids] -> add to CharacterFeat -> s2c FeatIIR state 0x05 ack. See
					// GameServerListener.Feats.cs / docs/subsystems/feats.md.
					await _feats.HandleFeatTrain(client, rawPayload);

					break;
				}

				case GameServer0X2000Opcodes.TradeskillRequestIIR:
				{
					// The client opened the Crafting -> Recipes book and is asking for its recipe list. Answer with
					// the TradeskillReplyIIR (0x9062D559) catalog from the DB so the book populates. See
					// TradeskillRecipeCodec / docs/subsystems/tradeskill-recipes.md.
					_ = body.ReadRemainingArray();
					await SendCraftingRecipeBook(client);

					break;
				}

				case GameServer0X2000Opcodes.UpdateInventoryIIR:
				{
					// c2s UpdateInventoryIIR is a PROBE, not a request that wants an answer: as an entity streams in
					// the client fires a pair asking for that entity's Equipped(101) then Bag(104) container -
					//   [handle][env 00][u32 pbLen][ProtoInventoryGame { 1: { 1: kind, 2: handle } }]
					// where the protobuf's inner id is the carrier handle again. It rides the same streaming burst as
					// UpdateQuestMarkerNPC + RequestAggroStatus on that handle.
					// Retail NEVER answers it: across all 104 captures there are 34 c2s and 2 s2c, and the 2 s2c are an
					// UNSOLICITED push of the player's OWN containers after an equip change (a RemoveItem, in
					// Hoxxling_mage-heraldofxotli_login_gameplay2 frame 5118) - zero of the 34 requests draw a reply.
					// Visible gear already arrives with the spawn snapshot, so drop it quietly like the teleport acks
					// below; answering would diverge from capture. The s2c push itself is unimplemented (see
					// UpdateInventoryCodec, currently emitted nowhere).

					Logger.LogDebug("GameServer 0x2000 inventory probe"); //" {SubOpcodeHex} ({RawLength} bytes).", pbOpcode.ToHex(), probe.Length);

					break;
				}

				default:
				{
					var data = body.ReadRemainingArray();

					// In-world the client streams 0x2000 packets whose sub-opcode is its OWN instance id
					// (the client-instance ack/keepalive stream). With the create flow now using the real
					// character id, that id is dynamic (not a fixed enum value like the old 0x021662C8), so
					// it lands here. Treat it as a benign ping: log quietly, don't spam system messages.
					if ((uint)pbOpcode == client.Session.Character.Id)
					{
						Logger.LogDebug("GameServer 0x2000 client-instance ping {SubOpcodeHex} ({RawLength} bytes).", pbOpcode.ToHex(), rawPayload.Length);
						break;
					}

					// After we relocate the player on resurrect (IntendedTeleport + n3Teleport, #214), the client echoes
					// the teleport records back; benign acks, no reply needed - log quietly instead of spamming a system
					// message into chat.
					if ((uint)pbOpcode == (uint)GameServer0X2000Opcodes.N3TeleportIIR || (uint)pbOpcode == (uint)GameServer0X2000Opcodes.IntendedTeleportIIR)
					{
						Logger.LogDebug("GameServer 0x2000 client teleport-ack {SubOpcodeHex} ({RawLength} bytes).", pbOpcode.ToHex(), rawPayload.Length);
						break;
					}

					Logger.LogDebug("Unhandled GameServer 0x2000 subopcode {SubOpcodeHex} raw payload: {RawPayload}", pbOpcode.ToHex(), rawPayload.ToHex());

					await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"Unknown 0x2000 Opcode: {pbOpcode.ToHex()} ({rawPayload.Length} bytes)");

					break;
				}
			}
		}

		// Handles the create-editor "instance echo": the client repeats its own client instance id as a 0x2000
		// subopcode while the editor is loading. We count these to time the post-0x205F and post-ready bursts, 		// exactly as retail does, and ack standalone ones. (Retail used a fixed instance; we hand out the
		// character's real id, so the matching is done dynamically by the caller.)
		private async Task HandleCreateEditorInstanceEcho(INetworkClient client, FaolanStream body)
		{
			// The instance-echo carries [ack0][requestId]; read them directly (a short body throws, surfacing the
			// unexpected shape, rather than being silently swallowed).
			var ack0 = body.ReadUInt32();
			var requestId = body.ReadUInt32();
			Logger.LogInformation("Create-editor instance-echo decoded: ack0={Ack0:X}, requestId={RequestId:X}.", ack0, requestId);

			if (!client.Session.CreateEditorObservedRequestIds.Contains(requestId))
				client.Session.CreateEditorObservedRequestIds.Add(requestId);

			// Retail just ACKs the client's create-editor instance-echoes (there is no special post-0x205F burst); the
			// burst that actually launches the editor is sent on CharInPlayIIR.
			Logger.LogInformation("Acknowledging create-editor instance-echo with requestId={RequestId:X}.", requestId);
			await CreateFlowService.SendRetailCreateEditorPostReadyAck(client, requestId);
		}

		// GameAgent (connection-class) dispatch: auth / chat / ping / server-stats. Routed here by receiver class.
		private async Task HandleGameAgentPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var opcode = (GameAgentOpcodes)packet.Opcode;
			if (opcode is not GameAgentOpcodes.UpdateGameServerStats)
				LogPacketReceived(packet);

			switch (opcode)
			{
				case GameAgentOpcodes.Authenticate2:
				{
					// 0x2003 = GameAgent.Authenticate2 (the connection Hello: clientInstance + accountId + version).
					// The GameCharAgent.SetTutorialStats collision on this opcode is resolved by the class-first route
					// above, so here the body is always the >= 12-byte Authenticate2. Guard defensively anyway: a short
					// body would over-read and drop the client.
					if (body.Length - body.Position < 12)
					{
						Logger.LogWarning("GameAgent.Authenticate2 (0x2003) with a short {Bytes}-byte body; ignoring.", body.Length - body.Position);
						break;
					}

					var clientInstance = body.ReadUInt32(); // 0x0802e5d4 0x1101b4f5
					var accountId = body.ReadUInt32(); // 0x310cec57 0x2b68ea25=728295973
					var clientVersion = body.ReadString();

					client.Session.Account = (await Database.GetAccount(accountId))!;
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					client.Session.Account.ClientInstance = clientInstance;

					// No `!` here on purpose: GetCharacterForAccount returns null for a character this account does not own,
					// and forgiving that makes the check look like dead code someone will helpfully delete.
					client.Session.Character = await Database.GetCharacterForAccount(accountId, clientInstance)
						?? throw new Exception($"Character not found for account {accountId:X} and client instance {clientInstance:X}");

					Logger.LogInformation("GameServer hello: clientInstance={ClientInstance:X8}, clientVersion={ClientVersion}", clientInstance, clientVersion);

					// The build we decoded against, from config (default v5.01.0@602248 - every capture in the corpus is
					// that one). Not a refusal: the protocol is largely shared across builds, and rejecting on a string
					// would lock out a working client for a trivial difference. It is a loud warning because otherwise
					// "the client behaves oddly" and "the client is a different build" look identical in the logs.
					if (clientVersion != Configuration.ClientVersion)
					{
						Logger.LogWarning("GameServer hello: client reports build {Reported}, but the protocol was decoded against {Expected}. Unexpected behaviour past this point may be a build difference.",
							clientVersion, Configuration.ClientVersion);
					}

					// Character creation has two phases on this GameServer connection:
					//  1) CREATOR: the client connects on the temp instance 0x021662C8 and we send the
					//     verbatim editor burst (RetailCreateEditorInitialBurst) so the creator UI appears.
					//     Auth replies use the editor conversation family so the client associates the burst.
					//  2) ENTER-WORLD: after the player customises + names, the world entry is sent (handled
					//     out of the post-0x205F / ReadyForPlayScreen path), not here.
					// Existing-character login uses the legacy conversation family + the world entry.
					var isNewCharacter = client.Session!.Character.Name == null;

					// Post-creation WORLD connection: the client reconnects with the same (now named) character
					// instance, but this is the SECOND GameServer connection - the one opened after the 0x2002
					// enter-world handoff. It is recognised by the character id being in PendingCreateWorldEntry
					// (added when the name was committed on the editor connection). Consume it here so a later
					// normal login of the same character does not re-trigger the intro. This connection needs the
					// full retail creation world entry (the verbatim burst with its own 0x200B/0x200C/0x2001
					// auth + the intro/cutscene), so skip the helper auth for it.
					var isCreateWorldEntry = !isNewCharacter && CreateFlowService.TryConsumePendingCreateWorldEntry(clientInstance);
					// The connection's world-entry role, decided ONCE here and the single source of truth for routing
					// every later handshake signal (docs/subsystems/world-entry-flow.md). CreateWorld/CreateEditor are
					// known now; the Login-vs-TeleportReentry split is finalised in the existing-login branch below (it
					// consumes PendingTeleportReentry there, next to the follow-up arming that depends on it).
					client.Session.EntryMode = isCreateWorldEntry
						? WorldEntryMode.CreateWorld
						: isNewCharacter
							? WorldEntryMode.CreateEditor
							: WorldEntryMode.Login;

					// Retail sends this auth trio (DimensionId 0x200b / ServerId 0x200c / AckAuthenticate 0x2001)
					// byte-identically in BOTH the create-editor and the existing-login flow (verified against
					// Barbarian-create vs -login) - so it is NOT the create-vs-login differentiator (that is purely the
					// following 0x2000 burst content). Send it for both; only isCreateWorldEntry (the post-creation world
					// connection) skips it - it carries its own auth in the verbatim burst.
					if (!isCreateWorldEntry)
					{
						var authSender = isNewCharacter
							? Endpoints.Endpoint(Endpoints.GameAgentType, 0x59)
							: WorldSenderEndpoint;
						var authReceiver = isNewCharacter
							? CreateFlowService.Receiver(client)
							: client.Endpoint(Endpoints.GameInterfaceType);

						await ReportDimensionId(client, client.Session.Character.RealmId, authSender, authReceiver); // 1
						await ReportServerId(client, 1, authSender, authReceiver); // 0x00000006
						await client.SendAsync(new Rpc(authSender, authReceiver, GameInterfaceOpcodes.DispatchAckAuthenticate, s => s.WriteUInt32(1)));
					}

					client.Session.HasPendingPost205FFollowUp = false;
					client.Session.CreateEditorObservedRequestIds.Clear();
					client.Session.MorphAckCounter = 0;
					client.Session.MorphActivitySequence = 0;
					client.Session.PostMorphBurstSent = false;
					client.Session.AwaitingEnterWorld = false;
					client.Session.CreateWorldChunkIndex = 0;
					// IsCreateEditorConnection / IsCreateWorldSession were set above from this connection's role.

					if (isCreateWorldEntry) // Post-creation enter-world -> full retail creation world entry (-> scene)
					{
						// Force the Tortage intro spawn onto the character (covers resumed characters that
						// never went through CreateCharacter), so the live SpawnPlayer uses the right position.
						var spawnRows = await Database.SetCreationSpawn(client.Session.Character.Id);
						Logger.LogInformation(
							"Post-creation world entry (clientInstance={ClientInstance:X8}, character={CharacterId:X8} '{Name}'): set creation spawn (rows={Rows}, pos=({X},{Y},{Z}), map={Map}); sending the creation world SCENE burst.",
							clientInstance, client.Session.Character.Id, client.Session.Character.Name, spawnRows, client.Session.Character.PositionX, client.Session.Character.PositionY,
							client.Session.Character.PositionZ, client.Session.Character.MapId);
						// #234 reconstruct: the verbatim
						// SendCreateWorldSceneBurst delivers the captured `hoxxling` world (wrong char) -> after the
						// cutscene the client drops the connection and the reconnect hangs. Send the PROVEN burst-less
						// live-char world + the destiny cutscene's Scry-script overlay instead (SendCreateWorldWithCutscene):
						// the created char lands on the beach as ITSELF and the cutscene drives to IntroDone. Cutscene
						// actors stay invisible until #185; SendCreateWorldSceneBurst stays as the golden reference.
						await _createFlow.SendCreateWorldWithCutscene(client);
					}
					else if (isNewCharacter) // New character -> show the creator
					{
						Logger.LogInformation("New-character create connection (clientInstance={ClientInstance:X8}): sending the character-creator (editor) burst.", clientInstance);
						await CreateFlowService.SendRetailCreateEditorInitialBurst(client, Database, logger: Logger);
					}
					else
					{
						// Existing-character login: the DB-driven, burst-less world entry - the character's current-map
						// world content is CONSTRUCTED from the DB (WorldObject/NpcSpawn tables) and addressed to this
						// character, so the client builds the playfield and renders the NPCs. The 0x201c handshake comes
						// from us; the player's own position/name/inventory are rebuilt from the DB too. (See SendWorldEntry.)
						await SendWorldEntry(client);
						// Abilities (0x2052) are granted AFTER 0x2024 ReadyForPlayScreen (in FlushPendingPost205FFollowUp), 						// matching the retail login order (handshake -> 0x2024 -> 0x2052). Granting them eagerly here -
						// before the client polled/was ready - is a divergence from the Strie login dump.

						// Teleport/changemap re-entry: a full login arms the post-0x205F follow-up off the client's 0x205F, 						// but a direct GameServer->GameServer changemap reconnect sends no 0x205F (retail confirms). Arm it
						// here so the next 0x2020 poll flushes 0x2024 ReadyForPlayScreen (+ abilities) and the loading screen
						// dismisses; otherwise the burst lands but the client loads forever (#28 / totem recall).
						if (PendingTeleportReentry.TryRemove(clientInstance, out _))
						{
							client.Session.EntryMode = WorldEntryMode.TeleportReentry;
							client.Session.HasPendingPost205FFollowUp = true;
							Logger.LogInformation(
								"Teleport re-entry (clientInstance={ClientInstance:X8}): armed the post-0x205F follow-up so 0x2024 ReadyForPlayScreen flushes on the next 0x2020 (no client 0x205F on a changemap reconnect).",
								clientInstance);
						}
					}

					break;
				}

				case GameAgentOpcodes.PassBlob:
				{
					await Handle0X2000(client, packet, body);

					break;
				}

				case GameAgentOpcodes.UpdateGameServerStats: // client 0x2005 (empty) -> server Pong 0x2009
				{
					await KeepAliveService.HandlePing(client);

					break;
				}

				case GameAgentOpcodes.ChatVicinity:
				{
					// 0x2002 = GameAgent.ChatVicinity (typed chat, carries GM commands). The GameCharAgent.RequestBindLocation
					// collision on this opcode is resolved by the class-first route above (HandleRequestBindLocation).
					LogPacketBodyDebug("GameServer ChatCommand body", body.ToArray());

					var unk0 = body.ReadUInt32();
					var unk1 = body.ReadUInt32();
					var unk2 = body.ReadUInt32();
					var unk3 = body.ReadUInt32();
					var text = body.ReadString();

					if (text.Length > 2 && text[0] == '.' && text[1] != '.' && client.Session.Account.Type >= AccountType.GameMaster)
						await HandleGmCommand(client, text);
					else
						// "say" is proximity chat - only same-map players within range hear it (the sender always does).
						// The GameServer owns the live positions, so it picks the recipients here.
						await _agentServerDispatcher.CharacterSay(client.Session.Character.Id, text, _simulation.SayRecipients(client));

					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		// GameCharAgent dispatch: the 0x2002/0x2003 collision forms (bind/tutorial) + every per-character method
		// (loot / shop / respawn / shortcut / latency / ...). Routed here by receiver class from ReceivedPacket.
		private async Task HandleGameCharAgentPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var opcode = (GameCharAgentOpcodes)packet.Opcode;
			if (opcode is not GameCharAgentOpcodes.Ping)
				LogPacketReceived(packet);

			switch (opcode)
			{
				// --- player trade (#101). The request itself arrives as a BCC (ActionType 0x16 at a PLAYER), handled above;
				// everything from the window opening onward is these five methods. ------------------------------------
				case GameCharAgentOpcodes.StartTrade: // 0x2065 - the TARGET opens the window, creating the session
				{
					await _trade.Start(client.Session.Character.Id, TeamMessageCodec.ParseCharacterId(body));
					break;
				}

				case GameCharAgentOpcodes.SetTradeCash: // 0x2067
				{
					await _trade.SetCash(client.Session.Character.Id, body.ReadUInt64());
					break;
				}

				// 0x2068 - the SAME method index as the s2c DispatchRequestTrade, on the other interface.
				case GameCharAgentOpcodes.AddTradeItem:
				{
					var request = PlayerTradeCodec.ParseAddItem(body);
					await _trade.AddItem(client.Session.Character.Id, request.ItemPos, request.DestPos);
					break;
				}

				case GameCharAgentOpcodes.AcceptTrade: // 0x206B, empty
				{
					await _trade.Accept(client.Session.Character.Id);
					break;
				}

				// 0x2069 RemoveTradeItem([int destPos]) - take an item back off the table.
				case GameCharAgentOpcodes.RemoveTradeItem:
				{
					_trade.RemoveItem(client.Session.Character.Id, body.ReadUInt32());
					break;
				}

				// 0x206C AbortTrade (empty) and 0x2066 DeclineTrade([ID32]) - the cancel, and refusing the request before
				// the window opens. Both end the session; neither has moved anything, since the exchange only happens on
				// the double accept. What the server owes the PARTNER to close THEIR window is not known - see #427.
				case GameCharAgentOpcodes.AbortTrade:
				{
					_trade.Abandon(client.Session.Character.Id);
					break;
				}

				case GameCharAgentOpcodes.DeclineTrade:
				{
					TeamMessageCodec.ParseCharacterId(body); // the requester, echoed by the client
					_trade.Abandon(client.Session.Character.Id);
					break;
				}

				// 0x2008 LeaveTeam (no body): a DIFFERENT interface's method 0x2008 than the CS tier's AcceptTeamInvite -
				// method indices are per interface, so the number collision is expected and must not be unified.
				case GameCharAgentOpcodes.LeaveTeam:
				{
					await _csPlayerAgentDispatcher.LeaveTeam(client.Session.Character.Id);
					break;
				}

				// 0x2009 KickFromTeam([ID32 target]): the leader removes a member. TeamService checks the leadership.
				case GameCharAgentOpcodes.KickFromTeam:
				{
					await _csPlayerAgentDispatcher.KickFromTeam(client.Session.Character.Id, TeamMessageCodec.ParseCharacterId(body));
					break;
				}

				// 0x2091 RequestTeamInvite([member record]): the invited client resolves the inviter's details, the way
				// ClientRequestGuildInfo resolves a guild banner. Answer with that character's record.
				case GameCharAgentOpcodes.RequestTeamInvite:
				{
					var subject = TeamMemberRecord.Read(body);

					if (await Database.GetCharacter(subject.CharacterId) is { } character)
						// Two things about the header, both of which cost an invite window when missed:
						//
						// The SENDER is the GameCharAgent object (f683: class GameCharAgent, instance 0x10), not the
						// world sender this file usually replies from - a reply belongs to the interface pair its
						// request arrived on.
						//
						// And it carries the trailing headerData 0x01, as KeepAliveService's DispatchPong on this same
						// interface does. Retail puts it on this reply (f682 and f683 both carry it) while the CS-tier
						// invite dispatches carry none, and the client DISCARDS the reply without it: it accepted our
						// invite record, echoed it back in RequestTeamInvite, and then never raised the window - and
						// still did not when fed retail's own captured bytes, because the bytes were never the problem.
						await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x10), client.Endpoint(Endpoints.GameCharInterfaceType),
							GameCharInterfaceOpcodes.DispatchReplyTeamInvite, (byte[])[0x01],
							TeamMessageCodec.BuildReplyInvite(TeamMessageCodec.MemberOf(character, GetCharacterLiveState(character.Id)?.MapId ?? character.MapId))));

					break;
				}

				case GameCharAgentOpcodes.RequestBindLocation: // 0x2002 = RequestBindLocation
				{
					await HandleRequestBindLocation(client, packet, body);
					return;
				}

				case GameCharAgentOpcodes.SetTutorialStats: // 0x2003 = SetTutorialStats (Tortage tutorial tracking)
				{
					Logger.LogDebug("GameCharAgent.SetTutorialStats; no reply.");
					return;
				}

				case GameCharAgentOpcodes.SetHideInspectOption:
				{
					LogPacketBodyDebug("GameServer 0x207E body", body.ToArray());
					if (body.Length > 0)
						Logger.LogInformation("0x207E decoded: value0={Value0:X2}", body.ToArray()[0]);

					break;
				}

				case GameCharAgentOpcodes.CinematicTransition:
				{
					var unk0 = body.ReadUInt32(); // 0xc350
					var characterId = body.ReadUInt32();

					if (client.Session.IsUnnamedCreatePhase)
					{
						// Create-editor phase: retail sends no download in response to 0x205F - it just acks the
						// client's polls and waits for CharInPlayIIR (which triggers the editor-launch burst).
						client.Session.HasPendingPost205FFollowUp = true;
						Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Create-editor phase; awaiting CharInPlayIIR.", unk0, characterId);
						break;
					}

					client.Session.HasPendingPost205FFollowUp = true;
					Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}, expectedCharacterId={ExpectedCharacterId:X8}. Marked post-0x205F follow-up as pending.", unk0,
						characterId, characterId);

					break;
				}

				case GameCharAgentOpcodes.UpdateQuestMarkerNPC:
				{
					// wire 0x2020 on the GameCharAgent class = method-index slot 0x20, whose RPC catalog LABEL is
					// UpdateQuestMarkerNPC - but the world-entry handshake REUSES it as the client's spawn-poll +
					// ready-trigger. Owned by HandleWorldEntrySpawnPoll (WorldEntry partial), not decoded inline here.
					await HandleWorldEntrySpawnPoll(client, packet, body);
					break;
				}

				case GameCharAgentOpcodes.DoSkillSellback:
				{
					LogPacketBodyDebug("GameServer 0x205C body", body.ToArray());

					break;
				}

				case GameCharAgentOpcodes.PlayStockphraseAsSubtitle:
				{
					// #234 create-cutscene speech: the client is asking us to resolve a stock phrase
					// ({stock_N}) and BLOCKS up to 60 s for the 0x205D reply. Answer it (Kalanthes's beach
					// lines) instead of silently timing out. See Dialogue/StockConversationService.cs.
					await _stockConversation.HandlePlayStockphraseAsSubtitle(client, body.ToArray());

					break;
				}

				case GameCharAgentOpcodes.RequestShortcutBar: // client asks for its saved hotbar at login; reply with the restored bar (#71)
				{
					await _shortcutBar.SendRestoredBar(client);

					break;
				}

				case GameCharAgentOpcodes.SellItem:
				{
					await _shop.HandleSellItemToShop(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.UndoLastSoldItem:
				{
					await _shop.HandleUndoLastSoldItem(client);

					break;
				}

				case GameCharAgentOpcodes.BuyItem:
				{
					body.ReadUInt32(); // shop ID32 type (0xC75B)
					var shopId = body.ReadUInt32();
					var pos = body.ReadUInt32();

					// Resolve the clicked slot against the open shop, debit the wallet and grant the item live
					// (DispatchAddItem). The item-grant wall that gated this is gone (protocol_containeradditem).
					await _shop.HandleBuyItemFromShop(client, shopId, pos);

					break;
				}

				case GameCharAgentOpcodes.CloseShop:
				{
					Logger.LogInformation("Shop closed by {Character}.", client.Session.Character?.Name);

					break;
				}

				case GameCharAgentOpcodes.TryLootAll: // the loot-all click on an open loot window; body = [typetag][bag handle]
				{
					await _loot.HandleLootAll(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.TryLootItem: // single drag of one item out of the loot window
				{
					await _loot.HandleLootItem(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.UntrainFeat: // "untrain all feats" -> send the confirm messagebox
				{
					await _feats.HandleFeatUntrainRequest(client);

					break;
				}

				case GameCharAgentOpcodes.FeatUntrainAnswer: // confirm-dialog answer -> reset if confirmed
				{
					await _feats.HandleFeatUntrainAnswer(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.CloseLootBag: // client closed the loot window; body = [typetag][bag handle]
				{
					await _loot.HandleCloseLootBag(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.TryLootCash: // loot the bag's coins; fires on every loot alongside TryLootAll
				{
					await _loot.HandleLootCash(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.RespawnMe: // the player's release/resurrect click; body = [u32 token]
				{
					var token = body.Length >= sizeof(uint)
						? body.ReadUInt32()
						: 0;

					// Retail respawn is click-driven (vitals restore + tombstone + teleport + re-render at the
					// resurrection point); our simulation already implements that flow on a fallback timer - the
					// click just triggers it immediately.
					if (await _simulation.TryRespawnNow(client))
						Logger.LogInformation("RespawnMe (token 0x{Token:X8}): player released; respawned.", token);
					else
						Logger.LogWarning("RespawnMe (token 0x{Token:X8}) from a player that is not dead; ignored.", token);

					break;
				}

				case GameCharAgentOpcodes.CancelBuff: // right-click-cancel a buff; body = [u32 buffId(base)][u32 charId]
				{
					var buffId = body.ReadUInt32();
					var charId = body.ReadUInt32();
					var self = client.Session.Character.Id;
					var entity = _simulation.PlayerWorldEntity(client);

					// The client sends the buff's BASE id (rank byte stripped, e.g. 0x0037F38C); our registry keys are
					// the APPLIED wire id ((base<<8)|rank). Match by base so the removal twin carries the exact id.
					var appliedId = buffId << 8;
					if (entity != null)
					{
						foreach (var key in entity.ActiveBuffs.Keys)
						{
							if (key >> 8 == buffId)
							{
								appliedId = key;
								break;
							}
						}
					}

					// The client does NOT self-remove the icon on right-click (verified in-client: the icon stayed up
					// with only the registry drop) - the server has to drive it. Send retail's rank-1 end twin, which
					// clears the icon live (byte-exact vs the captured buff-end twin, NecroL80_..._castle-exit rec#735).
					// No capture oracle for the cancel reply exists, so this is the in-client-proven behaviour.
					await SendBuffRemoval(client, self, self, appliedId);

					if (entity != null && entity.ActiveBuffs.TryRemove(appliedId, out _))
						Logger.LogInformation("CancelBuff: buff 0x{Buff:X8} removed from {CharId:X8}.", appliedId, charId);
					else
						Logger.LogDebug("CancelBuff: buff 0x{Buff:X8} was not tracked for {CharId:X8} (cast before the registry, or an aura).", appliedId, charId);

					// If the cancelled buff was the tracked Shield of the Risen, forget it so the next playfield entry
					// re-applies cleanly rather than trying to remove an id the client no longer shows.
					if (client.Session.ActiveShieldSpellBase == buffId)
						client.Session.ActiveShieldSpellBase = null;

					break;
				}

				case GameCharAgentOpcodes.Ping: // latency check (client 0x2085 -> server 0x2082)
				{
					await KeepAliveService.HandleLatencyCheck(client, packet, body);

					break;
				}

				// 0x206A MoveTradeItemInternal([int pos1][int pos2]): the player drags an offered item to another slot
				// of the trade window (#101). Signature from the client's own catalog. This case had sat here since the
				// C++-era port with a commented-out N3Teleport block under it that had nothing to do with trade.
				case GameCharAgentOpcodes.MoveTradeItemInternal:
				{
					_trade.MoveItem(client.Session.Character.Id, body.ReadUInt32(), body.ReadUInt32());
					break;
				}

				case GameCharAgentOpcodes.GetInstanceListForCurrentPlayfield: // When the 'Select instance' button is clicked
				{
					LogPacketBodyDebug("GameServer 0x207B body", body.ToArray());


					break;
				}

				case GameCharAgentOpcodes.PerformSpecialAction:
				{
					// 0x205A PerformSpecialAction is a GENERIC action opcode multiplexed by its body word, NOT a
					// spellweave-only toggle: 0xFC = the necro spellweave activate/deactivate toggle (all 3 NecroL80
					// spellweave captures), 0xFF = open the Crafting -> Recipes book, 0x6C/0x6D/0x70/0xF0 = melee
					// combo inputs (Hoxxling/AssassinL23 combo captures). Only 0xFC drives the trance - blindly routing
					// every 0x205A to spellweave made opening the recipe book start a weave on a necro.
					var action = body.ReadUInt32();

					Logger.LogInformation("0x205A (PerformSpecialAction) param={Action:X4}", action);

					switch (action)
					{
						case SpellweaveToggleParam:
						{
							await _spellweave.ToggleAsync(client, action);
							break;
						}

						// Path of Asura: a recall teleport, same as the Totem of Origin (#206). The client double-sends the
						// action (capture F303+F307), so gate on IsTeleportingOut - the first recall arms the handoff, the
						// duplicate is absorbed.
						case PathOfAsuraSpecialAction when !client.Session.IsTeleportingOut:
						{
							await RecallToBindCommand(client, "The Path of Asura carries you home...");
							break;
						}
					}

					break;
				}

				case GameCharAgentOpcodes.StackInventoryItemIfPossible: // (the old C++ docs called this "SpawnCheck", p.160-161)
				{
					LogPacketBodyDebug("GameServer StackInventoryItemIfPossible body", body.ToArray());

					break;
				}

				case GameCharAgentOpcodes.SplitInventoryItem:
				{
					var unk0 = body.ReadUInt32();
					var unk1 = body.ReadUInt32();
					var sourceSlot = body.ReadUInt32();
					var targetStackSize = body.ReadUInt32();
					var targetSlot = body.ReadUInt32();

					// 0x00, 0x00, 0x00, 0x68, 					// 0x11, 0x01, 0xB4, 0xF5, 					// 0x00, 0x00, 0x00, 0x09, // source slot
					// 0x00, 0x00, 0x00, 0x29, // 41 (target stack size)
					// 0x00, 0x00, 0x00, 0x13  // target Slot

					// 0x00, 0x00, 0x00, 0x68, 					// 0x11, 0x01, 0xB4, 0xF5, 					// 0x00, 0x00, 0x00, 0x03, // source slot
					// 0x00, 0x00, 0x00, 0x1C, // 28 (target stack size)
					// 0x00, 0x00, 0x00, 0x04  // target Slot

					// Persist the split so both stacks survive logout (bag container; the packet carries no kind).
					await _equipment.PersistSplitStack(client, (int)sourceSlot, (int)targetSlot, targetStackSize);

					break;
				}

				case GameCharAgentOpcodes.RemoveShortcut: // body = [u32 slot]
				{
					await _shortcutBar.HandleRemoveShortcut(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.UpdateShortcut: // body = [u32 slot][u32 flag][u32 container]
				{
					await _shortcutBar.HandleUpdateShortcut(client, packet, body);

					break;
				}

				case GameCharAgentOpcodes.AddShortcut:
				{
					// 0x206E = MoveItemToBar: ALWAYS a bar drag ([u32 type][u32 slot][u32 id][u32 unk]). During create
					// the client populates the NEW character's starting bar with it right after CharCreationFinished
					// (verified in the Blablatest/Hoxxling create captures: the starting abilities + spells at slots
					// 0x64.. / 0x4E20..), BEFORE entering the world - so persist it on the editor connection too, or a
					// fresh character loses its retail starting bar and falls back to our seeded default.
					await _shortcutBar.HandleAddShortcut(client, packet, body);

					// On the editor connection the FIRST post-world-prep 0x206E also signals "ready to enter world":
					// answer it once with the 0x2002 handoff. #234: wash the new char ashore first with a changemap-prep
					// (IntendedTeleport 0x10D27BC0 + n3Teleport 0x5A32F0D7) to the Tortage Beach spawn - Blablabla create
					// capture f444/449 (dest map 0x0FAA, pos 322.5,150,704.9, descriptor 0x32) - else the player stays
					// at the drowning cutscene's underwater end position. Mirrors GMCommands.DoTeleport (prep -> handoff).
					if (client.Session is { IsCreateEditorConnection: true, AwaitingEnterWorld: true })
					{
						client.Session.AwaitingEnterWorld = false;
						await SendChangeMapPrep(client, Map.TortageBeachId, Map.NewPlayerSpawnSpawnPoint.Position, 0x32);
						await SendWorldHandoff(client);
					}

					break;
				}

				case GameCharAgentOpcodes.QueuePetCommand:
				{
					await HandlePetCommand(client, packet, body);
					return;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		// Inbound RPCs on the GameCharAgentProject endpoint (0x5B9756FC). Routed here by class id from
		// ReceivedPacket because its method ids (0x2000, ...) collide with ordinary GameServer opcodes.
		//
		// The one that matters for world entry is RequestAggroStatus (client_to_server[0] => opcode 0x2000): once
		// the playfield is built the client polls the server for the aggro state between the player and each nearby
		// hostile NPC, and will NOT finish the loading screen until each poll is answered. Retail answers every one
		// (~17 over a login); we answered none, so the live client retried the same poll 450+ times and sat on the
		// loading screen forever - which is why it never opened the ChatServer/CommunityServer (chat) connection.
		// Decoded from the kalantes_chat retail capture (port 7015):
		//   req  C->S: recv=GameCharAgentProject, send=GameCharInterfaceProject/<player>, op 0x2000,
		//               body [u32 0xC350][u32 charInstance][u32 0xC350][u32 targetInstance]   (two ProtoID32s)
		//   resp S->C: send=GameCharAgentProject/<zone handle 0x58>, recv=GameCharInterfaceProject/<player>, op 0x2000,
		//               body [u32 0xC350][u32 targetInstance][u32 cCategory]
		// The 3rd field is the target's overhead NAME-COLOUR category (0 friendly / 1 hostile / 2 neutral), the SOLE
		// driver of the red/green/yellow name (proven by live gdb, #256) - not a live-aggro flag. We answer every
		// poll (still unblocks the load) and now set 1 for hostile NPCs so their names render red like retail.
		private async Task HandleGameCharAgentProject(INetworkClient client, Rpc packet, FaolanStream body)
		{
			switch ((GameCharAgentProjectOpcodes)packet.Opcode)
			{
				case GameCharAgentProjectOpcodes.RequestAggroStatus: // 0x2000 - GATES the loading screen (must reply)
				{
					await HandleRequestAggroStatus(client, packet, body);
					break;
				}

				case GameCharAgentProjectOpcodes.ToggleVanityArmorFilter: // 0x200B - persist the appearance filter
				{
					await HandleToggleVanityArmorFilter(client, packet, body);
					break;
				}

				case GameCharAgentProjectOpcodes.SetSprintInputState: // 0x200C, the sprint key
				{
					// A fire-and-forget input-state update retail does NOT reply to (#72). Body = [u16 state] (1 = on,
					// 0 = off). Sprint drains the yellow ENERGY bar CLIENT-side (retail's server barely streams the bar
					// during sprint - verified: 1 packet across a sprint-heavy capture). We don't reply or drive the bar;
					// we only MIRROR the drain into the tracked session.Energy so spellweaving starts from the real
					// current value and Exhausted can fire at 0 (docs/subsystems/combos.md).
					var state = body.Length - body.Position >= sizeof(ushort)
						? body.ReadUInt16()
						: (ushort)0;
					TrackSprintEnergy(client, state == 1);
					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		private async Task HandleRequestAggroStatus(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// RequestAggroStatus(const ID32& charId, const ID32& targetId): each ID32 is [u32 type][u32 instance].
			body.ReadUInt32(); // charId.type   (0x0000C350)
			body.ReadUInt32(); // charId.instance (the polling player)
			var targetType = body.ReadUInt32(); // targetId.type (0x0000C350)
			var targetInstance = body.ReadUInt32();

			var instance = client.Session.Character.Id;

			// The reply's third field (cCategory) is the client's overhead NAME-COLOUR category for this target, 			// NOT a live-aggro toggle: 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow). Proven by live
			// gdb (#256): DispatchUpdateAggroStatus -> FUN_0115eb10 emits the disposition signal that writes the
			// nameplate colour index (color_friendly_npc/color_hostile_npc/color_neutral_npc); retail's cCategory is
			// exactly 0/1/2. We always sent 0, so every NPC rendered green - including hostile mobs. Send 1 for a
			// hostile NPC so its name renders red like retail. (The NpcStats.community we put in the spawn is correct
			// but the client ignores it for the name colour - this RPC is the sole driver.)
			// cCategory is the target's captured retail disposition (0 friendly / 1 hostile / 2 neutral), the SAME
			// source that gates aggro (#256/aggro). Sending target.Disposition directly (not Hostile ? 1 : 0) means a
			// neutral NPC renders yellow, not just red/green - and a friendly civilian in a non-zero faction community
			// no longer wrongly renders red.
			var category = WorldEntity.DispositionFriendly;
			// Resolve the target in the player's OWN instance (copy) of the map (#268), not the default shared zone.
			var player = _simulation.PlayerWorldEntity(client);
			if (player != null && _simulation.World.ZoneForEntity(player).TryGet(targetInstance, out var target))
				category = target.Disposition;

			await client.SendAsync(new Rpc(ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(Endpoints.GameCharInterfaceProjectType, instance),
				GameInterfaceOpcodes.DispatchPassBlob, b => b.WriteUInt32(targetType).WriteUInt32(targetInstance).WriteUInt32(category))); // cCategory: 0 = friendly, 1 = hostile, 2 = neutral

			Logger.LogDebug("RequestAggroStatus(target=0x{Target:X8}) -> cCategory={Category}.", targetInstance, category);
		}

		private async Task HandleRequestBindLocation(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// body = [u32 ID32 type][u32 requesterId]; tolerate a SHORT/empty poll (the client sends a bare-header
			// variant) - the reply is derived from the character, not the body, and requesterId is only for the log.
			// Reading past a short body threw EndOfStreamException, which propagated out of ReceivedPacket and CLOSED
			// the connection (the client dropped at world entry).
			uint requesterId = 0;
			if (body.Length >= 8)
			{
				body.ReadUInt32(); // ID32 type (0x0000C350)
				requesterId = body.ReadUInt32();
			}

			// The bind playfield is the map of the character's persisted bind RespawnPoint (set by .bind / a
			// wayfarer); an unbound character (or one whose point vanished) falls back to its current map.
			var character = client.Session.Character;
			var bindMapId = character?.MapId ?? Map.TortageBeachId;
			if (character?.BindRespawnPointId is { } bindPointId)
				bindMapId = (await Database.GetRespawnPoint(bindPointId))?.MapId ?? bindMapId;

			// Reply framing (sender/receiver swapped from the request), unlike the unsolicited push in
			// SendUpdateBindLocation - retail answers a request on the conversation it arrived on.
			await client.SendAsync(new Rpc(packet.Receiver, packet.Sender, GameCharInterfaceOpcodes.DispatchUpdateBindLocation, s => s.WriteUInt32(bindMapId)));
			Logger.LogDebug("RequestBindLocation({RequesterId:X8}) -> bind playfield {MapId}.", requesterId, bindMapId);
		}

		// ToggleVanityArmorFilter(int slot, uint32 removeditemOccupySlots, bool toggleState): the appearance panel's
		// per-slot (and master) "hide this vanity piece" toggle. Fire-and-forget like sprint - retail sends no reply and
		// the client renders the show/hide itself. Body = [u32 slot][u32 removedItemOccupySlots][u8 toggleState]. We
		// PERSIST it (CharacterEquipment vanity flags) so it survives relog: a withheld vanity item then drops out of
		// the character snapshot equipment (PlayerSpawnEquipment), which is how the client restores the toggle on login.
		private async Task HandleToggleVanityArmorFilter(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;
			if (character == null || body.Length - body.Position < 9)
				return;

			var slot = body.ReadUInt32();
			body.ReadUInt32(); // removedItemOccupySlots (bitmask; 0 across all captures - not needed to persist)
			// Polarity CONFIRMED in-client: checkbox ON (the slot's vanity toggle enabled / its vanity item is
			// shown) sends toggleState=1 and SETS the slot's mask bit - matching the retail vanity-head-selected capture
			// (checkbox on -> bit 2 set). So the mask bit = "this slot's vanity toggle is on"; toggleState maps straight to it.
			var toggleState = Convert.ToBoolean(body.ReadByte());

			var applied = await Database.SetVanityFilter(character.Id, slot, toggleState);
			Logger.LogDebug("ToggleVanityArmorFilter(slot={Slot}, toggleState={toggleState}) {Applied}.", slot, toggleState, applied
				? "persisted"
				: "(unmodelled appearance slot, ignored)");
		}
	}
}
