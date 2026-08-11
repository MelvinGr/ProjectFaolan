// Settles what the client does with the NPC stat-core's `npc_type` (NpcStats protobuf field 14): it is the
// nameplate's RANK title — 1 `Minion`, 2 `MiniBoss`, 3 `Boss`, 4 nothing (Gitea #508, named in #797).
//
// The captures cannot answer it. They establish that Keaira and King Conan — the pair the chat annotation
// compares ("talked to npc keira (skulls next to name. like king conan)") — both carry npc_type 3, and that the
// value's 59-row roster reads as named bosses. What they cannot show is the CLIENT's reading, and the roster
// contains a tamed pet and a Tortage civilian, so "3 = the skull rank" did not survive it. (The skull is a
// different field entirely: `normalset`, stat 205 — see DumpNormalsetReaders.java.)
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpNpcTypeRank.java
//
// Output: $FAOLAN_GHIDRA_OUT/npc_type_rank.txt, in three parts that together are the proof:
//
//   PART A — the DEAD END, kept so it is not re-walked: `npc_type` is not a defined string in the image. The
//            client stores its protobuf field names only inside serialized FileDescriptorProto blobs, so the
//            accessor cannot be anchored on the field's own name.
//
//   PART B — the CONSUMER. `FUN_011d8b00`, the nameplate builder, reads a non-stat member and DEC/JZ-chains it
//            into three LDB category-100 tokens. The disassembly window below shows the whole selector:
//
//              GetStat(0xCD) == 4         -> "Pray"        (the critter tier, short-circuits the rank)
//              [[dynel+0x330]+0x18] == 1  -> "Minion"
//                                    == 2 -> "MiniBoss"
//                                    == 3 -> "Boss"
//              otherwise                  -> nothing rendered
//
//   PART C — the PRODUCER, which is what makes it npc_type rather than an inference. `FUN_01458d40` (the
//            SimpleChar spawn apply, called from `FUN_011215e0`) copies the spawn's `npc_stats` submessage —
//            the pointer at ProtoCoreCharacterIIR `+0x5c`, with the protobuf default-instance fallback — field
//            by field - four of the copies land on stats the client's own name table names, the fifth on the
//            member PART B reads:
//
//              npc_stats+0x18 -> SetStat(0x79)  SuperFamily         = NpcStats field 5
//              npc_stats+0x2c -> SetStat(0xCD)  Normalset           = field 10
//              npc_stats+0x30 -> SetStat(0x7F)  NPCProfessionName   = field 11
//              npc_stats+0x34 -> SetStat(0xC9)  NPCProfessionType   = field 12
//              npc_stats+0x3c -> [[dynel+0x330]+0x18]               = field 14  <- npc_type
//
//            The same window also copies three fields of the SIBLING submessage `stats` (CharacterStats, the
//            pointer at +0x58), and they land on the same base offset:
//
//              stats+0x34 -> SetStat(0x51C) LooksPacket         = CharacterStats field 12
//              stats+0x48 -> SetStat(0x40)  HeadMesh            = field 17
//              stats+0x58 -> SetStat(0x15)  BCC                 = field 21
//
//            Seven field->stat identities across TWO messages, every one of them naming base +0x08, which puts
//            NpcStats field 14 at +0x3c: the slot written straight into the member PART B renders. Nothing here
//            rests on which NPCs happen to carry which value.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.*;

public class DumpNpcTypeRank extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long NAMEPLATE_BUILDER = 0x011d8b00L; // the rank selector, PART B
    static final long RANK_FROM = 0x011d8f0dL, RANK_TO = 0x011d8f74L;

    static final long SPAWN_APPLY = 0x01458d40L; // the npc_stats copier, PART C
    static final long APPLY_FROM = 0x01458fcbL, APPLY_TO = 0x0145909cL;

    PrintWriter w;

    public void run() throws Exception {
        w = new PrintWriter(OUT + "npc_type_rank.txt");

        w.println("=== PART A - the dead end: is \"npc_type\" a string in the image? ===");
        int hits = 0;
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            Data d = data.next();
            Object v = d.getValue();
            if (v == null || !v.toString().contains("npc_type")) continue;
            hits++;
            w.printf("  @ %s  %s%n", d.getAddress(), v);
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getAddress())) {
                Function f = getFunctionContaining(r.getFromAddress());
                w.printf("      xref %s  in %s%n", r.getFromAddress(), f == null ? "(no function)" : f.getName());
            }
        }
        if (hits == 0)
            w.println("  none - protobuf field names live only inside the serialized FileDescriptorProto blobs,\n"
                + "  so the accessor is NOT reachable from the field's own name. Anchor on the consumer instead.");

        w.println();
        w.println("=== PART B - the consumer: the nameplate rank selector ===");
        dumpRange(NAMEPLATE_BUILDER, RANK_FROM, RANK_TO);

        w.println();
        w.println("=== PART C - the producer: the SimpleChar spawn apply copying npc_stats ===");
        dumpRange(SPAWN_APPLY, APPLY_FROM, APPLY_TO);

        w.close();
        println("wrote " + OUT + "npc_type_rank.txt");
    }

    // The instruction window, with every referenced string resolved inline - the tokens ("Minion", "MiniBoss",
    // "Boss") are what make PART B readable without a second lookup.
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

    // The ASCII string at a VA, whether or not Ghidra defined data there - the token operands in PART B are bare
    // pointers into a string blob, and half of them have no Data object, so a defined-data-only lookup silently
    // annotates some of the branches and not others.
    String stringAt(long va) {
        Address a = toAddr(va);
        if (!currentProgram.getMemory().contains(a)) return null;
        StringBuilder sb = new StringBuilder();
        try {
            for (int i = 0; i < 64; i++) {
                byte b = currentProgram.getMemory().getByte(a.add(i));
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return null;
                sb.append((char)b);
            }
        } catch (Exception e) { return null; }
        return sb.length() >= 3 ? sb.toString() : null;
    }
}
