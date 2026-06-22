import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
public class DumpCbf5d0 extends GhidraScript {
    static final long[] FNS = { 0x00cbf5d0L, 0x00cba1b0L };
    @Override public void run() throws Exception {
        java.io.PrintWriter out = new java.io.PrintWriter("/home/melvin/Development/Ghidra/Aoc/cbf5d0.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long p : FNS) {
            Function f = getFunctionContaining(toAddr(p));
            out.println("###### 0x"+Long.toHexString(p)+" -> "+(f==null?"null":f.getName()+" @"+f.getEntryPoint()));
            if (f!=null){ DecompileResults r=di.decompileFunction(f,200,monitor);
                out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"<fail>"); }
            out.println();
        }
        out.close(); println("done");
    }
}
