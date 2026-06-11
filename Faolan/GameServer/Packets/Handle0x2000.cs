using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
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
							client.Session.Post205FF98E10B3Count++;
							Logger.LogInformation("Queued new-character 0xF98E10B3 #{Count} while post-0x205F handoff is still pending.", client.Session.Post205FF98E10B3Count);
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
					//Logger.LogInformation($"[DUMP] Move ({rawPayload.Length} bytes): {rawPayload.ToHex()}");
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

							sessionCharacter.Position = packet.Body.ReadVector3();
							sessionCharacter.Rotation = packet.Body.ReadVector3(); // not sure if this is correct
							var datadat = packet.Body.ReadRemainingArray();

							// send to all (nearby) clients? (single client for now -> the session conversation;
							// retail's move broadcast uses sender 0d13ce71b110 58 + receiver = the moving entity's
							// instance, confirmed across Strie/dikketest/demol80 captures)
							await new PacketStream()
								.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
								.WriteArrayUInt32Length(s => s
									.WriteUInt32(pbOpcode)
									.WriteUInt32(dataobjdec)
									.WriteUInt32(dataclientinst)
									.WriteUInt16(movingType)
									.WriteVector3BigEndian(sessionCharacter.Position)
									.WriteVector3BigEndian(sessionCharacter.Rotation)
									.WriteArray(datadat))
								.SendAsync(client);

							break;
						}

						default:
						{
							await LogToClient($"Received unknown moving type: {movingType.ToHex()}");
							break;
						}
					}

					break;
				}

				case GameServer0X2000Opcodes.ReadyForPlayScreen: //peer0_160
				{
					//Logger.LogInformation($"[DUMP] ReadyForPlayScreen ({rawPayload.Length} bytes): {rawPayload.ToHex()}");
					//Logger.LogInformation($"Completing world entry for {client.Session.Account?.UserName ?? client.IpAddress}.");

					if (client.Session.Character?.Name == null)
					{
						if (IsRetailCreateEditorFlow(client))
						{
							client.Session.CreateEditorTempInstanceCount = 0;
							client.Session.CreateEditorReadyRequestIds.Clear();
							client.Session.HasPendingCreateEditorReadyFollowUp = true;
							Logger.LogInformation("Queued retail create-editor ReadyForPlayScreen follow-up; waiting for the post-ready 0x021662C8 burst before replying.");
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

				case GameServer0X2000Opcodes.Interact:
				{
					//Logger.LogInformation($"[DUMP] Interact ({rawPayload.Length} bytes): {rawPayload.ToHex()}");
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

						await LogToClient($"SwitchWeaponsButton: {args}");
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
								// Fading black horse
								// from = 0x3E34F1
								// unk2 = 0x00, 0x00, 0x02, 0x26, 0x01

								// Swift fading black horse
								// from = 0x3E34F2
								// unk2 = 0x00, 0x00, 0x02, 0x27, 0x01

								// for both:
								// target = 0

								await LogToClient($"SummonMount: {args}");

								break;
							}

							case ActionType.RightClickItem:
							{
								await LogToClient($"RightClickItem: {args}");

								break;
							}

							case ActionType.CastSpell:
							{
								var spellId = (uint)(args.SpellTarget?.SpellId ?? args.TypeId);
								var casterHandle = client.Session.Account.ClientInstance;
								var targetHandle = (uint)(args.SpellTarget?.TargetHandle ?? 0);

								await LogToClient($"CastSpell spell={spellId} target=0x{targetHandle:X} {args}");

								// Play the cast (caster -> target, or self when no target), then resolve its effect.
								await ApplySpell(client, casterHandle, targetHandle == 0
									? casterHandle
									: targetHandle, spellId);

								// Casting costs mana - drain the caster's mana bar (the spell analogue of the melee
								// stamina cost). Applies to offensive and self/buff casts alike.
								await SpendCasterMana(client, spellId);

								// An offensive cast at another entity applies damage + pushes the target's vitals; a
								// self/no-target cast is treated as a buff (no damage) until spell data distinguishes them.
								if (targetHandle != 0 && targetHandle != casterHandle)
									await ResolveOffensiveCast(client, casterHandle, targetHandle, spellId);

								break;
							}

							case ActionType.NormalAttack:
							{
								// The melee swing / auto-attack: ack the behavior + resolve the hit (see
								// SendNormalAttackResponse). The target is in the request args (SpellTarget.TargetHandle).
								await SendNormalAttackResponse(client, rawPayload, (uint)(args.SpellTarget?.TargetHandle ?? 0));

								break;
							}

							case ActionType.Emote:
							{
								await LogToClient($"Emote: {args}");

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
								if (await TryReplayConversation(client, (uint)args.TypeId, (uint)args.Mode, (uint)args.Field3, npcHandle))
									break;

								// Last-resort fallback ONLY when the DB has no node for this NPC: the single hardcoded
								// post-creation intro (conversation 12493). It is NPC-agnostic, so it shows the same text
								// for every NPC - the DB path above is what routes the correct dialogue per NPC.
								if (client.Session.IsCreateWorldSession && npcHandle != 0)
									await SendNpcIntroConversation(client, npcHandle);
								// Beach login with no captured node for this (Mode, Field3): close the chat window so the
								// client doesn't hang waiting for a reply (the "advance a step -> chat locks, can't ESC"
								// bug). Conversation replay is coverage-bound, so an un-captured branch is expected.
								else if (client.Session.UsedWorldBurstReplay)
									await SendConversationClose(client);

								break;
							}

							case ActionType.MoveEquippedToFromInventory:
							{
								// bag slot starts top left, continue counting on next page

								var direction = (MoveItemInInventoryDirection)args.TypeId;
								switch (direction)
								{
									case MoveItemInInventoryDirection.InventoryToEquipped:
									{
										var fromSlot = from;
										var targetSlot = (CharacterEquipmentSlot)target;

										await LogToClient($"InventoryToEquipped: ({fromSlot} -> {targetSlot}) {args}");

										// The trailing struct (after from/target) carries the item's template handle as a
										// big-endian uint32; persist the equip into CharacterEquipment.
										var equipHandle = unk2.Length >= 4
											? BinaryPrimitives.ReadUInt32BigEndian(unk2.AsSpan(0, 4))
											: 0;
										if (equipHandle != 0)
											await PersistEquip(client, targetSlot, equipHandle, (int)fromSlot);

										break;
									}

									case MoveItemInInventoryDirection.EquippedToInventory:
									{
										var fromSlot = (CharacterEquipmentSlot)from;
										var targetSlot = target;

										await LogToClient($"EquippedToInventory: ({fromSlot} -> {targetSlot}) {args}");

										// Unequip is identified by the source equip slot (the trailing handle is 0);
										// `target` is the bag cell the item lands in.
										await PersistUnequip(client, fromSlot, (int)targetSlot);

										break;
									}

									default:
									{
										await LogToClient($"MoveEquippedToFromInventory: unknown direction={direction} {args}");

										break;
									}
								}

								break;
							}

							default:
							{
								await LogToClient($"UNKNOWN-{actionType.ToHex()}: {args}");
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
					var data = packet.Body.ReadRemainingArray();

					await LogToClient($"SelectDeselect: {data.ToHex()}");

					break;
				}

				case GameServer0X2000Opcodes.DropItem:
				{
					var unk0 = packet.Body.ReadUInt32(); // c350
					var clientInst = packet.Body.ReadUInt32();
					var reserved = packet.Body.ReadUInt32();
					var move = packet.Body.ReadProtobufByteLength<MoveItemInInventoryArgs>();

					await LogToClient($"DropItem: {move}");

					break;
				}

				case GameServer0X2000Opcodes.MoveItemInInventory:
				{
					//Logger.LogInformation($"[DUMP] MoveItemInInventory ({rawPayload.Length} bytes): {rawPayload.ToHex()}");
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

					await LogToClient($"MoveItemInInventory: {move}");

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
					//Logger.LogInformation($"[DUMP] InventoryClaimsButton ({rawPayload.Length} bytes): {rawPayload.ToHex()}");

					break;
				}

				case GameServer0X2000Opcodes.LegacyConanHimselfInstance:
				{
					var data = packet.Body.ReadRemainingArray();
					//Logger.LogInformation($"[DUMP] Ox1101B4F6 ({rawPayload.Length} bytes): {rawPayload.ToHex()}\r\nremaining: {data.ToHex()}");

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

					Logger.LogWarning("Unhandled GameServer 0x2000 subopcode {SubOpcodeHex} ({RawLength} bytes).", pbOpcode.ToHex(), rawPayload.Length);
					if (Logger.IsEnabled(LogLevel.Debug))
						Logger.LogDebug("Unhandled GameServer 0x2000 subopcode {SubOpcodeHex} raw payload: {RawPayload}", pbOpcode.ToHex(), rawPayload.ToHex());
					await _agentServerDispatcher.SendSystemMessage(client.Session.Account.ClientInstance, $"Unknown 0x2000 Opcode: {pbOpcode.ToHex()} ({rawPayload.Length} bytes)");

					break;
				}
			}

			return;

			async Task LogToClient(string message)
			{
				Logger.LogInformation(message);
				await _agentServerDispatcher.SendSystemMessage(client.Session.Account.ClientInstance, message);
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
				Logger.LogInformation("Acknowledging standalone create-editor instance-echo with requestId={RequestId:X}.", requestId.Value);
				await SendRetailCreateEditorPostReadyAck(client, requestId.Value);
			}
			else
				Logger.LogInformation("Ignoring standalone create-editor instance-echo after the expected bursts already flushed.");
		}
	}
}
