using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Packets.Protobuf;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
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
				case GameServer0X2000Opcodes.StatIIR:
				{
					if (client.Session.Character?.Name == null)
					{
						if (IsRetailCreateEditorFlow(client))
						{
							Logger.LogInformation("Observed retail create-editor 0xF98E10B3 while pendingPost205F={PendingPost205F}, pendingReady={PendingReady}.", client.Session.HasPendingPost205FFollowUp,
								client.Session.HasPendingCreateEditorReadyFollowUp);
							break;
						}

						if (client.Session.HasPendingPost205FFollowUp)
						{
							Logger.LogInformation("Queued new-character 0xF98E10B3 while post-0x205F handoff is still pending.");
						}
						else
							Logger.LogInformation("Ignoring standalone new-character 0xF98E10B3 because the post-0x205F handoff has already been flushed.");
					}

					break;
				}

				case GameServer0X2000Opcodes.LegacyCreateEchoInstanceStrie:
				case GameServer0X2000Opcodes.LegacyCreateEchoInstanceRetail:
				{
					// Legacy fixed-instance echoes from older captures (retail 0x021662C8 / Strie 0x02165EFF).
					// The normal path is the dynamic-instance pre-switch check above; this stays as a fallback.
					await HandleCreateEditorInstanceEcho(client, packet);
					break;
				}

				case GameServer0X2000Opcodes.ScryScriptIIR:
				{
					await HandleCommandPacket(client, packet);
					break;
				}

				case GameServer0X2000Opcodes.Move:
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
							var sessionCharacter = client.Session.Character ?? client.Session.Account.Character;
							if (sessionCharacter == null)
							{
								Logger.LogWarning(
									"Ignoring movement packet {MovingType} because no resolved character is attached to the GameServer session yet. dataobjdec=0x{DataObject:X}, dataclientinst=0x{DataClientInstance:X}.",
									movingType, dataobjdec, dataclientinst);
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
								moveEntity.Rotation = moveRotation;
							}
							else
							{
								// The DB Character stores a quaternion; the move wire carries a Vector3 (Euler) rotation.
								sessionCharacter.Position = movePosition;
								sessionCharacter.Rotation = moveRotation.ToQuaternion();
							}

							var livePosition = moveEntity?.Position ?? sessionCharacter.Position;
							var liveRotation = moveEntity?.Rotation ?? sessionCharacter.Rotation.ToVector3();

							// send to all (nearby) clients? (single client for now -> the session conversation;
							// retail's move broadcast uses sender 0d13ce71b110 58 + receiver = the moving entity's
							// instance, confirmed across Strie/dikketest/demol80 captures). Interest-managed broadcast to
							// nearby players is the next step.
							await new PacketStream()
								.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerCarrierOpcodes.IirCarrier)
								.WriteArrayUInt32Length(s => s
									.WriteUInt32(pbOpcode)
									.WriteUInt32(dataobjdec)
									.WriteUInt32(dataclientinst)
									.WriteUInt16(movingType)
									.WriteVector3BigEndian(livePosition)
									.WriteVector3BigEndian(liveRotation)
									.WriteArray(datadat))
								.SendAsync(client);

							// Interest-managed broadcast: spawn this player to newly-near players and forward the move
							// to players who already see them (docs/archive/server-architecture-roadmap.md). No-op solo.
							await _broadcaster.BroadcastPlayerMove(client, (uint)pbOpcode, dataobjdec, dataclientinst, (ushort)movingType, livePosition, liveRotation, datadat);

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

				case GameServer0X2000Opcodes.ReadyForPlayScreen: //peer0_160
				{
					// 0x2024 also = GameCharAgent.RespawnMe: when the player is DEAD this is their pick on the release
					// screen (DispatchValidResurrectionSpots), body = [u32 spawnPointId]. Route to respawn, not the
					// login ReadyForPlayScreen path (they share the opcode).
					if (_simulation.IsAwaitingRespawn(client))
					{
						var spawnPointId = packet.Body != null && packet.Body.Length - packet.Body.Position >= sizeof(uint)
							? packet.Body.ReadUInt32()
							: 0;
						Logger.LogInformation("RespawnMe (0x2024): {Char} chose resurrection spot {SpawnPointId}.", client.Session.Character?.Name, spawnPointId);
						await _simulation.RespawnAtChosenPoint(client, spawnPointId);
						break;
					}

					if (client.Session.Character?.Name == null)
					{
						if (IsRetailCreateEditorFlow(client))
						{
							if (client.Session.HasFlushedCreateEditorPostReady)
							{
								Logger.LogInformation("Client sent ReadyForPlayScreen after the deadlock-breaker already flushed the post-ready burst; not re-flushing.");
							}
							else
							{
								client.Session.CreateEditorTempInstanceCount = 0;
								client.Session.CreateEditorReadyRequestIds.Clear();
								client.Session.HasPendingCreateEditorReadyFollowUp = true;
								Logger.LogInformation("Queued retail create-editor ReadyForPlayScreen follow-up; waiting for the post-ready 0x021662C8 burst before replying.");
							}
						}
						else
						{
							await SendReadyForPlayScreen(client);
							await HandleNewCharacter(client, packet);
						}
					}
					else
					{
						await SendReadyForPlayScreen(client);
						await SendExistingCharacterPostReadyFollowUp(client);
					}

					break;
				}

				case GameServer0X2000Opcodes.KnubotAnswer:
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

				case GameServer0X2000Opcodes.KnubotCloseChatWindow:
					// The player closed the NPC chat window (ESC/X). The client signals this with its OWN
					// KnubotCloseChatWindow (the client double-sends it); retail just accepts it. We must tear down the
					// conversation session state so the dialogue stays closed - leaving it set let the next interaction
					// re-enter a stale conversation, and the unknown-opcode default path also spammed a system message
					// on every close (#217). No reply: the window is already closed on the client.
					ConversationService.EndConversation(client);
					break;

				case GameServer0X2000Opcodes.Interact:
				{
					// Important capture observation:
					// world-object interactions appear to be the likely trigger path for the container-style
					// response family 0xF096251E, which carries names like "Chest [N7FU]" and "object_closed".
					// Representative samples split into a small object/position block, a 125-byte metadata block,
					// and a variable nested entry block that currently decodes as 2, 3, or 4 repeated entries.
					// New random-actions evidence suggests the player bag is more likely part of the giant 0x737A6DF9
					// initial-state dump, while 0xF096251E still looks like remote chest/container data.
					var unk0 = packet.Body.ReadUInt32();
					var clientInst = packet.Body.ReadUInt32();
					var unk1 = packet.Body.ReadUInt16();
					var actionType = packet.Body.ReadUInt32<ActionType>();
					var args = packet.Body.ReadProtobufUInt32Length<InteractArgs>();

					if (actionType == ActionType.SwitchWeaponsButton)
					{
						// Has no extra data

						Logger.LogInformation($"SwitchWeaponsButton: {args}");
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
								// The client-initiated mount summon (#27): `from` is the mount/feat id on the bar
								// (0x3E34F1 = "Fading Black Horse", 0x3E34F2 = "Swift Fading Black Horse"; unk2's
								// 4th byte is the mount variant index, target = 0). The server answers exactly like
								// the `.mount` GM command: cast the summon spell (ApplySpell 0x6B87E316) so the
								// channelled summon plays, then spawn the mount creature at the player so it renders
								// (the FA015807 mount NPC, harvested by CaptureMountImporter). This is the proper
								// client-cast mount flow - and the clean mid-session client-CAST-spawn render test.
								var mount = World.MountCatalog.ForId(from) ?? World.MountCatalog.Default;
								Logger.LogInformation("SummonMount: from=0x{From:X} -> {Mount} (spell 0x{Spell:X}). {Args}", from, mount.CreatureName, mount.SummonSpellId, args);

								var self = client.Session.Account.ClientInstance;
								await ApplySpell(client, self, self, mount.SummonSpellId);
								await _broadcaster.SpawnMountForPlayer(client, mount.CreatureName);

								break;
							}

							case ActionType.RightClickItem:
							{
								Logger.LogInformation("RightClickItem: container={Container} slot={Slot} {Args}", from, target, args);

								// Every behavior gets an echo-ack (docs/combat.md rule 1); the item-use domain logic
								// lives in ItemUseService. The used item is identified by its inventory location - from =
								// container (0x68 = backpack), target = slot - decoded from the use_totem_of_origin
								// capture (using a Totem of Origin recalls to the bind point, #206; the recall's handoff
								// is the capture's "long loading screen").
								await _combat.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);
								await _itemUse.UseInventoryItem(client, (int)from, (int)target);

								break;
							}

							case ActionType.CastSpell:
							{
								var spellId = (uint)(args.SpellTarget?.SpellId ?? args.TypeId);
								var casterHandle = client.Session.Account.ClientInstance;
								var targetHandle = (uint)(args.SpellTarget?.TargetHandle ?? 0);

								Logger.LogInformation($"CastSpell spell={spellId} target=0x{targetHandle:X} {args}");

								// Play the cast (caster -> target, or self when no target), then resolve its effect.
								await ApplySpell(client, casterHandle, targetHandle == 0
									? casterHandle
									: targetHandle, spellId);

								// Casting costs mana - drain the caster's mana bar (the spell analogue of the melee
								// stamina cost). Applies to offensive and self/buff casts alike.
								await _combat.SpendCasterMana(client, spellId);

								// An offensive cast at another entity applies damage + pushes the target's vitals; a
								// self/no-target cast is a buff (no damage), already registered by ApplySpell (#69).
								if (targetHandle != 0 && targetHandle != casterHandle)
									await _combat.ResolveOffensiveCast(client, casterHandle, targetHandle, spellId);

								break;
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
								// Using a world object - the loot path: `target` is the object handle (its typetag,
								// 0xC87A for loot bags, rides in `from`). Opens the bag if it's a loot bag in the zone.
								await _broadcaster.OpenLootBag(client, target);

								break;
							}

							case ActionType.TalkToNpc:
							{
								// The clicked NPC's entity handle rides in the interact args (field 10 / field 2);
								// (TypeId, Mode, Field3) select the conversation node the client is requesting.
								var npcHandle = (uint)(args.SpellTarget?.TargetHandle ?? 0);
								Logger.LogInformation("TalkToNpc: from=0x{From:X}, target=0x{Target:X}, npcHandle=0x{NpcHandle:X}, args={Args}", from, target, npcHandle, args);

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
									await SendNpcIntroConversation(client, npcHandle);
								// Beach login with no captured node for this (Mode, Field3): close the chat window so the
								// client doesn't hang waiting for a reply (the "advance a step -> chat locks, can't ESC"
								// bug). Conversation replay is coverage-bound, so an un-captured branch is expected.
								else if (client.Session.WorldEntered)
								{
									var routedName = client.Session.SpawnedNpcNamesByHandle.TryGetValue(npcHandle, out var rn) ? rn : "<unmapped handle>";
									Logger.LogInformation("TalkToNpc: no conversation node for npcHandle=0x{Npc:X} (name={Name}, typeId={TypeId}, mode={Mode}, field3={Field3}); closing the window.", npcHandle, routedName, args.TypeId, args.Mode, args.Field3);
									await _conversation.SendConversationClose(client, npcHandle); // #217: close the CLICKED NPC's window (re-address the camera release)
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
										var fromSlot = from;
										var targetSlot = (CharacterEquipmentSlot)target;

										Logger.LogInformation($"{direction}: ({fromSlot} -> {targetSlot}) {args}");

										// The trailing struct (after from/target) carries the item's template handle as a
										// big-endian uint32; persist the equip into CharacterEquipment.
										var equipHandle = unk2.Length >= 4
											? BinaryPrimitives.ReadUInt32BigEndian(unk2.AsSpan(0, 4))
											: 0;
										var movedItem = equipHandle != 0
											? await Database.GetItem(equipHandle)
											: null;
										if (equipHandle != 0)
											await _equipment.PersistEquip(client, targetSlot, equipHandle, (int)fromSlot);

										// Retail's reply: the behavior ack carrying the item's identity quad + a 0x201C
										// entry (prefix 0x65, state = the destination slot). Capture frames 289-290.
										await _combat.SendEquipMoveAck(client, rawPayload, (uint)args.Mode, movedItem);
										await Send0X201CEntry(client, LoginWorld201CSender, LoginWorld201CReceiver(client), 0x00000065, target, 0x00160000u + ((uint)Environment.TickCount & 0xFFFF));

										break;
									}

									// Out-of-slot moves: tail = [srcEquipSlot][destBagCell][0...].
									case MoveItemInInventoryDirection.EquippedToInventory:
									case MoveItemInInventoryDirection.EquippedToBag:
									{
										var fromSlot = (CharacterEquipmentSlot)from;
										var targetSlot = target;

										Logger.LogInformation($"{direction}: ({fromSlot} -> {targetSlot}) {args}");

										// Unequip is identified by the source equip slot (the trailing handle is 0);
										// `target` is the bag cell the item lands in.
										await _equipment.PersistUnequip(client, fromSlot, (int)targetSlot);

										// Retail's reply: the behavior ack with null identity + a 0x201C entry
										// (prefix 0x68, state 0). Capture frames 320-321.
										await _combat.SendEquipMoveAck(client, rawPayload, (uint)args.Mode, null);
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
								await _combat.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);

								break;
							}
						}
					}

					break;
				}

				case GameServer0X2000Opcodes.CloseGame: // Also received when Activating feat point ??
				{
					var unk0 = packet.Body.ReadUInt32();
					var clientInst = packet.Body.ReadUInt32();

					Logger.LogInformation("GameServer close request for character {CharacterId}", clientInst.ToHex());

					// is handled in ClientDisconnected
					//account.Character.SaveDataToDatabase(Database);
					break;
				}

				case GameServer0X2000Opcodes.SelectDeselect:
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

				case GameServer0X2000Opcodes.DropItem:
				{
					var unk0 = packet.Body.ReadUInt32(); // c350
					var clientInst = packet.Body.ReadUInt32();
					var reserved = packet.Body.ReadUInt32();
					var move = packet.Body.ReadProtobufByteLength<MoveItemInInventoryArgs>();

					Logger.LogInformation($"DropItem: {move}");

					// Remove the dropped item from the persisted bag so it doesn't reappear on relog.
					await _equipment.PersistDropItem(client, move);

					// Push the LIVE remove so the open bag updates without a relog (#230): the DropItem opcode has no
					// implicit client-side removal, so without this the dropped item stays visible until next login.
					// Same DispatchRemoveItem (0x201F) the .removeitem GM path / RemoveItemFromBackpack use.
					if (move?.SourceContainerId is { Type: InventoryContainerBackpack })
						await SendRemoveItem(client, InventoryContainerBackpack, move.SourceItemPos);

					break;
				}

				case GameServer0X2000Opcodes.MoveItemInInventory:
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
					var move = packet.Body.ReadProtobufByteLength<MoveItemInInventoryArgs>();

					Logger.LogInformation($"MoveItemInInventory: {move}");

					// Persist the new slot positions so the player's bag arrangement survives logout (the world-entry
					// snapshot rebuilds the bag from these CharacterInventory rows).
					await _equipment.PersistInventoryMove(client, move);

					// A move that touches the personal bank (kind 105) gets no client-side render otherwise (the bank
					// tab isn't refreshed), so a deposit looks like the item vanished - push the live bank refresh.
					await _bank.UpdateBankAfterMove(client, move);

					break;
				}

				case GameServer0X2000Opcodes.DeleteQuest:
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
						0x00, 0x00, 0xC3, 0x50,
						0x11, 0x01, 0xB4, 0xF5, // clientInst
						0x00,
						0x00, 0x00, 0x00, 0x01,
						0x00, 0x00, 0x00, 0x04,
						0x00, 0x00, 0x4B, 0xA4, // questId?
						0x00, 0x00, 0x00, 0x00

					    // Summoned
					    0x00, 0x00, 0xC3, 0x50,
					    0x11, 0x01, 0xB4, 0xF5, // clientInst
					    0x00,
					    0x00, 0x00, 0x00, 0x01,
					    0x00, 0x00, 0x00, 0x04,
					    0x00, 0x00, 0x46, 0x20, // questId?
					    0x00, 0x00, 0x00, 0x00

					    // Storming the gate
					    0x00, 0x00, 0xC3, 0x50,
					    0x11, 0x01, 0xB4, 0xF5, // clientInst
					    0x00,
					    0x00, 0x00, 0x00, 0x01,
					    0x00, 0x00, 0x00, 0x04,
					    0x00, 0x00, 0x36, 0x47, // questId?
					    0x00, 0x00, 0x00, 0x00
					 */

					break;
				}

				case GameServer0X2000Opcodes.InventoryClaimsButton:
				{
					var data = packet.Body.ReadRemainingArray();

					break;
				}

				case GameServer0X2000Opcodes.LegacyConanHimselfInstance:
				{
					var data = packet.Body.ReadRemainingArray();

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
					if ((uint)pbOpcode == (uint)GameServer0X2000RespondsOpcodes.N3TeleportIIR
					    || (uint)pbOpcode == (uint)GameServer0X2000RespondsOpcodes.IntendedTeleportIIR)
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
		// subopcode while the editor is loading. We count these to time the post-0x205F and post-ready bursts,
		// exactly as retail does, and ack standalone ones. (Retail used a fixed instance; we hand out the
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

			client.Session.CreateEditorTempInstanceCount++;

			if (client.Session.HasPendingPost205FFollowUp)
			{
				Logger.LogInformation("Queued create-editor instance-echo #{Count} while post-0x205F follow-up is pending.", client.Session.CreateEditorTempInstanceCount);

				if (client.Session.CreateEditorTempInstanceCount >= 3)
				{
					Logger.LogInformation("Flushing create-editor post-0x205F burst after {Count}x instance echo.", client.Session.CreateEditorTempInstanceCount);
					await SendRetailCreateEditorPost205FBurst(client);
					client.Session.HasPendingPost205FFollowUp = false;
					client.Session.HasFlushedCreateWorldEntryBurst = true;
					client.Session.CreateEditorTempInstanceCount = 0;
				}
			}
			else if (client.Session.HasPendingCreateEditorReadyFollowUp)
			{
				if (requestId.HasValue && !client.Session.CreateEditorReadyRequestIds.Contains(requestId.Value))
					client.Session.CreateEditorReadyRequestIds.Add(requestId.Value);

				Logger.LogInformation("Queued create-editor post-ready instance-echo #{Count}; uniqueRequestIds={UniqueCount}.", client.Session.CreateEditorTempInstanceCount, client.Session.CreateEditorReadyRequestIds.Count);

				// Retail waits for at least one post-ready echo before sending the 0x2024 / 0x864CFEF8 /
				// 0xF508F4C1 follow-up; the live client can stall before the full 5-id burst, so flush on the
				// first decoded post-ready request and let later standalone ones flow through the ack path.
				if (requestId.HasValue)
				{
					Logger.LogInformation("Flushing create-editor post-ready burst using observed request ids [{RequestIds}].", string.Join(", ", client.Session.CreateEditorReadyRequestIds.ConvertAll(id => id.ToString("X"))));
					await SendRetailCreateEditorPostReadyBurst(client, client.Session.CreateEditorReadyRequestIds);
					client.Session.HasPendingCreateEditorReadyFollowUp = false;
					client.Session.CreateEditorTempInstanceCount = 0;
					client.Session.CreateEditorReadyRequestIds.Clear();
				}
			}
			else if (requestId.HasValue)
			{
				// Break the create-editor DEADLOCK. Retail's client sends its own ReadyForPlayScreen once the editor
				// scene finishes loading, which arms HasPendingCreateEditorReadyFollowUp and flushes the post-ready
				// burst (the 0x2024 + the 0xA36D3B74 "NEW_character_creation" script that actually LAUNCHES the editor).
				// Our live client never reaches that point - it just re-echoes its preview instances (e.g. 9FF92-96)
				// forever while we only ack them, so the editor never appears (black/loading screen). So once the
				// post-0x205F burst has flushed and the client has settled into re-echoing a few distinct preview
				// instances, flush the post-ready burst OURSELVES (once) to launch the editor.
				if (client.Session.HasFlushedCreateWorldEntryBurst && !client.Session.HasFlushedCreateEditorPostReady)
				{
					if (!client.Session.CreateEditorReadyRequestIds.Contains(requestId.Value))
						client.Session.CreateEditorReadyRequestIds.Add(requestId.Value);

					if (client.Session.CreateEditorReadyRequestIds.Count >= 3)
					{
						Logger.LogInformation("Create-editor deadlock breaker: client never sent ReadyForPlayScreen; proactively flushing the post-ready burst using observed request ids [{RequestIds}].",
							string.Join(", ", client.Session.CreateEditorReadyRequestIds.ConvertAll(id => id.ToString("X"))));
						await SendRetailCreateEditorPostReadyBurst(client, client.Session.CreateEditorReadyRequestIds);
						client.Session.HasFlushedCreateEditorPostReady = true;
						client.Session.CreateEditorReadyRequestIds.Clear();
						return;
					}
				}

				Logger.LogInformation("Acknowledging standalone create-editor instance-echo with requestId={RequestId:X}.", requestId.Value);
				await SendRetailCreateEditorPostReadyAck(client, requestId.Value);
			}
			else
				Logger.LogInformation("Ignoring standalone create-editor instance-echo after the expected bursts already flushed.");
		}
	}
}
