// OFF=6c STRIDE=70 : every function that reads [reg + OFF] AND carries the immediate STRIDE in an add/sub/imul/lea -
// the signature of a loop over a vector of fixed-size records reading one field of each. An offset census alone
// cannot see it (+0x6c is thousands of functions), and a stride alone is just a constant; the pair is "who walks
// THIS record type and looks at THIS field". Written for #929: the completed-quest journal's line record is 0x70
// bytes with the unexplained flag at +0x6c, and its reader (FUN_00922c90) was the only function any census found.
//
//   OFF=6c STRIDE=70 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindOffAndStride.java
//
// Output: $FAOLAN_GHIDRA_OUT/off_and_stride.txt, one line per function with both hit counts and the first hit of each.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class FindOffAndStride extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String needle = "0x" + System.getenv("OFF").toLowerCase() + "]";
        long stride = Long.parseLong(System.getenv("STRIDE"), 16);
        Set<String> strideOps = new HashSet<>(Arrays.asList("ADD", "SUB", "IMUL", "LEA", "CMP"));

        PrintWriter out = new PrintWriter(OUT + "off_and_stride.txt");
        int matched = 0;
        FunctionIterator fns = currentProgram.getFunctionManager().getFunctions(true);
        while (fns.hasNext() && !monitor.isCancelled()) {
            Function f = fns.next();
            int offHits = 0, strideHits = 0;
            String firstOff = null, firstStride = null;
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                for (int op = 0; op < ins.getNumOperands(); op++) {
                    String s = ins.getDefaultOperandRepresentation(op);
                    if (s.contains("[") && s.contains(needle) && op != 0) {
                        offHits++;
                        if (firstOff == null) firstOff = ins.getAddress() + " " + ins;
                    }
                    if (strideOps.contains(ins.getMnemonicString()) && !s.contains("[")) {
                        for (Object o : ins.getOpObjects(op)) {
                            if (o instanceof Scalar && ((Scalar) o).getUnsignedValue() == stride) {
                                strideHits++;
                                if (firstStride == null) firstStride = ins.getAddress() + " " + ins;
                            }
                        }
                    }
                }
            }
            if (offHits > 0 && strideHits > 0) {
                matched++;
                out.printf("%s  %-20s  %s x%d (%s)   stride x%d (%s)%n", f.getEntryPoint(), f.getName(), needle, offHits, firstOff, strideHits, firstStride);
            }
        }
        out.close();
        println("FindOffAndStride: " + matched + " function(s) read " + needle + " and carry stride 0x" + Long.toHexString(stride) + " -> " + OUT + "off_and_stride.txt");
    }
}
