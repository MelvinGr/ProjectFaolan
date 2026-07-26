import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;
public class DumpFns extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long[] ADDRS = { 0x004e0390L, 0x01122b10L, 0x0048f900L, 0x0145ac80L };
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "spawn_fns.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long addr : ADDRS) {
            Function f = getFunctionContaining(toAddr(addr));
            out.println("==== FUN @0x"+Long.toHexString(addr)+" -> "+(f==null?"null":f.getName()+" size="+f.getBody().getNumAddresses())+" ====");
            if (f==null) continue;
            try { DecompileResults r=di.decompileFunction(f,90,monitor);
                out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"  <fail>"); } catch(Exception e){ out.println(e); }
            out.println();
        }
        out.close(); println("done");
    }
}
