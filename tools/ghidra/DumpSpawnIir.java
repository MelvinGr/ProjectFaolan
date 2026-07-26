// Decompile SimpleCharFullUpdateIIR_t::ReadSubClass (the FA015807 reader) to recover the 227-byte pre-IIR
// scaffold block layout (the last un-lifted part of the NPC spawn, Gitea #185). Anchors on the unique error
// strings inside the method. Run:
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpSpawnIir.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;

public class DumpSpawnIir extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final String[] ANCHORS = {
        "SimpleCharFullUpdateIIR_t::ReadSubClass() Received a bad stream",
        "Wrong client version of SimpleCharFullupdateIIR",
        "simplecharfullupdateiir.cpp",
    };

    Address findStr(String s) throws Exception {
        return currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), s.getBytes("US-ASCII"), null, true, monitor);
    }

    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "spawn_iir.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> dumped = new HashSet<>();

        for (String anchor : ANCHORS) {
            Address s = findStr(anchor);
            out.println("################## anchor \"" + anchor + "\" -> " + s);
            if (s == null) continue;
            int g = 0;
            for (Reference r : rm.getReferencesTo(s)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f == null || !dumped.add(f.getEntryPoint().getOffset())) continue;
                out.println("==== ref @" + r.getFromAddress() + " in " + f.getName() + " @" + f.getEntryPoint()
                            + " size=" + f.getBody().getNumAddresses() + " ====");
                try {
                    DecompileResults res = di.decompileFunction(f, 200, monitor);
                    out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
                } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
                out.println();
                if (++g > 4) break;
            }
        }
        out.close();
        println("DumpSpawnIir done -> " + OUT + "spawn_iir.txt");
    }
}
