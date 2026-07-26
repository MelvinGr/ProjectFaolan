// Settles what the client does with the count-framed list word (0x3F1 / (count+1)*0x3F1) — and specifically
// whether GameActionListIIR's 0x7E2 is a COUNT or a constant. The captures cannot answer it: all 1305
// GameActionListIIR frames carry head=0x7E2, which is equally consistent with "a constant" and "a 1-element count
// with variable-length tails". The client's own reader is the oracle: it either divides by 0x3F1 or compares.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpListFraming.java
//
// Output: $FAOLAN_GHIDRA_OUT/list_framing.txt
//   PART A — every function whose CODE references the scalar 1009 (0x3F1), with the decompiled body. These are the
//            list readers/writers; what they do with it (== compare, / divide, * multiply) is the answer.
//   PART B — the GameActionList reader, found by anchoring on its type-name strings, decompiled in full.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;

public class DumpListFraming extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long UNIT = 0x3F1L;   // 1009 - the framing unit
    static final long ONE_ELEM = 0x7E2L; // 2018 - a 1-element list; the value that keeps passing for a constant

    // Strings that should sit near the GameActionList reader (same anchoring trick as DumpIirReaders).
    static final String[] ANCHORS = {
        "GameActionListIIR_c::ReadSubClass", "gameactionlistiir.cpp", "GameActionListIIR_c",
        "GameActionList_t", "gameactionlist.cpp",
    };

    Address findStr(String s) throws Exception {
        return currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), s.getBytes("US-ASCII"), null, true, monitor);
    }

    String decompile(DecompInterface di, Function f) {
        DecompileResults r = di.decompileFunction(f, 60, monitor);
        if (r == null || r.getDecompiledFunction() == null) return "<decompile failed>";
        return r.getDecompiledFunction().getC();
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "list_framing.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        // ---------------- PART A: who references 1009 / 2018 at all ----------------
        out.println("################## PART A: functions referencing 0x3F1 (1009) or 0x7E2 (2018)");
        Map<Function, List<String>> hits = new LinkedHashMap<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext() && !monitor.isCancelled()) {
            Instruction in = it.next();
            for (int op = 0; op < in.getNumOperands(); op++) {
                for (Object o : in.getOpObjects(op)) {
                    if (!(o instanceof Scalar)) continue;
                    long v = ((Scalar) o).getUnsignedValue();
                    if (v != UNIT && v != ONE_ELEM) continue;
                    Function f = getFunctionContaining(in.getAddress());
                    String tag = (v == UNIT ? "0x3F1" : "0x7E2");
                    hits.computeIfAbsent(f, k -> new ArrayList<>())
                        .add(in.getAddress() + "  " + tag + "  " + in.toString());
                }
            }
        }
        out.println("functions touching the framing word: " + hits.size());
        for (Map.Entry<Function, List<String>> e : hits.entrySet()) {
            Function f = e.getKey();
            out.println("\n===== " + (f == null ? "<no function>" : f.getName() + " @ " + f.getEntryPoint()));
            for (String s : e.getValue()) out.println("    " + s);
            if (f != null) {
                out.println("--- decompiled:");
                out.println(decompile(di, f));
            }
        }

        // ---------------- PART B: the GameActionList reader specifically ----------------
        out.println("\n\n################## PART B: GameActionList reader (anchored on type strings)");
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> dumped = new HashSet<>();
        for (String anchor : ANCHORS) {
            Address s = findStr(anchor);
            out.println("\n########## anchor \"" + anchor + "\" -> " + s);
            if (s == null) continue;
            for (Reference r : rm.getReferencesTo(s)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f == null || !dumped.add(f.getEntryPoint().getOffset())) continue;
                out.println("\n===== " + f.getName() + " @ " + f.getEntryPoint() + "  (ref from " + r.getFromAddress() + ")");
                out.println(decompile(di, f));
            }
        }

        out.close();
        println("wrote " + OUT + "list_framing.txt");
    }
}
