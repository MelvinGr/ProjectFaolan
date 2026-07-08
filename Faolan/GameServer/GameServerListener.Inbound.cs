using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{

		private async Task Handle0X2000(INetworkClient client, ConanPacket packet)
		{
			// Snapshot the entire 0x2000 sub-payload before consuming it, so every
			// case below can dump the raw bytes for offline reverse-engineering.
			// This is the data starting at the 4-byte pbLength header.
			var rawPayload = packet.Body.ToArray();

			var pbLength = packet.Body.ReadUInt32();
			var pbOpcode = packet.Body.ReadUInt32<GameServer0X2000Opcodes>();

			Logger.LogInformation("GameServer 0x2000 subopcode {SubOpcodeName} ({SubOpcodeHex}), payloadLength={PayloadLength}, rawLength={RawLength}", pbOpcode, pbOpcode.ToHex(), pbLength, rawPayload.Length);
			if (Logger.IsEnabled(LogLevel.Debug))
				Logger.LogDebug("GameServer 0x2000 raw payload for {SubOpcodeName} ({SubOpcodeHex}) ({RawLength} bytes): {RawPayload}", pbOpcode, pbOpcode.ToHex(), rawPayload.Length, rawPayload.ToHex());

			// During the create-editor handshake the client echoes its own instance id as the 0x2000 subopcode.
			// Retail used a fixed id (0x021662C8, or Strie's 0x02165EFF); we hand out the character's real id, so
			// the subopcode is dynamic - match it against the session instance and route it to the echo handler.
			if (client.Session.Character?.Name == null && client.Session.Account != null && (uint)pbOpcode == client.Session.Account.ClientInstance)
			{
				await HandleCreateEditorInstanceEcho(client, packet);
				return;
			}

			switch (pbOpcode)
			{
				case GameServer0X2000Opcodes.CombatReactiveIIR:
				{
					// Spellweave ADD-COMPONENT (docs/combos.md). Each add is two packets: a 0x03-tagged carrier that
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
					if (client.Session.Character?.Name == null)
						Logger.LogInformation("Observed retail create-editor 0xF98E10B3 while pendingPost205F={PendingPost205F}.", client.Session.HasPendingPost205FFollowUp);

					break;
				}

				case GameServer0X2000Opcodes.ScryScriptIIR:
				{
					await _createFlow.HandleCommandPacket(client, packet);
					break;
				}

				case GameServer0X2000Opcodes.QueuePetCommand:
				{
					await HandlePetCommand(client, packet);
					break;
				}

				case GameServer0X2000Opcodes.CharDCMoveIIR:
				{
					var dataobjdec = packet.Body.ReadUInt32();
					var dataclientinst = packet.Body.ReadUInt32();
					var movingType = packet.Body.ReadUInt16<MovingType>();

					switch (movingType)
					{
						case MovingType.MountedRun:
						case MovingType.Walk:
						case MovingType.Jump:
						case MovingType.WalkJump:
						case MovingType.MountedJump:
						case MovingType.Falling:
						case MovingType.Ox011A:
						case MovingType.Ox001E:
						case MovingType.Ox001A:
						case MovingType.Sprint:
						case MovingType.Rotate:
						{
							var sessionCharacter = client.Session.Character;
							if (sessionCharacter == null)
							{
								Logger.LogWarning(
									"Ignoring movement packet {MovingType} because no resolved character is attached to the GameServer session yet. dataobjdec=0x{DataObject:X}, dataclientinst=0x{DataClientInstance:X}.", 									movingType, 									dataobjdec, 									dataclientinst);
								break;
							}

							var movePosition = packet.Body.ReadVector3();
							var moveRotation = packet.Body.ReadVector3(); // not sure if this is correct
							var datadat = packet.Body.ReadRemainingArray();

							// The WorldEntity owns the live transform (docs/archive/server-architecture-roadmap.md); the
							// persisted Character is only the spawn seed (movement is never persisted live). Update the
							// authoritative entity and broadcast from it; fall back to the Character only if the player
							// isn't in the world model yet.
							var moveEntity = PlayerWorldEntity(client);
							if (moveEntity != null)
							{
								moveEntity.Position = movePosition;
								moveEntity.Rotation = moveRotation.ToQuaternion();
							}
							else
							{
								// The DB Character stores a quaternion; the move wire carries a Vector3 (Euler) rotation.
								sessionCharacter.Position = movePosition;
								sessionCharacter.Rotation = moveRotation.ToQuaternion();
							}

							var livePosition = moveEntity?.Position ?? sessionCharacter.Position;
							var liveRotation = moveEntity?.Rotation.ToVector3() ?? sessionCharacter.Rotation.ToVector3();

							// The CharDCMove carrier's movingType u16 is [u8 env][u8 flag1] (docs/chardcmove-grammar.md):
							// the client streams its move with env=0x01 (the "I am the controlling client" flag); the
							// server's echo + relay carry env=0x00. Retail strips it on both directions - verified
							// byte-exact in the other-players-visible capture, where the c2s move and its s2c twin differ
							// ONLY in this byte (c2s 0x011e -> s2c 0x001e). Forwarding the client's 0x01 verbatim made
							// OTHER viewers treat B's move as locally-controlled and over-predict it from the velocity
							// block, so B drifted/teleported even while standing still (#48). Clear the env byte.
							var relayMovingType = (ushort)((ushort)movingType & 0x00FF);

							// send to all (nearby) clients? (single client for now -> the session conversation;
							// retail's move broadcast uses sender 0d13ce71b110 58 + receiver = the moving entity's
							// instance, confirmed across Strie/dikketest/demol80 captures). Interest-managed broadcast to
							// nearby players is the next step.
							await new PacketStream()
								.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
								.WriteArrayUInt32Length(s => s
									.WriteUInt32(pbOpcode)
									.WriteUInt32(dataobjdec)
									.WriteUInt32(dataclientinst)
									.WriteUInt16(relayMovingType)
									.WriteVector3(livePosition)
									.WriteVector3(liveRotation)
									.WriteArray(datadat))
								.SendAsync(client);

							// Interest-managed broadcast: spawn this player to newly-near players and forward the move
							// to players who already see them (docs/archive/server-architecture-roadmap.md). No-op solo.
							await _broadcaster.BroadcastPlayerMove(client, (uint)pbOpcode, dataobjdec, dataclientinst, relayMovingType, livePosition, liveRotation, datadat);

							break;
						}

						default:
						{
							Logger.LogInformation($"Received unknown moving type: {movingType.ToHex()}");
							break;
						}
					}

					break;
				}

				case GameServer0X2000Opcodes.CharInPlayIIR: //peer0_160
				{
					// CharInPlayIIR is ONLY sent by the create-editor (unnamed) client: retail login and the create
					// WORLD entry both use 0x205f, and a dead player's respawn pick is a separate RespawnMe (0x2024
					// GameCharAgent) - all verified in captures. So a named character never reaches this; nothing to do.
					// Retail answers the editor's CharInPlayIIR DIRECTLY with the post-ready burst - the 0x2024
					// ReadyForPlayScreen + CharInPlayIIR + 0xF508F4C1 + the creation script that launches the
					// editor (Blablatest necro create: client CharInPlayIIR at f90 -> server burst at f95). No
					// instance-echo counting or deferral - CharInPlayIIR IS the trigger.
					Logger.LogInformation("Create-editor: CharInPlayIIR received; sending the retail post-ready burst to launch the editor.");
					await CreateFlowService.SendRetailCreateEditorPostReadyBurst(client, client.Session.CreateEditorObservedRequestIds);

					break;
				}

				case GameServer0X2000Opcodes.KnubotAnswerIIR:
				{
					// The player picked a dialogue option. The chosen option INDEX is the last u32 of the body; it
					// selects the branch, so route to the child screen at the extended answer-path (AdvanceConversation).
					// No active conversation -> no-op.
					var answerIndex = rawPayload.Length >= 4
						? BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(rawPayload.Length - 4, 4))
						: 0u;
					await _conversation.AdvanceConversation(client, answerIndex);

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
					var unk0 = packet.Body.ReadUInt32();
					var clientInst = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt16();
					// Some Interact variants are short and carry NO actionType/args (observed: a 10-byte trailer ending
					// `00 08` the client sends around loot/kill). Reading actionType unconditionally threw EndOfStreamException, 					// which ReceivedPacket turns into a hard client.Stop() (disconnect). Bail gracefully on a short body.
					if (packet.Body.Length - packet.Body.Position < sizeof(uint))
					{
						Logger.LogDebug("Interact: short body (unk1=0x{Unk1:X4}), no actionType - ignoring.", unk1);
						break;
					}

					var actionType = packet.Body.ReadUInt32<ActionType>();
					var args = packet.Body.ReadProtobufUInt32Length<ProtoInteractIIR>();

					if (actionType == ActionType.SwitchWeaponsButton)
					{
						// #259: the toggle carries no extra data. Echo-ack the behavior (docs/combat.md rule 1) and toggle
						// the player's combat stance, pushing the CombatMode stat so the client draws/sheathes the weapon -
						// the player analogue of the NPC weapon-draw (#218). NEEDS in-client verification of the exact
						// mechanism (stat push vs behavior-only) against a capture.
						await CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);
						var drawn = _broadcaster.TogglePlayerCombatStance(client);
						await _broadcaster.BroadcastPlayerCombatMode(client, drawn);
						Logger.LogInformation("SwitchWeaponsButton -> combat stance {State}. {Args}", 							drawn
								? "drawn"
								: "sheathed", 							args);
					}
					else
					{
						// Not every action carries from/target after the args (e.g. TalkToNpc only has a short
						// trailer - the NPC handle lives in args.SpellTarget). Read defensively to avoid overrunning.
						var from = packet.Body.Length - packet.Body.Position >= sizeof(uint)
							? packet.Body.ReadUInt32()
							: 0; // fromid or slotid depending on actionType, see below
						var target = packet.Body.Length - packet.Body.Position >= sizeof(uint)
							? packet.Body.ReadUInt32()
							: 0;
						var unk2 = packet.Body.ReadRemainingArray();

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
								var mount = MountCatalog.ForId(from) ?? MountCatalog.Default;
								var playerEntity = PlayerWorldEntity(client);
								var dismounting = playerEntity?.IsMounted ?? false;
								Logger.LogInformation("SummonMount: from=0x{From:X} -> {Mount} ({Action}). {Args}", 									from, 									mount.CreatureName, 									dismounting
										? "dismount"
										: "mount", 									args);

								var self = client.Session.Account.ClientInstance;
								await CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode); // cast begin: bar + animation

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
									if (playerEntity != null)
										playerEntity.IsMounted = false;
									break;
								}

								var mountCastPayload = (byte[])rawPayload.Clone();
								Task.RunAfter(MountCatalog.SummonChannelMs, 									async () => // horse runs up during the channel, then mount
									{
										if (client.Session?.Account == null)
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
											[CharacterStatId.Mount] = mount.RideStat, 											[CharacterStatId.MountSpeed] = 100
										}; // the mount StatUpd (frame 520)
										await SendStatUpdate(client, mountStats, self);
										if (playerEntity != null)
											playerEntity.IsMounted = true;
									}, 									Logger);

								break;
							}

							case ActionType.RightClickItem:
							{
								Logger.LogInformation("RightClickItem: container={Container} slot={Slot} {Args}", from, target, args);

								// Every behavior gets an echo-ack (docs/combat.md rule 1); the item-use domain logic
								// lives in UseInventoryItem (GameServerListener.ItemUse.cs). The used item is identified by its inventory location - from =
								// container (0x68 = backpack), target = slot - decoded from the use_totem_of_origin
								// capture (using a Totem of Origin recalls to the bind point, #206; the recall's handoff
								// is the capture's "long loading screen").
								await CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);
								await UseInventoryItem(client, (int)from, (int)target);

								break;
							}

							case ActionType.CastSpell:
							{
								// The real spell id rides the action TRAILER (`target`, the uint32 read after the protobuf
								// args), NOT args.SpellTarget.SpellId - that protobuf field is the constant 0xC350 type-word, 								// so casts logged as "spell 50000" and ApplySpell carried a non-existent id (no effect shown).
								// Decoded from the ice-strike capture (c2s 0x642CD3D6 cast): trailer = [u32 from=1][u32 spellId];
								// 0x125D9B / 0x42CA77 are real learned-spell ids. args.SpellTarget.TargetHandle is the target. (#236)
								var spellId = target != 0
									? target
									: (uint)(args.SpellTarget?.SpellId ?? args.TypeId);
								var casterHandle = client.Session.Account.ClientInstance;
								var targetHandle = (uint)(args.SpellTarget?.TargetHandle ?? 0);

								Logger.LogInformation($"CastSpell spell=0x{spellId:X} target=0x{targetHandle:X} {args}");

								// Cast-begin ack (#236): retail answers the cast behavior with a BCC echo right away, which
								// is what makes the client enter the channel state + render the cast bar (without it the
								// client never channels - it sent only Mode 1, no progress ticks - and the cast looks
								// instant). Ack every cast packet (begin + channel ticks); the effect itself is resolved
								// once, below, after the cast time.
								await CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);

								// Honor the spell's cast time (#236): retail resolves the effect only AFTER the cast time
								// elapses (ice-strike capture: cast at frame 377, ApplySpell impact ~frame 441). Resolving on
								// receipt skipped the client's cast bar. CastTime 0 (instant spell, or an un-populated row)
								// resolves immediately. NB cast interruption on movement is not modelled yet (interim).
								var spell = await Database.GetSpell(spellId);
								var castMs = spell?.CastTime ?? 0;
								if (castMs == 0)
									await Resolve();
								else
								{
									Task.RunAfter(castMs, 										async () => // resolve the effect after the cast time (#236)
										{
											if (client.Session?.Account != null)
												await Resolve();
										}, 										Logger);
								}

								break;

								// Resolves the cast: play the ApplySpell (caster -> target, or self when no target), drain
								// mana, and on an offensive cast apply damage + push the target's vitals (#69).
								async Task Resolve()
								{
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
									// just the .pet GM command. Decoded from the necro-pet capture (docs/summon-pet-decode.md):
									// cast (CastSpell) -> after the channel -> FA015807 pet spawn + PetIIR. PetSummonCatalog maps
									// the summon spell id -> pet template + kind.
									if (PetSummonCatalog.ForSpell(spellId) is { } petSummon)
									{
										var pet = await SummonPetForPlayer(client, petSummon.PetTemplate, petSummon.Combat);
										Logger.LogInformation("CastSpell 0x{Spell:X} pet-summon '{Template}' -> {Result}.", 											spellId, 											petSummon.PetTemplate, 											pet != null
												? $"0x{pet.Handle:X8}"
												: "no spawnable template (pet-creature import needed)");
										return;
									}

									await ApplySpell(client, 										casterHandle, 										targetHandle == 0
											? casterHandle
											: targetHandle, 										spellId);
									await _combat.SpendCasterMana(client, spellId);
									if (targetHandle != 0 && targetHandle != casterHandle)
										await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId);
								}
							}

							case ActionType.NormalAttack:
							{
								// The melee swing / auto-attack: ack the behavior + resolve the hit (see
								// SendNormalAttackResponse). The target is in the request args (SpellTarget.TargetHandle).
								await _combat.SendNormalAttackResponse(client, rawPayload, (uint)(args.SpellTarget?.TargetHandle ?? 0), (uint)args.Mode);

								break;
							}

							case ActionType.UnarmedAttack:
							{
								// Unarmed swing (empty main hand; behavior TypeIds 1089xx, ability id in the tail).
								// Retail's reply is the plain behavior echo-ack - no result block (combat capture
								// 2026-06-12); targeted damage reuses the armed path (the capture's unarmed swings
								// hit air, so the damage part is an extension pending in-client validation).
								await _combat.SendUnarmedAttackResponse(client, rawPayload, (uint)args.Mode, (uint)(args.SpellTarget?.TargetHandle ?? 0));

								break;
							}

							case ActionType.Emote:
							{
								Logger.LogInformation($"Emote: {args}");

								break;
							}

							case ActionType.UseObject:
							{
								// Using a world object - the loot path: `target` is the object handle (its typetag, 								// 0xC87A for loot bags, rides in `from`). Opens the bag if it's a loot bag in the zone.
								await _broadcaster.OpenLootBag(client, target);

								break;
							}

							case ActionType.TalkToNpc:
							{
								// The clicked NPC's entity handle rides in the interact args (field 10 / field 2);
								// (TypeId, Mode, Field3) select the conversation node the client is requesting.
								var npcHandle = (uint)(args.SpellTarget?.TargetHandle ?? 0);
								Logger.LogInformation("TalkToNpc: from=0x{From:X}, target=0x{Target:X}, npcHandle=0x{NpcHandle:X}, args={Args}", from, target, npcHandle, args);

								// Vendor trade-open (#134): when the clicked NPC is a shop vendor (matched by NAME - the
								// stable key), retail answers the interact itself with the trade-select echo + bare BCC
								// echo + DispatchShopData riding this conversation (sell_items_to_trader frames
								// 1188-1193) - no knubot screens in between. Checked on the greeting click only
								// (Field3 == 0, like the captured open).
								if (npcHandle != 0 && args.Field3 == 0 && client.Session.SpawnedNpcNamesByHandle.TryGetValue(npcHandle, out var vendorNpcName))
								{
									var vendorShop = await Database.GetShopByVendorName(vendorNpcName);
									if (vendorShop != null)
										await _shop.HandleVendorTradeOpen(client, rawPayload, (uint)args.Mode, vendorShop, npcHandle);
									else
										await _agentServerDispatcher.SendSystemMessage(client.Session.Account!.ClientInstance, "Shop not yet imported");

									break;
								}

								// Real branching dialogue: replay the captured conversation node for whichever NPC/node
								// the client asked for, re-addressed to the clicked NPC.
								if (await _conversation.TryReplayConversation(client, (uint)args.TypeId, (uint)args.Field3, npcHandle))
									break;

								// Only a GREETING click (Field3 == 0) may OPEN a dialogue. The client fires a second
								// TalkToNpc with Field3 != 0 the same instant it opens one (retail answers it with a bare
								// ack); if that duplicate isn't absorbed above (no active conversation - e.g. right after a
								// close) it must NOT re-open the intro/greeting, or the dialogue "triggers a second time"
								// (#217). A non-greeting click with nothing to absorb is a no-op.
								if (args.Field3 != 0)
									break;

								// Last-resort fallback ONLY when the DB has no node for this NPC: the single hardcoded
								// post-creation intro (conversation 12493). It is NPC-agnostic, so it shows the same text
								// for every NPC - the DB path above is what routes the correct dialogue per NPC.
								if (client.Session.IsCreateWorldSession && npcHandle != 0)
									await _createFlow.SendNpcIntroConversation(client, npcHandle);
								// Beach login with no captured node for this (Mode, Field3): close the chat window so the
								// client doesn't hang waiting for a reply (the "advance a step -> chat locks, can't ESC"
								// bug). Conversation replay is coverage-bound, so an un-captured branch is expected.
								else if (client.Session.WorldEntered)
								{
									var routedName = client.Session.SpawnedNpcNamesByHandle.TryGetValue(npcHandle, out var rn)
										? rn
										: "<unmapped handle>";
									Logger.LogInformation("TalkToNpc: no conversation node for npcHandle=0x{Npc:X} (name={Name}, typeId={TypeId}, mode={Mode}, field3={Field3}); closing the window.", 										npcHandle, 										routedName, 										args.TypeId, 										args.Mode, 										args.Field3);
									await ConversationService.SendConversationClose(client, npcHandle); // #217: close the CLICKED NPC's window (re-address the camera release)
								}

								break;
							}

							case ActionType.MoveEquippedToFromInventory:
							{
								// bag slot starts top left, continue counting on next page

								var direction = (MoveItemInInventoryDirection)args.TypeId;
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
										await CombatService.SendEquipMoveAck(client, rawPayload, (uint)args.Mode, movedItem);
										await Send0X201CEntry(client, LoginWorld201CSender, LoginWorld201CReceiver(client), 0x00000065, target, 0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

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
										await CombatService.SendEquipMoveAck(client, rawPayload, (uint)args.Mode, null);
										await Send0X201CEntry(client, LoginWorld201CSender, LoginWorld201CReceiver(client), 0x00000068, 0, 0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

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

								// Robustness (#236 channeled cast 0x15, #238 dodge hop): an un-decoded behavior must
								// STILL get the generic behavior echo-ack (docs/combat.md rule 1), or the client stays
								// stuck in the action's start state forever (cast bar never finishes / dodge never lands
								// back to idle) and the player soft-locks until relog. Acking with the request's Mode
								// returns control even when we don't yet model the action's effect.
								await CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);

								break;
							}
						}

						// #48: the combat responses above are framed to the ACTOR only - so the swing/cast was invisible
						// to other players. Relay the actor's behavior (the BCC animation) to nearby viewers so the attack
						// renders on their clients too (retail relays this exact echo body, only the frame receiver
						// differs - other-players-visible capture). Limited to the world-visible combat behaviors.
						if (actionType is ActionType.NormalAttack or ActionType.UnarmedAttack or ActionType.CastSpell)
						{
							var behaviorRelay = BehaviorAckBody.BuildBehaviorRelayBody(client.Session.Account.ClientInstance, rawPayload, (uint)args.Mode);
							if (behaviorRelay != null)
								await _broadcaster.BroadcastBehaviorToViewers(client, behaviorRelay);
						}
					}

					break;
				}

				case GameServer0X2000Opcodes.CharacterActionIIR: // Also received when Activating feat point ??
				{
					var unk0 = packet.Body.ReadUInt32();
					var clientInst = packet.Body.ReadUInt32();

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
					var data = packet.Body.ReadRemainingArray();

					uint targetHandle = 0;
					if (data.Length >= 17)
						targetHandle = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(13, 4));
					client.Session.SelectedTargetHandle = targetHandle;
					// #217: a (re-)select is genuine intent to interact - lift the post-close greeting suppression so a
					// deliberate re-talk opens (the reflexive post-close greeting carries no SelectDeselect).
					client.Session.ConversationEndedNpcHandle = 0;

					Logger.LogInformation("SelectDeselect: target 0x{Target:X8} ({Data})", targetHandle, data.ToHex());

					break;
				}

				case GameServer0X2000Opcodes.DeleteItemIIR:
				{
					var unk0 = packet.Body.ReadUInt32(); // c350
					var clientInst = packet.Body.ReadUInt32();
					var reserved = packet.Body.ReadUInt32();
					var move = packet.Body.ReadProtobufByteLength<ProtoMoveInventoryItemIIR>();

					Logger.LogInformation($"DropItem: {move}");

					// Remove the dropped item from the persisted bag so it doesn't reappear on relog.
					await _equipment.PersistDropItem(client, move);

					// Push the LIVE remove so the open bag updates without a relog (#230): the DropItem opcode has no
					// implicit client-side removal, so without this the dropped item stays visible until next login.
					// Same DispatchRemoveItem (0x201F) the .removeitem GM path / RemoveItemFromBackpack use.
					if (move?.SourceContainerId is { Type: InventoryContainerBackpack })
						await SendRemoveItem(client, InventoryContainerBackpack, (uint)move.SourceItemPos);

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

					var protocolConstant = packet.Body.ReadUInt32();
					var characterHandle = packet.Body.ReadUInt32();
					var reserved = packet.Body.ReadUInt32();
					var move = packet.Body.ReadProtobufByteLength<ProtoMoveInventoryItemIIR>();

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
					var unk0 = packet.Body.ReadUInt32();
					var clientInst = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadByte();
					var unk2 = packet.Body.ReadUInt32();
					var unk3 = packet.Body.ReadUInt32();
					var questId = packet.Body.ReadUInt32(); // probably
					var unk4 = packet.Body.ReadUInt32();

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
					var data = packet.Body.ReadRemainingArray();

					break;
				}

				case GameServer0X2000Opcodes.TradeskillRequestIIR:
				{
					// The client opened the Crafting -> Recipes book and is asking for its recipe list. Answer with
					// the TradeskillReplyIIR (0x9062D559) catalog from the DB so the book populates. See
					// TradeskillRecipeCodec / docs/tradeskill-recipes.md.
					_ = packet.Body.ReadRemainingArray();
					await SendCraftingRecipeBook(client);

					break;
				}

				default:
				{
					var data = packet.Body.ReadRemainingArray();

					// In-world the client streams 0x2000 packets whose sub-opcode is its OWN instance id
					// (the client-instance ack/keepalive stream). With the create flow now using the real
					// character id, that id is dynamic (not a fixed enum value like the old 0x021662C8), so
					// it lands here. Treat it as a benign ping: log quietly, don't spam system messages.
					if ((uint)pbOpcode == client.Session.Account.ClientInstance || (uint)pbOpcode == client.Session.Character?.Id)
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

					Logger.LogWarning("Unhandled GameServer 0x2000 subopcode {SubOpcodeHex} ({RawLength} bytes).", pbOpcode.ToHex(), rawPayload.Length);
					if (Logger.IsEnabled(LogLevel.Debug))
						Logger.LogDebug("Unhandled GameServer 0x2000 subopcode {SubOpcodeHex} raw payload: {RawPayload}", pbOpcode.ToHex(), rawPayload.ToHex());
					await _agentServerDispatcher.SendSystemMessage(client.Session.Account.ClientInstance, $"Unknown 0x2000 Opcode: {pbOpcode.ToHex()} ({rawPayload.Length} bytes)");

					break;
				}
			}
		}

		// Handles the create-editor "instance echo": the client repeats its own client instance id as a 0x2000
		// subopcode while the editor is loading. We count these to time the post-0x205F and post-ready bursts, 		// exactly as retail does, and ack standalone ones. (Retail used a fixed instance; we hand out the
		// character's real id, so the matching is done dynamically by the caller.)
		private async Task HandleCreateEditorInstanceEcho(INetworkClient client, ConanPacket packet)
		{
			if (client.Session.Character?.Name != null)
				return;

			uint? requestId = null;
			if (packet.Body.Length - packet.Body.Position >= sizeof(uint) * 2)
			{
				var ack0 = packet.Body.ReadUInt32();
				requestId = packet.Body.ReadUInt32();
				Logger.LogInformation("Create-editor instance-echo decoded: ack0={Ack0:X}, requestId={RequestId:X}.", ack0, requestId.Value);

				if (!client.Session.CreateEditorObservedRequestIds.Contains(requestId.Value))
					client.Session.CreateEditorObservedRequestIds.Add(requestId.Value);
			}

			// Retail just ACKs the client's create-editor instance-echoes (there is no special post-0x205F burst); the
			// burst that actually launches the editor is sent on CharInPlayIIR. So an echo carrying a request id gets a
			// plain ack, and one without is ignored.
			if (requestId.HasValue)
			{
				Logger.LogInformation("Acknowledging create-editor instance-echo with requestId={RequestId:X}.", requestId.Value);
				await CreateFlowService.SendRetailCreateEditorPostReadyAck(client, requestId.Value);
			}
			else
				Logger.LogInformation("Ignoring create-editor instance-echo with no request id.");
		}

		// GameAgent (connection-class) dispatch: auth / chat / ping / server-stats. Routed here by receiver class.
		private async Task HandleGameAgentPacket(INetworkClient client, ConanPacket packet)
		{
			var gaOp = (GameAgentOpcodes)packet.Opcode;
			if (gaOp is not GameAgentOpcodes.UpdateGameServerStats)
				LogPacketReceived(packet, gaOp);

			switch (gaOp)
			{
				case GameAgentOpcodes.Authenticate2:
				{
					// 0x2003 = GameAgent.Authenticate2 (the connection Hello: clientInstance + accountId + version).
					// The GameCharAgent.SetTutorialStats collision on this opcode is resolved by the class-first route
					// above, so here the body is always the >= 12-byte Authenticate2. Guard defensively anyway: a short
					// body would over-read and drop the client.
					if (packet.Body.Length - packet.Body.Position < 12)
					{
						Logger.LogWarning("GameAgent.Authenticate2 (0x2003) with a short {Bytes}-byte body; ignoring.", packet.Body.Length - packet.Body.Position);
						break;
					}

					var clientInstance = packet.Body.ReadUInt32(); // 0x0802e5d4 0x1101b4f5
					var accountId = packet.Body.ReadUInt32(); // 0x310cec57 0x2b68ea25=728295973
					var clientVersion = packet.Body.ReadString();

					client.Session.Account = await Database.GetAccount(accountId);
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					var resolvedCharacter = await Database.GetCharacterForAccount(accountId, clientInstance);
					client.Session.Account.ClientInstance = clientInstance;
					client.Session.Character = resolvedCharacter;
					if (resolvedCharacter?.Id != clientInstance)
					{
						Logger.LogInformation(
							"GameServer Hello: resolved temporary client instance {ClientInstance:X8} to pending character {CharacterId:X8} for account {AccountId:X8} while preserving the retail temporary instance for create-flow traffic.", 							clientInstance, 							resolvedCharacter?.Id ?? 0, 							accountId);
					}

					Logger.LogInformation("GameServer hello: clientInstance={ClientInstance:X8}, clientVersion={ClientVersion}", clientInstance, clientVersion);

					/*if (clientVersion != "v4.00.NoTS@369764")
					{
						Logger.Info("Incompatible client connects to the Server");
						//break;
					}*/

					// Character creation has two phases on this GameServer connection:
					//  1) CREATOR: the client connects on the temp instance 0x021662C8 and we send the
					//     verbatim editor burst (RetailCreateEditorInitialBurst) so the creator UI appears.
					//     Auth replies use the editor conversation family so the client associates the burst.
					//  2) ENTER-WORLD: after the player customises + names, the world entry is sent (handled
					//     out of the post-0x205F / ReadyForPlayScreen path), not here.
					// Existing-character login uses the legacy conversation family + the world entry.
					var isNewCharacter = client.Session.Character.Name == null;

					// Post-creation WORLD connection: the client reconnects with the same (now named) character
					// instance, but this is the SECOND GameServer connection - the one opened after the 0x2002
					// enter-world handoff. It is recognised by the character id being in PendingCreateWorldEntry
					// (added when the name was committed on the editor connection). Consume it here so a later
					// normal login of the same character does not re-trigger the intro. This connection needs the
					// full retail creation world entry (the verbatim burst with its own 0x200B/0x200C/0x2001
					// auth + the intro/cutscene), so skip the helper auth for it.
					var isCreateWorldEntry = !isNewCharacter && CreateFlowService.TryConsumePendingCreateWorldEntry(clientInstance);
					client.Session.IsCreateWorldSession = isCreateWorldEntry;
					client.Session.IsCreateEditorConnection = isNewCharacter;

					if (!isCreateWorldEntry)
					{
						// New (unnamed) characters take the create-editor path; existing characters log in normally.
						var authSender = isNewCharacter
							? RetailCreatePost205FSender
							: null;
						var authReceiver = isNewCharacter
							? CreateEditorFraming.Receiver(client)
							: null;

						await ReportDimensionId(client, client.Session.Character.RealmId, authSender, authReceiver); // 1
						await ReportServerId(client, 1, authSender, authReceiver); // 0x00000006
						await AckAuthenticate(client, 1, authSender, authReceiver);
					}

					client.Session.LastGameServer2020RequestId = null;
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
						var spawnRows = await Database.SetCreationSpawn(client.Session.Character);
						Logger.LogInformation(
							"Post-creation world entry (clientInstance={ClientInstance:X8}, character={CharacterId:X8} '{Name}'): set creation spawn (rows={Rows}, pos=({X},{Y},{Z}), map={Map}); sending the creation world SCENE burst.", 							clientInstance, 							client.Session.Character.Id, 							client.Session.Character.Name, 							spawnRows, 							client.Session.Character.PositionX, 							client.Session.Character.PositionY, 							client.Session.Character.PositionZ, 							client.Session.Character.MapId);
						// #234 reconstruct (docs/handoff-2026-06-22-create-flow-reconstruct.md): the verbatim
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
						await RetailCreateEditorInitialBurst.SendRetailCreateEditorInitialBurst(client, Database);
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
							client.Session.HasPendingPost205FFollowUp = true;
							Logger.LogInformation(
								"Teleport re-entry (clientInstance={ClientInstance:X8}): armed the post-0x205F follow-up so 0x2024 ReadyForPlayScreen flushes on the next 0x2020 (no client 0x205F on a changemap reconnect).", 								clientInstance);
						}
					}

					break;
				}

				case GameAgentOpcodes.PassBlob:
				{
					await Handle0X2000(client, packet);

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
					LogPacketBodyDebug("GameServer ChatCommand body", packet.Body?.ToArray());

					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var unk2 = packet.Body.ReadUInt32();
					var unk3 = packet.Body.ReadUInt32();
					var text = packet.Body.ReadString();

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
					// Resolve the method name from the (receiver class, opcode) pair (the GameServer multiplexes
					// several RPC classes, so the opcode alone is ambiguous) - logs the name, not bare hex.
					LogUnknownPacket(packet, packet.Opcode, GameServerMethodNames.Resolve(packet.Receiver.Type, packet.Opcode));
					break;
				}
			}
		}

		// GameCharAgent dispatch: the 0x2002/0x2003 collision forms (bind/tutorial) + every per-character method
		// (loot / shop / respawn / shortcut / latency / ...). Routed here by receiver class from ReceivedPacket.
		private async Task HandleGameCharAgentPacket(INetworkClient client, ConanPacket packet)
		{
			var charOp = (GameCharAgentOpcodes)packet.Opcode;
			if (charOp is not GameCharAgentOpcodes.Ping)
				LogPacketReceived(packet, charOp);

			switch (charOp)
			{
				case GameCharAgentOpcodes.RequestBindLocation: // 0x2002 = RequestBindLocation
				{
					await HandleRequestBindLocation(client, packet);
					return;
				}

				case GameCharAgentOpcodes.SetTutorialStats: // 0x2003 = SetTutorialStats (Tortage tutorial tracking)
				{
					var tutorialStat = packet.Body.Length - packet.Body.Position >= sizeof(uint)
						? packet.Body.ReadUInt32()
						: 0;
					Logger.LogDebug("GameCharAgent.SetTutorialStats(0x{Value:X8}); no reply.", tutorialStat);
					return;
				}

				case GameCharAgentOpcodes.SetHideInspectOption:
				{
					if (packet.Body != null)
					{
						LogPacketBodyDebug("GameServer 0x207E body", packet.Body.ToArray());
						if (packet.Body.Length > 0)
							Logger.LogInformation("0x207E decoded: value0={Value0:X2}", packet.Body.ToArray()[0]);
					}

					break;
				}

				case GameCharAgentOpcodes.CinematicTransition:
				{
					var unk0 = packet.Body.ReadUInt32(); // 0xc350
					var characterId = packet.Body.ReadUInt32();

					if (IsRetailCreateEditorFlow(client))
					{
						// Create-editor phase: retail sends no download in response to 0x205F - it just acks the
						// client's polls and waits for CharInPlayIIR (which triggers the editor-launch burst).
						client.Session.HasPendingPost205FFollowUp = true;
						Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}. Create-editor phase; awaiting CharInPlayIIR.", unk0, characterId);
						break;
					}

					client.Session.HasPendingPost205FFollowUp = true;
					Logger.LogInformation("0x205F decoded: unk0={Unk0:X}, characterId={CharacterId:X8}, expectedCharacterId={ExpectedCharacterId:X8}. Marked post-0x205F follow-up as pending.", unk0, characterId, characterId);

					break;
				}

				case GameCharAgentOpcodes.UpdateQuestMarkerNPC:
				{
					if (packet.Body?.Length >= sizeof(uint) * 4)
					{
						var ack0 = packet.Body.ReadUInt32();
						var requestId = packet.Body.ReadUInt32();
						var ack1 = packet.Body.ReadUInt32();
						var characterId = packet.Body.ReadUInt32();
						client.Session.LastGameServer2020RequestId = requestId;

						// #245: the poll's requestId is a handle the client is CONFIRMING it built - record it so interest
						// only ever despawns confirmed handles (a despawn for an unbuilt handle crashes a still-loading
						// client: pcDynel == null). The self-handle poll is harmless (the player never despawns itself).
						_simulation.PlayerWorldEntity(client)?.Confirmed.Add(requestId);

						// 0x2020 is the client polling a spawned entity (requestId = the entity handle). Retail
						// answers each with a 0x2008 SpawnCheck echoing that handle (verified: dikketest world conn
						// polls Kalanthes 0x535A0 and gets a SpawnCheck for 0x535A0). This is what completes/renders
						// each entity. Both existing-character login (burst-less world entry) AND post-creation world
						// entry need it: the create-world flow's old hand-built batch answered the wrong, hardcoded
						// capture handles (0x818xx) on Strie's instance, so its entities (incl. Kalanthes) were never
						// confirmed and the intro cinematic ("invalid camera path Kalanthes") failed.
						if (client.Session.WorldEntered || client.Session.IsCreateWorldSession)
							await SendSpawnCheckResponse(client, requestId);

						Logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}", ack0, requestId, ack1, characterId);
						Logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}", 							ack0, 							requestId, 							ack1, 							characterId, 							client.Session.HasPendingPost205FFollowUp);
						await FlushPendingPost205FFollowUp(client);
						packet.Body.Position = 0;
					}
					else
						LogPacketBodyDebug("GameServer 0x2020 short body", packet.Body?.ToArray());

					break;
				}

				case GameCharAgentOpcodes.DoSkillSellback:
				{
					if (packet.Body != null)
						LogPacketBodyDebug("GameServer 0x205C body", packet.Body.ToArray());

					break;
				}

				case GameCharAgentOpcodes.PlayStockphraseAsSubtitle:
				{
					// #234 create-cutscene speech: the client is asking us to resolve a stock phrase
					// ({stock_N}) and BLOCKS up to 60 s for the 0x205D reply. Answer it (Kalanthes's beach
					// lines) instead of silently timing out. See CreateFlow/StockPhraseResponder.cs.
					if (packet.Body != null)
						await _createFlow.HandlePlayStockphraseAsSubtitle(client, packet.Body.ToArray());

					break;
				}

				case GameCharAgentOpcodes.RequestShortcutBar: // client asks for its saved hotbar at login; reply with the restored bar (#71)
				{
					await _shortcutBar.SendRestoredBar(client);

					break;
				}

				case GameCharAgentOpcodes.SellItem:
				{
					await _shop.HandleSellItemToShop(client, packet);

					break;
				}

				case GameCharAgentOpcodes.UndoLastSoldItem:
				{
					await _shop.HandleUndoLastSoldItem(client);

					break;
				}

				case GameCharAgentOpcodes.BuyItem:
				{
					packet.Body.ReadUInt32(); // shop ID32 type (0xC75B)
					var shopId = packet.Body.ReadUInt32();
					var pos = packet.Body.ReadUInt32();

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
					await _loot.HandleLootAll(client, packet);

					break;
				}

				case GameCharAgentOpcodes.TryLootItem: // single drag of one item out of the loot window
				{
					await _loot.HandleLootItem(client, packet);

					break;
				}

				case GameCharAgentOpcodes.CloseLootBag: // client closed the loot window; body = [typetag][bag handle]
				{
					await _loot.HandleCloseLootBag(client, packet);

					break;
				}

				case GameCharAgentOpcodes.TryLootCash: // loot the bag's coins; fires on every loot alongside TryLootAll
				{
					await _loot.HandleLootCash(client, packet);

					break;
				}

				case GameCharAgentOpcodes.RespawnMe: // the player's release/resurrect click; body = [u32 token]
				{
					var token = packet.Body?.Length >= sizeof(uint)
						? packet.Body.ReadUInt32()
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

				case GameCharAgentOpcodes.CancelBuff: // right-click-cancel a buff; body = [u32 buffId][u32 charId]
				{
					var buffId = packet.Body.ReadUInt32();
					var charId = packet.Body.ReadUInt32();

					// The client removes its own icon (retail replies only a FormatFeedback text, no removal
					// IIR); removing the entry from the server-side registry is what makes the cancel real.
					var entity = _simulation.PlayerWorldEntity(client);
					if (entity != null && entity.ActiveBuffs.TryRemove(buffId, out _))
						Logger.LogInformation("CancelBuff: buff 0x{Buff:X8} removed from {CharId:X8}.", buffId, charId);
					else
						Logger.LogDebug("CancelBuff: buff 0x{Buff:X8} was not tracked for {CharId:X8} (cast before the registry, or an aura).", buffId, charId);

					break;
				}

				case GameCharAgentOpcodes.Ping: // latency check (client 0x2085 -> server 0x2082)
				{
					await KeepAliveService.HandleLatencyCheck(client, packet);

					break;
				}

				case GameCharAgentOpcodes.MoveTradeItemInternal: // player-to-player trade window item move; not implemented
				{
					/*byte[] sender = { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x14 };
						    byte[] receiver = { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0x84, 0x80, 0x80, 0x08 };

						    PacketBuffer aBuffer = new PacketBuffer();
						    aBuffer.WriteHeader(sender, receiver, null, 0x2009, true);
						    aBuffer.WriteUInt32(0x00000067);
						    aBuffer.WriteUInt32(GameServer0X2000Opcodes.N3TeleportIIR);
						    aBuffer.WriteUInt32(0x0000c350);
						    aBuffer.WriteUInt32(client.client.Account.nClientInst);
						    aBuffer.WriteByte(0);
						    aBuffer.WriteUInt64(0);
						    aBuffer.WriteUInt64(0);
						    aBuffer.WriteUInt64(0);
						    aBuffer.WriteUInt32(0x3f800000);
						    aBuffer.WriteByte(0x62);
						    aBuffer.WriteUInt32(0x0000c79c);
						    aBuffer.WriteUInt32(0x00000faa); //map
						    aBuffer.WriteUInt32(client.client.Account.nClientInst);
						    aBuffer.WriteUInt32(0x0000000a);
						    aBuffer.WriteUInt32(0x00009c50);
						    aBuffer.WriteUInt32(0x00030bde); //instance
						    aBuffer.WriteUInt64(0);
						    aBuffer.WriteUInt32(0x00000014);
						    aBuffer.WriteUInt32(0x00018704);
						    aBuffer.WriteUInt32(0x43a14000);
						    aBuffer.WriteUInt32(0x43160000);
						    aBuffer.WriteUInt32(0x4430399a);
						    aBuffer.WriteUInt32(0x00000032);
						    aBuffer.WriteByte(0);
						    //Log.Warning("Send Packet:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
						    aBuffer.Send(client);

						    aBuffer = new PacketBuffer();
						    aBuffer.WriteHeader(sender, receiver, null, 0x2002, true);
						    //aBuffer.WriteUInt32(htonl(inet_addr(realm->worldServerIPAddress)));
						    //aBuffer.WriteUInt16(realm->worldServerPort);
						    aBuffer.WriteUInt32(Native.Network.htonl(Native.Network.inet_addr("127.0.0.1")));
						    aBuffer.WriteUInt16(Program.GameServerPort);
						    aBuffer.Send(client);*/

					//var packetData = packet.Data?.ToArray().ToHexString();

					break;
				}

				case GameCharAgentOpcodes.GetInstanceListForCurrentPlayfield: // When the 'Select instance' button is clicked
				{
					LogPacketBodyDebug("GameServer 0x207B body", packet.Body?.ToArray());

					//Logger.Info("Receive opcode 0x207b. Maybe hide object(helmet,...)");

					break;
				}

				case GameCharAgentOpcodes.PerformSpecialAction:
				{
					// 0x205A PerformSpecialAction is a GENERIC action opcode multiplexed by its body word, NOT a
					// spellweave-only toggle: 0xFC = the necro spellweave activate/deactivate toggle (all 3 NecroL80
					// spellweave captures), 0xFF = open the Crafting -> Recipes book, 0x6C/0x6D/0x70/0xF0 = melee
					// combo inputs (Hoxxling/AssassinL23 combo captures). Only 0xFC drives the trance - blindly routing
					// every 0x205A to spellweave made opening the recipe book start a weave on a necro.
					var counter = packet.Body.ReadUInt32();

					Logger.LogInformation("0x205A (PerformSpecialAction) param={Counter:X4}", counter);
					if (counter == SpellweaveToggleParam)
						await _spellweave.ToggleAsync(client, counter);

					break;
				}

				case GameCharAgentOpcodes.StackInventoryItemIfPossible: // (the old C++ docs called this "SpawnCheck", p.160-161)
				{
					LogPacketBodyDebug("GameServer StackInventoryItemIfPossible body", packet.Body?.ToArray());

					/*var part1 = packet.Data.ReadUInt32();
					var spawnId = packet.Data.ReadUInt32();
					var unk0 = packet.Data.ReadUInt32();
					var nClientInst = packet.Data.ReadUInt32();

					new PacketStream()
						.WriteHeader(Sender9, Receiver9, null, GameCharInterfaceDispatchOpcodes.DispatchSetQuestMarker)
						.WriteUInt32(part1)
						.WriteUInt32(spawnId)
						.WriteByte(0)
						.Send(client);*/

					break;
				}

				case GameCharAgentOpcodes.SplitInventoryItem:
				{
					var unk0 = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt32();
					var sourceSlot = packet.Body.ReadUInt32();
					var targetStackSize = packet.Body.ReadUInt32();
					var targetSlot = packet.Body.ReadUInt32();

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
					await _shortcutBar.HandleRemoveShortcut(client, packet);

					break;
				}

				case GameCharAgentOpcodes.UpdateShortcut: // body = [u32 slot][u32 flag][u32 container]
				{
					await _shortcutBar.HandleUpdateShortcut(client, packet);

					break;
				}

				case GameCharAgentOpcodes.AddShortcut:
				{
					// 0x206E = MoveItemToBar: ALWAYS a bar drag ([u32 type][u32 slot][u32 id][u32 unk]). During create
					// the client populates the NEW character's starting bar with it right after CharCreationFinished
					// (verified in the Blablatest/Hoxxling create captures: the starting abilities + spells at slots
					// 0x64.. / 0x4E20..), BEFORE entering the world - so persist it on the editor connection too, or a
					// fresh character loses its retail starting bar and falls back to our seeded default.
					await _shortcutBar.HandleAddShortcut(client, packet);

					// On the editor connection the FIRST post-world-prep 0x206E also signals "ready to enter world":
					// answer it once with the 0x2002 handoff. #234: wash the new char ashore first with a changemap-prep
					// (IntendedTeleport 0x10D27BC0 + n3Teleport 0x5A32F0D7) to the Tortage Beach spawn - Blablabla create
					// capture f444/449 (dest map 0x0FAA, pos 322.5,150,704.9, descriptor 0x32) - else the player stays
					// at the drowning cutscene's underwater end position. Mirrors GMCommands.DoTeleport (prep -> handoff).
					if (client.Session.IsCreateEditorConnection && client.Session.AwaitingEnterWorld)
					{
						client.Session.AwaitingEnterWorld = false;
						await SendChangeMapPrep(client, Map.TortageBeachId, Map.NewPlayerSpawnSpawnPoint.Position, 0x32);
						await SendWorldHandoff(client);
					}

					break;
				}

				default:
				{
					// Resolve the method name from the (receiver class, opcode) pair (the GameServer multiplexes
					// several RPC classes, so the opcode alone is ambiguous) - logs the name, not bare hex.
					LogUnknownPacket(packet, packet.Opcode, GameServerMethodNames.Resolve(packet.Receiver.Type, packet.Opcode));
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
		// loading screen forever - which is why it never opened the AgentServer/CommunityServer (chat) connection.
		// Decoded from the kalantes_chat retail capture (port 7015):
		//   req  C->S: recv=GameCharAgentProject, send=GameCharInterfaceProject/<player>, op 0x2000, 		//              body [u32 0xC350][u32 charInstance][u32 0xC350][u32 targetInstance]   (two ProtoID32s)
		//   resp S->C: send=GameCharAgentProject/<zone handle 0x58>, recv=GameCharInterfaceProject/<player>, op 0x2000, 		//              body [u32 0xC350][u32 targetInstance][u32 cCategory]
		// The 3rd field is the target's overhead NAME-COLOUR category (0 friendly / 1 hostile / 2 neutral), the SOLE
		// driver of the red/green/yellow name (proven by live gdb, #256) - not a live-aggro flag. We answer every
		// poll (still unblocks the load) and now set 1 for hostile NPCs so their names render red like retail.
		private async Task HandleGameCharAgentProject(INetworkClient client, ConanPacket packet) // TODO: convert to proper switch on opcode
		{
			var opcode = packet.Opcode;
			if (opcode != 0x2000)
			{
				// SetSprintInputState (0x200C, the sprint key): a fire-and-forget input-state update retail does NOT
				// reply to (#72). Body = [u16 state] (1 = on, 0 = off). Sprint drains the yellow ENERGY bar CLIENT-side
				// (retail's server barely streams the bar during sprint - verified: 1 packet across a sprint-heavy
				// capture). We don't reply or drive the bar; we only MIRROR the drain into the tracked session.Energy so
				// spellweaving starts from the real current value and Exhausted can fire at 0 (docs/combos.md).
				if (opcode == 0x200C)
				{
					var state = packet.Body != null && packet.Body.Length - packet.Body.Position >= sizeof(ushort)
						? packet.Body.ReadUInt16()
						: (ushort)0;
					TrackSprintEnergy(client, state == 1);
					return;
				}

				// Other GameCharAgentProject methods are not loading gates and retail's replies aren't decoded yet -
				// log and no-op rather than misparse them as a 0x2000 ping.
				Logger.LogDebug("GameCharAgentProject opcode 0x{Opcode:X4} (unhandled, no reply).", opcode);
				return;
			}

			// RequestAggroStatus(const ID32& charId, const ID32& targetId): each ID32 is [u32 type][u32 instance].
			packet.Body.ReadUInt32(); // charId.type   (0x0000C350)
			packet.Body.ReadUInt32(); // charId.instance (the polling player)
			var targetType = packet.Body.ReadUInt32(); // targetId.type (0x0000C350)
			var targetInstance = packet.Body.ReadUInt32();

			var instance = client.Session.Account.ClientInstance;

			// The reply's third field (cCategory) is the client's overhead NAME-COLOUR category for this target, 			// NOT a live-aggro toggle: 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow). Proven by live
			// gdb (#256): DispatchUpdateAggroStatus -> FUN_0115eb10 emits the disposition signal that writes the
			// nameplate colour index (color_friendly_npc/color_hostile_npc/color_neutral_npc); retail's cCategory is
			// exactly 0/1/2. We always sent 0, so every NPC rendered green - including hostile mobs. Send 1 for a
			// hostile NPC so its name renders red like retail. (The NpcStats.community we put in the spawn is correct
			// but the client ignores it for the name colour - this RPC is the sole driver.)
			uint category = 0;
			// Resolve the target in the player's OWN instance (copy) of the map (#268), not the default shared zone.
			var player = _simulation.PlayerWorldEntity(client);
			if (player != null && _simulation.World.ZoneForEntity(player).TryGet(targetInstance, out var target) && target.Hostile)
				category = 1;

			await new PacketStream()
				.WriteHeader(ExistingCharacterPostReadyReportServerIdSender, ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, instance), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteUInt32(targetType)
				.WriteUInt32(targetInstance)
				.WriteUInt32(category) // cCategory: 0 = friendly, 1 = hostile, 2 = neutral
				.SendAsync(client);

			Logger.LogDebug("RequestAggroStatus(target=0x{Target:X8}) -> cCategory={Category}.", targetInstance, category);
		}

		private async Task HandleRequestBindLocation(INetworkClient client, ConanPacket packet)
		{
			// body = [u32 ID32 type][u32 requesterId]; tolerate a SHORT/empty poll (the client sends a bare-header
			// variant) - the reply is derived from the character, not the body, and requesterId is only for the log.
			// Reading past a short body threw EndOfStreamException, which propagated out of ReceivedPacket and CLOSED
			// the connection (the client dropped at world entry).
			uint requesterId = 0;
			if (packet.Body.Length >= 8)
			{
				packet.Body.ReadUInt32(); // ID32 type (0x0000C350)
				requesterId = packet.Body.ReadUInt32();
			}

			// The bind playfield is the map of the character's persisted bind RespawnPoint (set by .bind / a
			// wayfarer); an unbound character (or one whose point vanished) falls back to its current map.
			var character = client.Session.Character;
			var bindMapId = character?.MapId ?? Map.TortageBeachId;
			if (character?.BindRespawnPointId is uint bindPointId)
				bindMapId = (await Database.GetRespawnPoint(bindPointId))?.MapId ?? bindMapId;

			await new PacketStream().WriteHeader(packet.Receiver, packet.Sender, GameCharInterfaceDispatchOpcodes.DispatchUpdateBindLocation).WriteUInt32(bindMapId).SendAsync(client);
			Logger.LogDebug("RequestBindLocation({RequesterId:X8}) -> bind playfield {MapId}.", requesterId, bindMapId);
		}
	}
}
