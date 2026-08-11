// Settles what the client DOES with the NPC stat-core's `spawn_animation` (NpcStats protobuf field 8, Gitea #800):
// it is a one-shot "play your entrance animation NOW" trigger, and the client falls back to the creature's IDLE
// animation when it declares no entrance one.
//
// This is the move #800 named as the cheapest remaining one — "nobody has looked for what the client does with
// field 8" — and it overturns #794's refutation. #794 argued the flag cannot mean "play this creature's entrance
// animation" because 275 of the flagged templates wear a creature that declares no `spawnanim`. PART C below is
// why that does not follow: the client picks `spawnanim` only when the string is non-empty and takes
// `defaultidleanim` otherwise, in an explicit branch. A flagged Citizen is not a contradiction — it plays its idle.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpSpawnAnimationConsumer.java
//
// Output: $FAOLAN_GHIDRA_OUT/spawn_animation_consumer.txt, in four parts that together are the proof:
//
//   PART A — the PRODUCER. `FUN_01458d40` (the SimpleChar spawn apply, the same window DumpNpcTypeRank.java reads)
//            copies the `npc_stats` submessage field by field into stats. NpcStats field N sits at +0x04 + 4N —
//            pinned by the five identities #797 established (field 5 -> +0x18, 10 -> +0x2c, 11 -> +0x30,
//            12 -> +0x34, 14 -> +0x3c) — so field 8 is +0x24, and the window shows the three consecutive copies:
//
//              npc_stats+0x1c -> SetStat(0x1C7)  community      = NpcStats field 6
//              npc_stats+0x20 -> SetStat(0x1D2)  los_height     = field 7
//              npc_stats+0x24 -> SetStat(0x19C)  SpawnAnimation = field 8   <- the one in question
//
//            0x19C is `SpawnAnimation` in the client's own stat-name table (docs/re/aoc-client-stat-names.tsv),
//            so the wire field and the client stat share a name as well as a slot.
//
//   PART B — the CONSUMER, which is the result. `FUN_01104750` is `SimpleChar_t` virtual slot 36 (vtable
//            0x0203E93C, entry 0x0203E9CC) — the monsterdata/model build. It ends with, in order:
//
//              if (GetStat(0x19C) != 0) {
//                  name = monsterData->spawnanim.empty() ? monsterData->defaultidleanim   // +0x13C
//                                                        : monsterData->spawnanim;        // +0x158
//                  if (ResolveAnim(name, &length))       // FUN_00FEBE10
//                      PlayAnim(..., 0x80, ...);         // FUN_005D9890, debug channel "Control/Anim"
//                  SetStat(0x19C, 0);                    // <- ONE-SHOT: consumed and cleared
//              }
//              if (GetStat(0x199) == 0) SetStat(0x199, monsterData->weaponmaterialtype);   // EmitterMaterial
//              if (GetStat(0x029) == 0) SetStat(0x029, monsterData->materialtype);         // ReceiverMaterial
//              if (GetStat(0x1A5) == 0) SetStat(0x1A5, monsterData->collisionradius);      // CharRadius
//
//            The three defaults after it are what identify the object the animation name is read from: PART C
//            shows the same loader writing exactly those offsets from named MonsterData attributes.
//
//   PART C — the MONSTERDATA loader `FUN_010005E0` (debug channel "RDB/RDBMonsterData"), which names the two
//            string members PART B chooses between and the selector it chooses with:
//
//              +0x114 collisionmeshid   +0x118 materialtype        +0x11C weaponmaterialtype
//              +0x120 footstepmaterialtype   +0x124 collisionradius   +0x128 mountgroup
//              +0x13C defaultidleanim   +0x158 spawnanim
//
//            The strings are 0x1C apart and MSVC's `std::string` keeps `_Mysize` at +0x10, so PART B's
//            `[monsterData+0x168] != 0` test is `spawnanim._Mysize != 0` — "does this creature declare one".
//
//   PART D — the class, walked from the vtable's RTTI: `.?AVSimpleChar_t@@`, and the slot index of PART B.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;

import java.io.PrintWriter;

public class DumpSpawnAnimationConsumer extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long SPAWN_APPLY = 0x01458d40L;                       // PART A
    static final long PRODUCER_FROM = 0x01458ed3L, PRODUCER_TO = 0x01458f2eL;

    static final long MODEL_BUILD = 0x01104750L;                       // PART B
    static final long CONSUMER_FROM = 0x011048d1L, CONSUMER_TO = 0x01104a11L;

    static final long MONSTERDATA_LOAD = 0x010005e0L;                  // PART C
    static final long LOADER_FROM = 0x01000905L, LOADER_TO = 0x01000973L;

    static final long SIMPLECHAR_VTABLE = 0x0203e93cL;                 // PART D
    static final long CONSUMER_SLOT = 0x0203e9ccL;

    PrintWriter w;

    public void run() throws Exception {
        w = new PrintWriter(OUT + "spawn_animation_consumer.txt");

        w.println("=== PART A - the producer: npc_stats field 8 (+0x24) -> SetStat(0x19C) ===");
        dumpRange(SPAWN_APPLY, PRODUCER_FROM, PRODUCER_TO);

        w.println();
        w.println("=== PART B - the consumer: play the entrance animation, then CLEAR the stat ===");
        dumpRange(MODEL_BUILD, CONSUMER_FROM, CONSUMER_TO);

        w.println();
        w.println("=== PART C - the MonsterData loader: which member is spawnanim, and the empty-string branch ===");
        dumpRange(MONSTERDATA_LOAD, LOADER_FROM, LOADER_TO);

        w.println();
        w.println("=== PART D - whose virtual PART B is ===");
        long fn = readU32(CONSUMER_SLOT);
        w.printf("  vtable %08x, entry %08x -> %08x  (slot %d)%n",
            SIMPLECHAR_VTABLE, CONSUMER_SLOT, fn, (CONSUMER_SLOT - SIMPLECHAR_VTABLE) / 4);
        w.printf("  RTTI: %s%n", rttiName(SIMPLECHAR_VTABLE));

        w.close();
        println("wrote " + OUT + "spawn_animation_consumer.txt");
    }

    // The instruction window with every referenced string resolved inline - the attribute names in PART C
    // ("spawnanim", "defaultidleanim") are what make the two struct offsets readable without a second lookup.
    void dumpRange(long fnEntry, long from, long to) throws Exception {
        Function f = getFunctionContaining(toAddr(fnEntry));
        w.println("---- " + (f == null ? "(no function)" : f.getName()) + " @ " + Long.toHexString(fnEntry) + " ----");
        Listing listing = currentProgram.getListing();
        for (Address a = toAddr(from); a.getOffset() <= to; ) {
            Instruction in = listing.getInstructionAt(a);
            if (in == null) { a = a.add(1); continue; }
            w.printf("  %s:  %-46s %s%n", in.getAddress(), in, annotate(in));
            a = in.getMaxAddress().add(1);
        }
    }

    String annotate(Instruction in) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < in.getNumOperands(); i++)
            for (Object o : in.getOpObjects(i))
                if (o instanceof ghidra.program.model.scalar.Scalar sc) {
                    String s = stringAt(sc.getUnsignedValue());
                    if (s != null) sb.append("; \"").append(s).append('"');
                }
        return sb.toString();
    }

    // MSVC RTTI: vtable-4 -> complete object locator, +0xC -> type descriptor, +8 -> the decorated name.
    String rttiName(long vtable) {
        try {
            long col = readU32(vtable - 4);
            long td = readU32(col + 0xc);
            String s = stringAt(td + 8);
            return s == null ? "(unreadable)" : s;
        } catch (Exception e) {
            return "(unreadable)";
        }
    }

    long readU32(long va) throws Exception {
        return currentProgram.getMemory().getInt(toAddr(va)) & 0xFFFFFFFFL;
    }

    // The ASCII string at a VA, whether or not Ghidra defined data there - most operand pointers here are bare
    // pointers into a string blob with no Data object, so a defined-data-only lookup annotates only some of them.
    String stringAt(long va) {
        Address a = toAddr(va);
        if (!currentProgram.getMemory().contains(a)) return null;
        StringBuilder sb = new StringBuilder();
        try {
            for (int i = 0; i < 64; i++) {
                byte b = currentProgram.getMemory().getByte(a.add(i));
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return null;
                sb.append((char) b);
            }
        } catch (Exception e) { return null; }
        return sb.length() >= 3 ? sb.toString() : null;
    }
}
