// RFROM=hex RTO=hex : decompile EVERY function whose entry lies in [RFROM,RTO) into $FAOLAN_GHIDRA_OUT/decompile_range.txt.
// The band-scoped DecompileFns: when a GUI view class owns a module (its widget-name strings all sit in one address
// run), decompiling the whole band and grepping it for the struct's storage offsets finds the FILLER that names the
// fields - the [[widget_names_to_semantics]] recipe - without first knowing which function it is. Written for #146
// (the PvP match buildup view, whose readers of PvPMatchMaking_c+0x98.. name the PvPMatchRules_c columns).
//   RFROM=01733000 RTO=01740000 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DecompileRange.java
// Env names are RFROM/RTO (not FROM/TO) so it composes with DumpStringsInRange / FindOffInRange in one headless run.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DecompileRange extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        Address from = toAddr(Long.parseLong(System.getenv("RFROM"), 16));
        Address to = toAddr(Long.parseLong(System.getenv("RTO"), 16));
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        PrintWriter out = new PrintWriter(OUT + "decompile_range.txt");
        int n = 0;
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(from, true);
        while (it.hasNext()) {
            Function f = it.next();
            if (f.getEntryPoint().compareTo(to) >= 0) break;
            out.println("######## " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " ########");
            try {
                DecompileResults r = di.decompileFunction(f, 120, monitor);
                out.println(r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception e) { out.println("  <exception " + e + ">"); }
            out.println();
            n++;
        }
        out.close();
        println("WROTE " + OUT + "decompile_range.txt (" + n + " functions)");
    }
}
