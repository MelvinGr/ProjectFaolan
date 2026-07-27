// Decompile every function that REFERENCES a given data address (ADDR env, hex) - the way in to a global that
// is populated at runtime and so has no static contents to read (a std::vector global, a table filled by an
// initializer). Complements DecompAddrs.java, which needs you to already know the function.
//   ADDR=2d25da4 analyzeHeadless ... -postScript DumpXrefsTo.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpXrefsTo extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        Address target = toAddr(Long.parseLong(System.getenv("ADDR").trim(), 16));
        PrintWriter out = new PrintWriter(OUT + "xrefs_to.txt");
        out.println("######## references to " + target + " ########");

        Set<Function> fns = new LinkedHashSet<>();
        for (Reference r : getReferencesTo(target)) {
            Function f = getFunctionContaining(r.getFromAddress());
            out.println("  from " + r.getFromAddress() + " (" + r.getReferenceType() + ")"
                + (f != null ? " in " + f.getName() + " @" + f.getEntryPoint() : " <no function>"));
            if (f != null) fns.add(f);
        }

        out.println();
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        for (Function f : fns) {
            out.println("######## " + f.getName() + " @" + f.getEntryPoint() + " ########");
            DecompileResults res = di.decompileFunction(f, 180, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            out.println();
        }

        out.flush(); out.close();
        println("WROTE " + OUT + "xrefs_to.txt (" + fns.size() + " functions)");
    }
}
