// CONSTS=2d24368,2d246dc : every instruction whose immediate is one of the listed values, the function that
// contains it, and that function decompiled once. The multi-value, decompiling form of ConstScan - written for the
// tweak hunt in #592, where a client tweak's NAME is a static std::string global and the question is which code
// looks the tweak up under it, so scanning one constant at a time meant one full instruction sweep per name.
//
//   CONSTS=2d24368,2d246dc /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpConstConsumers.java
//
// Output: $FAOLAN_GHIDRA_OUT/const_consumers.txt
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class DumpConstConsumers extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        Set<Long> wanted = new LinkedHashSet<>();
        for (String part : System.getenv("CONSTS").split(",")) {
            wanted.add(Long.parseLong(part.trim(), 16) & 0xFFFFFFFFL);
        }

        java.io.File file = new java.io.File(OUT, "const_consumers.txt");
        PrintWriter out = new PrintWriter(file);
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Map<Long, List<Instruction>> hits = new LinkedHashMap<>();
        for (Long value : wanted) {
            hits.put(value, new ArrayList<Instruction>());
        }

        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            InstructionIterator it = currentProgram.getListing().getInstructions(block.getStart(), true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Instruction insn = it.next();
                if (insn.getAddress().compareTo(block.getEnd()) > 0) {
                    break;
                }
                for (int i = 0; i < insn.getNumOperands(); i++) {
                    for (Object obj : insn.getOpObjects(i)) {
                        if (obj instanceof Scalar) {
                            long value = ((Scalar) obj).getUnsignedValue() & 0xFFFFFFFFL;
                            if (hits.containsKey(value)) {
                                hits.get(value).add(insn);
                            }
                        }
                    }
                }
            }
        }

        Set<Function> toDecompile = new LinkedHashSet<>();
        for (Map.Entry<Long, List<Instruction>> entry : hits.entrySet()) {
            out.println("\n=== 0x" + Long.toHexString(entry.getKey()) + "  (" + entry.getValue().size() + " site(s))");
            for (Instruction insn : entry.getValue()) {
                Function fn = getFunctionContaining(insn.getAddress());
                out.println("    " + insn.getAddress() + ": " + insn
                    + "   [" + (fn == null ? "no function" : fn.getName() + "@" + fn.getEntryPoint()) + "]");
                if (fn != null) {
                    toDecompile.add(fn);
                }
            }
        }

        out.println("\n\n=== decompiled containing functions ===");
        for (Function fn : toDecompile) {
            out.println("\n---- " + fn.getName() + " @ " + fn.getEntryPoint() + " ----");
            DecompileResults res = decomp.decompileFunction(fn, 60, monitor);
            out.println(res == null || res.getDecompiledFunction() == null
                ? "(decompilation failed)"
                : res.getDecompiledFunction().getC());
        }

        out.close();
        decomp.dispose();
        println("wrote " + file);
    }
}
