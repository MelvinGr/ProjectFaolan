// Decompile the nameplate name-resolution functions (#363): anchor on the OverheadText / GetDisplayName strings
// to see how a live entity's floating name is chosen and whether a custom-name field/flag overrides the LDB hash.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpNameRender extends GhidraScript {
    static final String[] NEEDLES = {
        "GetDisplayName doesnt have a valid name for hash",
        "could not create OverheadText_c",
    };
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/name_render.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Set<Function> seen = new LinkedHashSet<>();
        DataIterator it = currentProgram.getListing().getDefinedData(true);
        while (it.hasNext()) {
            Data d = it.next();
            Object v = d.getValue();
            if (!(v instanceof String)) continue;
            String s = (String) v;
            boolean hit = false;
            for (String n : NEEDLES) if (s.contains(n)) hit = true;
            if (!hit) continue;
            out.println("STRING @" + d.getAddress() + " : " + s);
            ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(d.getAddress());
            while (ri.hasNext()) {
                Function f = getFunctionContaining(ri.next().getFromAddress());
                if (f != null) seen.add(f);
            }
        }
        for (Function f : seen) {
            out.println("\n################## " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses());
            DecompileResults res = di.decompileFunction(f, 200, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<fail>");
        }
        out.close();
        println("done -> Captures/ghidra/output/name_render.txt");
    }
}
