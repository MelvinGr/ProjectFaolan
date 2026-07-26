// Decompiles a fixed list of functions by entry address (hex, comma-separated in FNS env var), into
// $FAOLAN_GHIDRA_OUT/decompile.txt (falling back to the system temp dir).
//   FNS=005230b0,00515420 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DecompileFns.java
//
// It writes a FILE rather than printing: analyzeHeadless prefixes only the first line of a multi-line println with
// "INFO <script>>", so the usual `grep <script>` on its output silently drops every line of C but the first.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;

public class DecompileFns extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String env = System.getenv("FNS");
        if (env == null || env.isEmpty()) { println("set FNS=addr,addr"); return; }

        DecompInterface di = new DecompInterface();
        // Without explicit options the decompiler silently produces a null result for every function - the header
        // prints and nothing follows, which reads like "this function is empty" rather than "the tool is misconfigured".
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        PrintWriter out = new PrintWriter(OUT + "decompile.txt");

        for (String hex : env.split(",")) {
            hex = hex.trim();
            if (hex.isEmpty()) continue;
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex, 16));
            Function f = getFunctionContaining(a);
            out.println("========== FUN @" + hex + " " + (f != null ? f.getName() : "(none)") + " ==========");
            if (f == null) continue;
            DecompileResults res = di.decompileFunction(f, 120, monitor);
            DecompiledFunction df = res == null ? null : res.getDecompiledFunction();
            out.println(df != null ? df.getC() : "(decompile failed)");
        }

        out.flush();
        out.close();
        println("WROTE " + OUT + "decompile.txt");
    }
}
