import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
public class DumpMoveTaskTail extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long[] FNS = { 0x00a4a9d0L, 0x00a3e400L, 0x00a3e510L };
    @Override public void run() throws Exception {
        java.io.PrintWriter out=new java.io.PrintWriter(OUT + "movetask_tail.txt");
        DecompInterface di=new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long p: FNS){
            Function f=getFunctionContaining(toAddr(p));
            out.println("###### 0x"+Long.toHexString(p)+" -> "+(f==null?"null":f.getName()));
            if (f!=null){ DecompileResults r=di.decompileFunction(f,140,monitor);
                out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"<fail>"); }
            out.println();
        }
        out.close(); println("done");
    }
}
