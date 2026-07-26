// Find every place a 32-bit ADDRESS appears as raw little-endian bytes (ADDR env, hex) and report the instruction
// + containing function. Use when Ghidra's own reference table is incomplete - a global that `getReferencesTo`
// reports as never-read is often read through instructions the analyzer never disassembled or resolved.
//   ADDR=2d246fc [DECOMP=1] /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript FindAddrOperand.java
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import java.io.PrintWriter;
import java.util.*;

public class FindAddrOperand extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        long target = Long.parseLong(System.getenv("ADDR").trim(), 16);
        boolean decomp = System.getenv("DECOMP") != null;
        byte[] pattern = new byte[] { (byte) target, (byte) (target >> 8), (byte) (target >> 16), (byte) (target >> 24) };

        DecompInterface di = null;
        if (decomp) {
            di = new DecompInterface();
            di.setOptions(new DecompileOptions());
            di.openProgram(currentProgram);
        }

        PrintWriter out = new PrintWriter(OUT + "addr_operand.txt");
        out.println("occurrences of " + Long.toHexString(target) + " as LE bytes:");
        Address at = currentProgram.getMinAddress();
        Set<Long> fns = new LinkedHashSet<>();
        int hits = 0;
        while (hits < 400) {
            Address h = currentProgram.getMemory().findBytes(at, pattern, null, true, monitor);
            if (h == null) break;
            hits++;
            // the operand starts 1-2 bytes into the instruction, so look a little before it
            Instruction ins = getInstructionContaining(h);
            if (ins == null) {
                for (int back = 1; back <= 6 && ins == null; back++)
                    ins = getInstructionContaining(h.subtract(back));
            }

            Function f = ins != null ? getFunctionContaining(ins.getAddress()) : null;
            out.println("  @" + h + (ins != null ? "  " + ins.getAddress() + ": " + ins : "  <no instruction - data>")
                    + (f != null ? "  in " + f.getName() + "@" + f.getEntryPoint() : ""));
            if (f != null) fns.add(f.getEntryPoint().getOffset());
            at = h.add(1);
        }

        out.println("\n" + hits + " occurrence(s), " + fns.size() + " distinct function(s)");
        println(hits + " occurrence(s), " + fns.size() + " distinct function(s)");
        if (di != null) {
            for (Long off : fns) {
                Function f = getFunctionAt(toAddr(off));
                out.println("\n######## " + f.getName() + "@" + f.getEntryPoint() + " ########");
                DecompileResults res = di.decompileFunction(f, 180, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            }
        }

        out.flush();
        out.close();
        println("WROTE " + OUT + "addr_operand.txt");
    }
}
