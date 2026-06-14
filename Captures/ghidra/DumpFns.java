// Decompile a fixed list of function addresses (the quest deserializer chain). Run:
//   /opt/ghidra/support/analyzeHeadless /tmp/ghidra-aoc/proj aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpFns.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;

public class DumpFns extends GhidraScript {
    static final long[] ADDRS = { 0x0091df50L, 0x0091cf10L };

    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter("/tmp/ghidra-aoc/quest_fns.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        for (long addr : ADDRS) {
            Address a = toAddr(addr);
            Function f = getFunctionContaining(a);
            out.println("==== FUN @0x" + Long.toHexString(addr) + " -> " + (f == null ? "null" : f.getName() + " size=" + f.getBody().getNumAddresses()) + " ====");
            if (f == null) continue;
            try {
                DecompileResults res = di.decompileFunction(f, 200, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            out.println();
        }
        out.close();
        println("DumpFns done");
    }
}
