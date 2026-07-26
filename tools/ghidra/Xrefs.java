// ADDRS=<hex,...> (or the older single-address ADDR=<hex>): every reference TO each address, with the containing
// function decompiled once. Writes $FAOLAN_GHIDRA_OUT/xrefs.txt rather than printing, because analyzeHeadless
// prefixes only the first line of a multi-line println and a decompiled body would be swallowed by the usual grep.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;
public class Xrefs extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "xrefs.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> done = new HashSet<>();
        var addrs = System.getenv().getOrDefault("ADDRS", System.getenv().getOrDefault("ADDR", ""));
        for (String s : addrs.split(",")) {
            if (s.isBlank()) continue;
            Address a = toAddr(Long.parseLong(s.trim(), 16));
            out.println("############ refs to " + a);
            for (Reference r : rm.getReferencesTo(a)) {
                Function f = getFunctionContaining(r.getFromAddress());
                out.println("  <- @" + r.getFromAddress() + " [" + r.getReferenceType() + "] " + (f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint()));
                if (f == null || !done.add(f.getEntryPoint().getOffset())) continue;
                DecompileResults res = di.decompileFunction(f, 120, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<fail>");
            }
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "xrefs.txt");
    }
}
