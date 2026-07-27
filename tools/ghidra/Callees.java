// FN=<entry hex>: every CALLER of that function, decompiled once. The inverse of Callers-by-name.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.*;
public class Callees extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "callers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Set<Long> done = new HashSet<>();
        for (String s : System.getenv("FN").split(",")) {
            Address a = toAddr(Long.parseLong(s.trim(), 16));
            Function target = getFunctionContaining(a);
            out.println("############ callers of " + (target == null ? s : target.getName() + "@" + target.getEntryPoint()));
            if (target == null) continue;
            for (Reference r : getReferencesTo(target.getEntryPoint())) {
                Function f = getFunctionContaining(r.getFromAddress());
                out.println("  <- @" + r.getFromAddress() + " [" + r.getReferenceType() + "] " + (f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint()));
                if (f == null || !done.add(f.getEntryPoint().getOffset())) continue;
                DecompileResults res = di.decompileFunction(f, 120, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<fail>");
            }
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "callers.txt");
    }
}
