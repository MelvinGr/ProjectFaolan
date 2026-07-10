// Decompile the shared reader helpers referenced by the IIR ReadSubClass methods (DumpVtableReaders).
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;
public class DumpHelpers extends GhidraScript {
    static final long[] ADDRS = { 0x0080c620L, 0x00cbfb80L, 0x00870500L, 0x00cbf5d0L };
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/helpers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long a : ADDRS) {
            Function f = getFunctionContaining(toAddr(a));
            out.println("==== FUN @0x"+Long.toHexString(a)+" -> "+(f==null?"null":f.getName()+" size="+f.getBody().getNumAddresses())+" ====");
            if (f==null) continue;
            try { DecompileResults r=di.decompileFunction(f,90,monitor);
                out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"  <fail>"); } catch(Exception e){ out.println(e); }
            out.println();
        }
        out.close(); println("done -> Captures/ghidra/output/helpers.txt");
    }
}
