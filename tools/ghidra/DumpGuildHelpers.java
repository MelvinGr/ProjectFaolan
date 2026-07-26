import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
public class DumpGuildHelpers extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long[] FNS = { 0x0048f840L, 0x0048f6d0L, 0x004e0390L, 0x0048f900L, 0x0048f720L, 0x004d4c70L };
    @Override public void run() throws Exception {
        java.io.PrintWriter out = new java.io.PrintWriter(OUT + "guild_helpers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long p : FNS) {
            Function f = getFunctionContaining(toAddr(p));
            out.println("###### 0x"+Long.toHexString(p)+" -> "+(f==null?"null":f.getName()));
            if (f!=null){ DecompileResults r=di.decompileFunction(f,120,monitor);
                out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"<fail>"); }
            out.println();
        }
        out.close(); println("done");
    }
}
