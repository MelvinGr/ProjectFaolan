// Decompile a comma-separated list of function addresses (ADDRS env, hex) - a general one-off helper.
//   ADDRS=487900,40ec10 analyzeHeadless ... -postScript DecompAddrs.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;

public class DecompAddrs extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        PrintWriter out = new PrintWriter(OUT + "decomp_addrs.txt");
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
        println("WROTE " + OUT + "decomp_addrs.txt");
    }
}
