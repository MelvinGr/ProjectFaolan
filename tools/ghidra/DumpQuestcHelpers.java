// Pin the two stream helpers used by the Quest_c reader (FUN_0091df50): FUN_00cc0540 (the per-objective
// localized-text/remoteformat reference reader — 4 output words) and FUN_0048f840 (the header "short" read).
// Also FUN_0091ff80 (vector clear) for completeness. #186 — finishing the Quest_c wire grammar.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DumpQuestcHelpers extends GhidraScript {
    static final long[] FNS = { 0x00cc0540L, 0x0048f840L, 0x0091ff80L };
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/questc_helpers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long a : FNS) {
            Function f = getFunctionContaining(toAddr(a));
            out.println("################## FUN_" + Long.toHexString(a)
                + (f == null ? "  <no fn>" : "  @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses()));
            if (f == null) { out.println(); continue; }
            out.println("  -- callees --");
            for (Function c : f.getCalledFunctions(monitor)) out.println("    " + c.getName() + " @" + c.getEntryPoint());
            try {
                DecompileResults res = di.decompileFunction(f, 200, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            out.println();
        }
        out.close();
        println("DumpQuestcHelpers done -> Captures/ghidra/output/questc_helpers.txt");
    }
}
