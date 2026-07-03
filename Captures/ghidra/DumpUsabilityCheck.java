// Find the client's item/spell usability check that emits "Your class cannot use this item until level N".
// Strategy: locate the requirement strings (RequiredLevel / MinLevel / GameAction_MinLevel / LevelReq),
// find every function that references them, decompile those + their direct callers to see which PLAYER
// value (which stat / entity field) the level requirement is compared against.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpUsabilityCheck.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpUsabilityCheck extends GhidraScript {
    static final String[] NEEDLES = {"RequiredLevel", "MinLevel", "GameAction_MinLevel", "LevelReq", "IndexMinLevel"};

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/usability_check.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        Set<Address> stringAddrs = new HashSet<>();
        // Find the requirement strings in defined data.
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            Data d = data.next();
            Object v = d.getValue();
            if (v == null) continue;
            String s = v.toString();
            for (String n : NEEDLES)
                if (s.equals(n) || s.equalsIgnoreCase(n)) {
                    out.println("STRING \"" + s + "\" @ " + d.getAddress());
                    stringAddrs.add(d.getAddress());
                }
        }
        out.println();

        Set<String> seen = new HashSet<>();
        for (Address sa : stringAddrs) {
            for (Reference ref : getReferencesTo(sa)) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || !seen.add(f.getEntryPoint().toString())) continue;
                out.println("========= referenced by " + f.getName() + " @" + f.getEntryPoint() + " (from " + ref.getFromAddress() + ") =========");
                DecompileResults res = di.decompileFunction(f, 120, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
                out.println();
            }
        }
        out.close();
        println("wrote /home/melvin/Development/Ghidra/Aoc/usability_check.txt");
    }
}
