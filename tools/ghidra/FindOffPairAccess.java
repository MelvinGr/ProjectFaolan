// Functions that touch BOTH of two struct offsets - the narrowing FindOffAccess needs when one offset alone is
// too common to be a lead. A class is identified by the COMBINATION of members its code touches, so asking "who
// writes +0xC8 *and* also reaches +0x3c" is asking "who handles the object that has both", which is far more
// specific than either question alone.
//
//   OFF=c8 MODE=w WITH=3c /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindOffPairAccess.java
//
// Written for #630: the spell template's flag word at +0xC8 (bit 2 picks percent-of-pool vs flat mana cost) has
// 3,216 writers across the image, but the object it lives on also carries the RDB property accessor at +0x3c,
// which is what FUN_008d6f70 reaches it through.
//
// MODE applies to OFF only (w = writes, r = reads, rw = both); WITH is matched in either operand position, since
// the point of it is co-occurrence rather than direction.
// Output: $FAOLAN_GHIDRA_OUT/off_pair_access.txt, one line per matching FUNCTION with both hit counts.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;

public class FindOffPairAccess extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String needle = "0x" + System.getenv("OFF").toLowerCase() + "]";
        String with = "0x" + System.getenv("WITH").toLowerCase() + "]";
        String mode = System.getenv().getOrDefault("MODE", "rw").toLowerCase();

        // Per function: how many times it touched OFF in the requested direction, and WITH in any.
        Map<Function, int[]> hits = new LinkedHashMap<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            Function f = getFunctionContaining(ins.getAddress());
            if (f == null)
                continue;

            for (var op = 0; op < ins.getNumOperands(); op++) {
                String s = ins.getDefaultOperandRepresentation(op);
                if (!s.contains("["))
                    continue;

                if (s.contains(needle) && !(mode.equals("w") && op != 0) && !(mode.equals("r") && op == 0))
                    hits.computeIfAbsent(f, k -> new int[2])[0]++;
                if (s.contains(with))
                    hits.computeIfAbsent(f, k -> new int[2])[1]++;
            }
        }

        PrintWriter out = new PrintWriter(OUT + "off_pair_access.txt");
        var n = 0;
        for (Map.Entry<Function, int[]> e : hits.entrySet()) {
            int[] c = e.getValue();
            if (c[0] == 0 || c[1] == 0)
                continue;

            out.printf("%s  %-44s  %s x%d, %s x%d%n", e.getKey().getEntryPoint(), e.getKey().getName(),
                    needle, c[0], with, c[1]);
            n++;
        }
        out.close();
        println("functions touching both: " + n + " -> " + OUT + "off_pair_access.txt");
    }
}
