import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;
public class DumpMoveTask extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final String[] ANCHORS = { "iir\\movetaskiir.cpp", "MoveTaskIIR_c::Activate[%d:%u], moving to position", "MoveTaskIIR_c::PollStatus" };
    Address findStr(String s) throws Exception {
        return currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), s.getBytes("US-ASCII"), null, true, monitor);
    }
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "movetask.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> dumped = new HashSet<>();
        for (String a : ANCHORS) {
            Address s = findStr(a);
            out.println("################## \""+a+"\" -> "+s);
            if (s==null) continue;
            int g=0;
            for (Reference r : rm.getReferencesTo(s)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f==null || !dumped.add(f.getEntryPoint().getOffset())) continue;
                out.println("==== "+f.getName()+" @"+f.getEntryPoint()+" size="+f.getBody().getNumAddresses()+" ====");
                try { DecompileResults res=di.decompileFunction(f,150,monitor);
                    out.println(res!=null&&res.decompileCompleted()?res.getDecompiledFunction().getC():"<fail>"); } catch(Exception ex){ out.println(ex); }
                out.println();
                if (++g>3) break;
            }
        }
        out.close(); println("done");
    }
}
