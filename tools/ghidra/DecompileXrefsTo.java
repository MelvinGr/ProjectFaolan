// ADDR=hex[,hex...] : decompile EVERY function that references any of the addresses (a global / singleton pointer / vtable) into
// $FAOLAN_GHIDRA_OUT/decompile_xrefs.txt. The consumer hunt for a struct that lives behind a global: an offset scan
// over the image is noise (+0xa0 has thousands of hits), but a function that reads the SINGLETON first and then a
// small offset off it is the field's consumer, and the set of functions touching the global is a few hundred at
// most. Written for #146 (PvPMatchMaking_c at DAT_0297d358, whose stored PvPMatchRules_c starts at +0x98).
//   ADDR=0297d358 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DecompileXrefsTo.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DecompileXrefsTo extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        TreeMap<Address, Function> fns = new TreeMap<>();
        int refs = 0;
        StringBuilder targets = new StringBuilder();
        for (String s : System.getenv("ADDR").split(",")) {
            Address target = toAddr(Long.parseLong(s.trim(), 16));
            targets.append(target).append(' ');
            ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(target);
            while (ri.hasNext()) {
                Reference r = ri.next();
                refs++;
                Function f = getFunctionContaining(r.getFromAddress());
                if (f != null) fns.put(f.getEntryPoint(), f);
            }
        }
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        PrintWriter out = new PrintWriter(OUT + "decompile_xrefs.txt");
        out.println("# " + refs + " references to " + targets + "from " + fns.size() + " functions");
        for (Function f : fns.values()) {
            out.println("######## " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " ########");
            try {
                DecompileResults r = di.decompileFunction(f, 120, monitor);
                out.println(r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception e) { out.println("  <exception " + e + ">"); }
            out.println();
        }
        out.close();
        println("WROTE " + OUT + "decompile_xrefs.txt (" + refs + " refs, " + fns.size() + " functions)");
    }
}
