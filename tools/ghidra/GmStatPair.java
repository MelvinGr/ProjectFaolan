// Finds functions that reference BOTH scalar immediates in CONSTS (comma-separated hex) — used to
// isolate the GM stat gate: a function touching both 0xD7 (GM Level) and 0xD0 (GM Disable Target
// System) is almost certainly the reader/writer of the GM level. Reports the intersection + the hit
// instructions per function so the GetStat/compare context is visible.
// Usage: CONSTS=d7,d0 analyzeHeadless <proj> aoc -process AgeOfConan.exe -noanalysis \
//          -scriptPath tools/ghidra -postScript GmStatPair.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.util.*;

public class GmStatPair extends GhidraScript {
    public void run() throws Exception {
        String[] parts = System.getenv("CONSTS").trim().split(",");
        long[] targets = new long[parts.length];
        for (int i = 0; i < parts.length; i++)
            targets[i] = Long.parseLong(parts[i].trim(), 16) & 0xFFFFFFFFL;

        // fn name -> set of consts seen, and fn name -> list of hit lines
        Map<String, Set<Long>> seen = new HashMap<>();
        Map<String, List<String>> lines = new HashMap<>();

        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction insn = it.next();
            Function f = getFunctionContaining(insn.getAddress());
            if (f == null) continue;
            String fn = f.getName() + "@" + f.getEntryPoint();
            for (int op = 0; op < insn.getNumOperands(); op++) {
                for (Object o : insn.getOpObjects(op)) {
                    if (o instanceof Scalar) {
                        long v = ((Scalar) o).getUnsignedValue() & 0xFFFFFFFFL;
                        for (long t : targets) {
                            if (v == t) {
                                seen.computeIfAbsent(fn, k -> new HashSet<>()).add(t);
                                lines.computeIfAbsent(fn, k -> new ArrayList<>())
                                     .add("      " + insn.getAddress() + "  " + insn);
                            }
                        }
                    }
                }
            }
        }

        println("=== functions referencing ALL of " + Arrays.toString(parts) + " ===");
        for (Map.Entry<String, Set<Long>> e : seen.entrySet()) {
            if (e.getValue().size() == targets.length) {
                println("FN " + e.getKey());
                for (String l : lines.get(e.getKey())) println(l);
            }
        }
    }
}
