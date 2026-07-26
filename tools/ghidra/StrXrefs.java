// Find an ASCII string (STR env) in the binary and decompile every function that references it - the general
// "who uses this name/constant" probe. Tweakable lookups, log channels and asset names are all string-keyed in
// AgeOfConan.exe, so this is usually the fastest way into an unnamed subsystem.
//   STR="TWK_TargetPerLevelArmor" [MAX=4] /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript StrXrefs.java
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;

public class StrXrefs extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String needle = System.getenv("STR");
        int max = Integer.parseInt(System.getenv().getOrDefault("MAX", "4"));
        PrintWriter out = new PrintWriter(OUT + "str_xrefs.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();

        Address at = currentProgram.getMinAddress();
        Set<Long> dumped = new HashSet<>();
        int hits = 0;
        while (hits < 8) {
            Address s = currentProgram.getMemory().findBytes(at, needle.getBytes("US-ASCII"), null, true, monitor);
            if (s == null) break;
            hits++;
            out.println("################ \"" + needle + "\" @" + s);
            println("string hit @" + s);
            int shown = 0;
            for (Reference r : rm.getReferencesTo(s)) {
                Function f = getFunctionContaining(r.getFromAddress());
                out.println("  <- ref @" + r.getFromAddress() + " [" + r.getReferenceType() + "] in "
                        + (f != null ? f.getName() + "@" + f.getEntryPoint() : "<no function>"));
                println("  ref @" + r.getFromAddress() + " in " + (f != null ? f.getName() + "@" + f.getEntryPoint() : "?"));
                if (f == null || !dumped.add(f.getEntryPoint().getOffset()) || shown++ >= max) continue;
                DecompileResults res = di.decompileFunction(f, 180, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
                out.println();
            }
            at = s.add(1);
        }

        out.flush();
        out.close();
        println("WROTE " + OUT + "str_xrefs.txt (" + hits + " string hit(s))");
    }
}
