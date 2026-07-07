using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Opcodes;

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
                                    "Ignoring movement packet {MovingType} because no resolved character is attached to the GameServer session yet. dataobjdec=0x{DataObject:X}, dataclientinst=0x{DataClientInstance:X}.",
                                    movingType,
                                    dataobjdec,
                                    dataclientinst);
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
                    // Representative samples split into a small object/position block, a 125-byte metadata block,
                    // and a variable nested entry block that currently decodes as 2, 3, or 4 repeated entries.
                    // New random-actions evidence suggests the player bag is more likely part of the giant 0x737A6DF9
                    // initial-state dump, while 0xF096251E still looks like remote chest/container data.
                    var unk0 = packet.Body.ReadUInt32();
                    var clientInst = packet.Body.ReadUInt32();
                    var unk1 = packet.Body.ReadUInt16();
                    // Some Interact variants are short and carry NO actionType/args (observed: a 10-byte trailer ending
                    // `00 08` the client sends around loot/kill). Reading actionType unconditionally threw EndOfStreamException,
                    // which ReceivedPacket turns into a hard client.Stop() (disconnect). Bail gracefully on a short body.
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
                        await Combat.CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);
                        var drawn = _broadcaster.TogglePlayerCombatStance(client);
                        await _broadcaster.BroadcastPlayerCombatMode(client, drawn);
                        Logger.LogInformation("SwitchWeaponsButton -> combat stance {State}. {Args}",
                            drawn
                                ? "drawn"
                                : "sheathed",
                            args);
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
                                Logger.LogInformation("SummonMount: from=0x{From:X} -> {Mount} ({Action}). {Args}",
                                    from,
                                    mount.CreatureName,
                                    dismounting
                                        ? "dismount"
                                        : "mount",
                                    args);

                                var self = client.Session.Account.ClientInstance;
                                await Combat.CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode); // cast begin: bar + animation

                                if (dismounting)
                                {
                                    // Immediate: don't hold the mount state during the client's dismount channel.
                                    var dismountAck = (byte[])rawPayload.Clone();
                                    if (dismountAck.Length > 17)
                                    {
                                        dismountAck[16] = 0x00;
                                        dismountAck[17] = 0x09;
                                    }

                                    await Combat.CombatService.SendBehaviorEchoAck(client, dismountAck, 3);
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
                                Task.RunAfter(MountCatalog.SummonChannelMs,
                                    async () => // horse runs up during the channel, then mount
                                    {
                                        if (client.Session?.Account == null)
                                            return;

                                        if (mountCastPayload.Length > 17)
                                        {
                                            mountCastPayload[16] = 0x00;
                                            mountCastPayload[17] = 0x09; // form-0x0a cast-complete (closes the bar)
                                        }

                                        await Combat.CombatService.SendBehaviorEchoAck(client, mountCastPayload, 3); // cast complete: mounts
                                        await ApplySpell(client, self, self, MountCatalog.RideSpell, MountCatalog.RideBuffPayload()); // seat (#26)
                                        var mountStats = new CharacterStatBlock
                                        {
                                            [CharacterStatId.Mount] = mount.RideStat,
                                            [CharacterStatId.MountSpeed] = 100
                                        }; // the mount StatUpd (frame 520)
                                        await SendStatUpdate(client, mountStats, self);
                                        if (playerEntity != null)
                                            playerEntity.IsMounted = true;
                                    },
                                    Logger);

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
                                await Combat.CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);
                                await _itemUse.UseInventoryItem(client, (int)from, (int)target);

                                break;
                            }

                            case ActionType.CastSpell:
                            {
                                // The real spell id rides the action TRAILER (`target`, the uint32 read after the protobuf
                                // args), NOT args.SpellTarget.SpellId - that protobuf field is the constant 0xC350 type-word,
                                // so casts logged as "spell 50000" and ApplySpell carried a non-existent id (no effect shown).
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
                                await Combat.CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);

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
                                    Task.RunAfter(castMs,
                                        async () => // resolve the effect after the cast time (#236)
                                        {
                                            if (client.Session?.Account != null)
                                                await Resolve();
                                        },
                                        Logger);
                                }

                                break;

                                // Resolves the cast: play the ApplySpell (caster -> target, or self when no target), drain
                                // mana, and on an offensive cast apply damage + push the target's vitals (#69).
                                async Task Resolve()
                                {
                                    // Cast-complete (#236): the cast bar is the form-0x0a cast behavior (retail BEGIN2/completion BCC,
                                    // ice-strike idx481/534), not the form-4 begin. The echo-ack derives its form from the packet's
                                    // seq word (form = seq+1), so to emit form 0x0a we stamp seq=9 onto a clone, with phase=3
                                    // (cast-state complete). Without this the bar fills then hangs.
                                    var completePayload = (byte[])rawPayload.Clone();
                                    if (completePayload.Length > 17)
                                    {
                                        completePayload[16] = 0x00;
                                        completePayload[17] = 0x09;
                                    }

                                    await Combat.CombatService.SendBehaviorEchoAck(client, completePayload, 3);

                                    // #307: a PET-SUMMON spell spawns the pet (creature + PetIIR owner-assign + follow) instead
                                    // of applying a target effect - this is what hooks pet summoning to real spell casts, not
                                    // just the .pet GM command. Decoded from the necro-pet capture (docs/summon-pet-decode.md):
                                    // cast (CastSpell) -> after the channel -> FA015807 pet spawn + PetIIR. PetSummonCatalog maps
                                    // the summon spell id -> pet template + kind.
                                    if (PetSummonCatalog.ForSpell(spellId) is { } petSummon)
                                    {
                                        var pet = await SummonPetForPlayer(client, petSummon.PetTemplate, petSummon.Combat);
                                        Logger.LogInformation("CastSpell 0x{Spell:X} pet-summon '{Template}' -> {Result}.",
                                            spellId,
                                            petSummon.PetTemplate,
                                            pet != null
                                                ? $"0x{pet.Handle:X8}"
                                                : "no spawnable template (pet-creature import needed)");
                                        return;
                                    }

                                    await ApplySpell(client,
                                        casterHandle,
                                        targetHandle == 0
                                            ? casterHandle
                                            : targetHandle,
                                        spellId);
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
                                    Logger.LogInformation("TalkToNpc: no conversation node for npcHandle=0x{Npc:X} (name={Name}, typeId={TypeId}, mode={Mode}, field3={Field3}); closing the window.",
                                        npcHandle,
                                        routedName,
                                        args.TypeId,
                                        args.Mode,
                                        args.Field3);
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
                                        await Combat.CombatService.SendEquipMoveAck(client, rawPayload, (uint)args.Mode, movedItem);
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
                                        await Combat.CombatService.SendEquipMoveAck(client, rawPayload, (uint)args.Mode, null);
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
                                await Combat.CombatService.SendBehaviorEchoAck(client, rawPayload, (uint)args.Mode);

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

        // Answers a TradeskillRequestIIR (recipe-book open) with the TradeskillReplyIIR (0x9062D559) recipe list built
        // from the Recipe catalog, so the Crafting -> Recipes window populates. The carrier is the standard world IIR
        // framing (GameAgent 0x58 -> GameInterface <instance>), body = [subop][0xC350][handle][reply], byte-exact vs
        // the capture (docs/tradeskill-recipes.md). Empty catalog -> an empty (count=0x3F1) list, which opens the book
        // cleanly with no recipes.
        private async Task SendCraftingRecipeBook(INetworkClient client)
        {
            var handle = client.Session.Account.ClientInstance;
            var rows = await Database.GetRecipes();
            var records = rows
                .Select(r => new TradeskillRecipeCodec.RecipeRecord(r.Id, r.Data))
                .ToList();
            var replyBody = TradeskillRecipeCodec.BuildReplyBody(records);

            await new PacketStream()
                .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
                .WriteArrayUInt32Length(s => s
                    .WriteUInt32(GameServer0X2000Opcodes.TradeskillReplyIIR)
                    .WriteUInt32(0x0000C350) // the ProtoID type marker (0xC350) preceding the target handle
                    .WriteUInt32(handle)
                    .WriteArray(replyBody))
                .SendAsync(client);

            Logger.LogInformation("Sent crafting recipe book: {Count} recipe(s) to client {Handle} (TradeskillReplyIIR).", records.Count, handle);
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

        // QueuePetCommand (0x205B, GameCharAgent): the pet command bar. Body = [u32 type=0xC350][u32 petHandle]
        // [u32 command]. Decoded commands (#307, docs/summon-pet-decode.md): 0x1F8 = dismiss, 0x1F6 = defend.
        // Dismiss = retail's PetIIR mode-1 remove + the n3ToClientQuit despawn. Other commands are logged for now
        // (attack/follow/stay AI is a follow-up; a .pet-summoned minion already follows via SetNpcFollow).
        private async Task HandlePetCommand(INetworkClient client, ConanPacket packet)
        {
            packet.Body.ReadUInt32(); // type 0xC350
            var petHandle = packet.Body.ReadUInt32();
            var command = packet.Body.ReadUInt32<PetCommand>();
            var owner = client.Session.Account.ClientInstance;
            Logger.LogInformation("QueuePetCommand from {Owner:X8}: pet 0x{Pet:X8}, command {Command}.", owner, petHandle, command);

            if (command == PetCommand.Dismiss)
            {
                await new PacketStream()
                    .WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
                    .WriteArrayUInt32Length(s => s.WriteArray(PetIirCodec.BuildRemove(owner, petHandle)))
                    .SendAsync(client);

                var removed = await _broadcaster.DespawnEntityForPlayer(client, petHandle);
                Logger.LogInformation(removed != null
                        ? "Pet 0x{Pet:X8} dismissed (despawned)."
                        : "Pet dismiss: no entity 0x{Pet:X8} in the player's zone.",
                    petHandle);
            }
        }
    }
}
