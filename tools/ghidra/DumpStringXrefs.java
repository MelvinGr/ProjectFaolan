// Find every defined string containing a substring (STR env), then decompile the functions that reference them.
// The string-anchor route into a subsystem when you know a log/assert message but not the code:
//   STR=ClassAutotrain analyzeHeadless ... -postScript DumpStringXrefs.java
// Complements DumpXrefsTo.java (which takes an address) and DecompAddrs.java (which takes a function).
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

public class DumpStringXrefs extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String needle = System.getenv("STR");
        int budget = Integer.parseInt(System.getenv().getOrDefault("BUDGET", "12"));
        PrintWriter out = new PrintWriter(OUT + "string_xrefs.txt");
        out.println("######## strings containing \"" + needle + "\" ########");

        Set<Address> hits = new LinkedHashSet<>();
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            Data d = data.next();
            Object v = d.getValue();
            if (v == null) continue;
            String s = v.toString();
            if (!s.contains(needle)) continue;
            out.println("  @" + d.getAddress() + "  \"" + s.replace("\n", "\\n") + "\"");
            hits.add(d.getAddress());
        }

        Set<Function> fns = new LinkedHashSet<>();
        for (Address a : hits)
            for (Reference r : getReferencesTo(a)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f != null) fns.add(f);
            }

        out.println();
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        for (Function f : fns) {
            if (budget-- <= 0) { out.println("...budget exhausted, " + fns.size() + " functions matched"); break; }
            out.println("######## " + f.getName() + " @" + f.getEntryPoint() + " ########");
            DecompileResults res = di.decompileFunction(f, 180, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            out.println();
        }

        out.flush(); out.close();
        println("WROTE " + OUT + "string_xrefs.txt (" + hits.size() + " strings, " + fns.size() + " functions)");
    }
}
