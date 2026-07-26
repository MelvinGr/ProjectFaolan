import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;
public class DumpYouCanNotUse extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "youcannotuse.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        long[] targets = {0x01fc1324L, 0x01fc0ab8L}; // YouCanNotUseYet, YouCanNotUse
        Set<String> seen = new HashSet<>();
        for (long t : targets) {
            for (Reference ref : getReferencesTo(toAddr(t))) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || !seen.add(f.getEntryPoint().toString())) continue;
                out.println("========= " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " (ref@" + ref.getFromAddress() + ") =========");
                DecompileResults res = di.decompileFunction(f, 180, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
                out.println();
            }
        }
        out.close(); println("done");
    }
}
