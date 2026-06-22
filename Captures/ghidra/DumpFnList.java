// Decompile an explicit list of function addresses (passed via the FNS env var, comma-separated hex) to
// /home/melvin/Development/Ghidra/Aoc/fnlist.txt. General helper for following a decompiled call chain a level at a time.
// Run:
//   FNS=004d4c70,0048f6d0,0048f720 /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath Captures/ghidra -postScript DumpFnList.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;

public class DumpFnList extends GhidraScript {
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/fnlist.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        String fns = System.getenv("FNS");
        if (fns == null) { out.println("set FNS=hex,hex,..."); out.close(); return; }
        for (String h : fns.split(",")) {
            long a = Long.parseLong(h.trim(), 16);
            Function f = getFunctionContaining(toAddr(a));
            out.println("\n================= 0x" + h.trim() + " = " + (f == null ? "null" : f.getName() + " (size " + f.getBody().getNumAddresses() + ")") + " =================");
            if (f == null) continue;
            try {
                DecompileResults res = di.decompileFunction(f, 180, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<fail>");
            } catch (Exception ex) { out.println("<exception: " + ex + ">"); }
        }
        out.close();
        println("DumpFnList done -> /home/melvin/Development/Ghidra/Aoc/fnlist.txt");
    }
}
