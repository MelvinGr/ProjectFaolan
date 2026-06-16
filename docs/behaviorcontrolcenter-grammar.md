# BehaviorControlCenterIIR_c (0x642CD3D6) wire grammar — "BccIIR"

Recovered from `AgeOfConan.exe` (Ghidra). The Behavior Control Center IIR is the NPC/player **behavior
command** carrier (the combat/interact "BccAck"). Gitea #190.

## Reader — `BehaviorControlCenterIIR_c::ReadSubClass` (`FUN_005e24e0`)

```
[u8 behaviorType]        (+0x1c)   # selects the command form
switch (behaviorType):
  2,3,4,5,7,10,0xF,0x10,0x12 : [u32 commandId] -> look up the Command (FUN_007dcd60), then the Command's own
                               reader (vtable slot 0x80) reads its behavior-specific data
  6,8,9,0xD,0x11             : (terminal - no extra read)
  0xB,0xC                    : [u32] (+0x28)
  0xE                        : [u32 a (+0x24)] ; if a != 0 -> [u32 commandId] -> Command reader
  default                    : (nothing)
```

So a BccIIR = `[u8 type][u32 commandId?][command-specific data]`, where the command data is read by the
looked-up Command object's reader (another vtable hop). The combat-relevant forms (the attack/interact ack)
are already built byte-exact by `CombatService`/`CombatPacketCodec` (`BehaviorAckTests`).

## Status

Grammar SHAPE decoded (type switch + commandId + per-command reader). A fully constructive codec needs the
per-Command readers (vtable slot 0x80 of each Command type) enumerated - the residue; the combat path is done.
