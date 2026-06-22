import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
public class DumpPathHelpers extends GhidraScript {
    static final long[] FNS = { 0x00a31ec0L, 0x0079f1c0L, 0x0079e910L, 0x0048f550L };
    @Override public void run() throws Exception {
        java.io.PrintWriter out=new java.io.PrintWriter("/home/melvin/Development/Ghidra/Aoc/path_helpers.txt");
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
