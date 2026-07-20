import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
public class DumpMoveTaskList extends GhidraScript {
    // ListMoveTask reader + its header helper; PathMoveTask ctor; the mode 5/6/7 object readers (#53).
    static final long[] FNS = { 0x00a3fcd0L, 0x00a48820L, 0x00a479f0L, 0x00a461d0L, 0x00a46340L, 0x00a46440L };
    @Override public void run() throws Exception {
        java.io.PrintWriter out = new java.io.PrintWriter("Captures/ghidra/output/movetask_list.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long p : FNS) {
            Function f = getFunctionContaining(toAddr(p));
            out.println("###### 0x"+Long.toHexString(p)+" -> "+(f==null?"null":f.getName()));
            if (f!=null){ DecompileResults r=di.decompileFunction(f,160,monitor);
                out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"<fail>"); }
            out.println();
        }
        out.close(); println("done");
    }
}
