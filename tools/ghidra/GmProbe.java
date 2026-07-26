// Batched string-xref probe for the GM investigation. STRS=comma-separated needles.
// For each needle: find every occurrence, list xref functions, decompile each unique one.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;

public class GmProbe extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String[] needles = System.getenv("STRS").split(",");
        int max = Integer.parseInt(System.getenv().getOrDefault("MAX", "6"));
        PrintWriter out = new PrintWriter(OUT + "gm_probe.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> dumped = new HashSet<>();

        for (String needle : needles) {
            needle = needle.trim();
            if (needle.isEmpty()) continue;
            out.println("\n#################### NEEDLE: \"" + needle + "\"");
            println("NEEDLE " + needle);
            Address at = currentProgram.getMinAddress();
            int hits = 0;
            while (hits < 12) {
                Address s = currentProgram.getMemory().findBytes(at, needle.getBytes("US-ASCII"), null, true, monitor);
                if (s == null) break;
                hits++;
                out.println("  === string @" + s);
                int shown = 0;
                for (Reference r : rm.getReferencesTo(s)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    out.println("    <- ref @" + r.getFromAddress() + " [" + r.getReferenceType() + "] in "
                            + (f != null ? f.getName() + "@" + f.getEntryPoint() : "<no function>"));
                    if (f == null || !dumped.add(f.getEntryPoint().getOffset()) || shown++ >= max) continue;
                    DecompileResults res = di.decompileFunction(f, 200, monitor);
                    out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "    <decompile failed>");
                    out.println();
                }
                at = s.add(1);
            }
            out.println("  (" + hits + " occurrence(s))");
        }
        out.flush();
        out.close();
        println("WROTE " + OUT + "gm_probe.txt");
    }
}
