// Decompile a comma-separated list of function addresses (ADDRS env, hex) - a general one-off helper.
//   ADDRS=487900,40ec10 analyzeHeadless ... -postScript DecompAddrs.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;

public class DecompAddrs extends GhidraScript {
    @Override
    public void run() throws Exception {
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        PrintWriter out = new PrintWriter("Captures/ghidra/output/decomp_addrs.txt");
        for (String s : System.getenv("ADDRS").split(",")) {
            long off = Long.parseLong(s.trim(), 16);
            Function f = getFunctionAt(toAddr(off));
            if (f == null) f = getFunctionContaining(toAddr(off));
            out.println("######## @" + Long.toHexString(off) + " :: " + (f != null ? f.getName() + " @" + f.getEntryPoint() : "<none>") + " ########");
            if (f != null) {
                DecompileResults res = di.decompileFunction(f, 120, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            }
            out.println();
        }
        out.flush(); out.close();
        println("WROTE Captures/ghidra/output/decomp_addrs.txt");
    }
}
