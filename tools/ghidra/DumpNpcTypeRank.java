// Settles what the client does with the NPC stat-core's `npc_type` field (protobuf field 14) — specifically
// whether it is the nameplate RANK that draws the skull marker next to a boss's name (Gitea #508).
//
// The captures cannot answer it on their own. They establish that Keaira and King Conan — the exact pair the
// chat annotation compares ("talked to npc keira (skulls next to name. like king conan)") — both carry
// npc_type 3, and that the 56 NPCs sharing that value read as a roster of named bosses. What they cannot show
// is the CLIENT's own reading, i.e. whether 3 is a rank at all and what the other three values (1, 2, 4) mean.
// The client's reader is the oracle.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpNpcTypeRank.java
//
// Output: $FAOLAN_GHIDRA_OUT/npc_type_rank.txt
//   PART A — the "npc_type" descriptor string and its xrefs. The client embeds protobuf field NAMES (that is how
//            aoc-client-protos.proto was recovered at all), so the accessor is reachable from the string.
//   PART B — every function referencing the rank-ish UI strings below, decompiled. If a nameplate decoration is
//            selected from npc_type, the switch/compare lives in one of these.
//   PART C — functions whose code compares a value against the small constants 1..4 *and* mention a nameplate or
//            rank string, i.e. the shape a 4-value enum dispatch has.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.*;

public class DumpNpcTypeRank extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    // The protobuf descriptor name, plus the words the nameplate/rank UI is likely to spell.
    static final String[] ANCHORS = {
        "npc_type", "NpcType", "nNpcType", "m_nNpcType",
        "skull", "Skull", "rank", "Rank", "elite", "Elite", "boss", "Boss",
        "nameplate", "NamePlate", "namePlate", "overheadname", "OverheadName"
    };

    PrintWriter w;
    DecompInterface dec;

    public void run() throws Exception {
        w = new PrintWriter(OUT + "npc_type_rank.txt");
        dec = new DecompInterface();
        dec.openProgram(currentProgram);

        w.println("=== PART A — anchor strings and their xrefs ===");
        Map<String, List<Address>> hits = new LinkedHashMap<>();
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            Data d = data.next();
            String s = valueOf(d);
            if (s == null) continue;
            for (String a : ANCHORS) {
                if (!s.equals(a) && !s.contains(a)) continue;
                hits.computeIfAbsent(a, k -> new ArrayList<>()).add(d.getAddress());
                w.printf("  %-18s @ %s  %s%n", a, d.getAddress(), truncate(s));
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getAddress())) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    w.printf("      xref %s  in %s%n", r.getFromAddress(), f == null ? "(no function)" : f.getName() + " @ " + f.getEntryPoint());
                }
                break;
            }
        }
        if (hits.isEmpty()) w.println("  (no anchor strings found — the descriptor may be built rather than stored)");

        w.println();
        w.println("=== PART B — decompiled functions reached from those xrefs ===");
        Set<Address> done = new HashSet<>();
        for (List<Address> addrs : hits.values()) {
            for (Address sa : addrs) {
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(sa)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    if (f == null || !done.add(f.getEntryPoint())) continue;
                    w.println("---- " + f.getName() + " @ " + f.getEntryPoint() + " ----");
                    w.println(decompile(f));
                    w.println();
                }
            }
        }

        w.println();
        w.println("=== PART C — functions comparing against 1..4 near a rank/nameplate string ===");
        // A 4-value enum dispatch shows up as a cluster of small-constant compares in ONE function. Report only
        // functions that touch at least three of {1,2,3,4} so ordinary loop counters do not flood the output.
        for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
            Set<Long> seen = new HashSet<>();
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                String m = ins.getMnemonicString().toLowerCase();
                if (!m.startsWith("cmp") && !m.startsWith("sub") && !m.startsWith("mov")) continue;
                for (int i = 0; i < ins.getNumOperands(); i++) {
                    Object[] ops = ins.getOpObjects(i);
                    for (Object o : ops)
                        if (o instanceof ghidra.program.model.scalar.Scalar sc) {
                            long v = sc.getUnsignedValue();
                            if (v >= 1 && v <= 4) seen.add(v);
                        }
                }
            }
            if (seen.size() >= 3 && done.contains(f.getEntryPoint()))
                w.printf("  %-40s @ %s  compares %s%n", f.getName(), f.getEntryPoint(), new TreeSet<>(seen));
        }

        w.close();
        println("wrote " + OUT + "npc_type_rank.txt");
    }

    String decompile(Function f) {
        DecompileResults res = dec.decompileFunction(f, 60, monitor);
        if (res == null || res.getDecompiledFunction() == null) return "(decompilation failed)";
        return res.getDecompiledFunction().getC();
    }

    static String valueOf(Data d) {
        Object v = d.getValue();
        return v == null ? null : v.toString();
    }

    static String truncate(String s) {
        s = s.replace('\n', ' ');
        return s.length() > 90 ? s.substring(0, 90) + "…" : s;
    }
}
