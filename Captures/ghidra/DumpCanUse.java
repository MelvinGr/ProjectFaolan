// The GameAction/item usability check blocks a level-15 char from a level-10 spell -> it reads the player level
// from a field that is NOT stat 0x36. Find the "CanUse" / requirement-check functions and decompile them (+ the
// functions that read the GameAction MinLevel param via the known param-reader FUN_0089bd80) to see which player
// field/stat the level requirement is compared against.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpCanUse.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpCanUse extends GhidraScript {
    static final String[] NEEDLES = {"CanUse", "can not use", "cannot use", "not high enough level", "level required",
        "RequiredLevel", "MinLevel", "Requirement not met", "IndexMinLevel", "not_high_enough"};
    static final long PARAM_READER = 0x0089bd80L; // GameAction param getter (from #271 recast RE)

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/can_use.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        // 1. String-ref based.
        Set<Address> addrs = new HashSet<>();
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            Data d = data.next();
            Object v = d.getValue();
            if (v == null) continue;
            String s = v.toString();
            for (String n : NEEDLES)
                if (s.toLowerCase().contains(n.toLowerCase())) { out.println("STRING \"" + s + "\" @ " + d.getAddress()); addrs.add(d.getAddress()); }
        }
        out.println();

        Set<String> seen = new HashSet<>();
        int budget = 20;
        for (Address sa : addrs) {
            for (Reference ref : getReferencesTo(sa)) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || f.getBody().getNumAddresses() > 4000 || !seen.add(f.getEntryPoint().toString())) continue;
                if (budget-- <= 0) break;
                out.println("========= CanUse ref: " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " =========");
                DecompileResults res = di.decompileFunction(f, 90, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
                out.println();
            }
        }

        // 2. Callers of the GameAction param reader that are small (likely requirement checks).
        Function pr = getFunctionAt(toAddr(PARAM_READER));
        if (pr != null) {
            out.println("##### callers of param-reader FUN_0089bd80 (small = likely requirement checks) #####");
            int cb = 12;
            for (Reference ref : getReferencesTo(pr.getEntryPoint())) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || f.getBody().getNumAddresses() > 1500 || !seen.add(f.getEntryPoint().toString())) continue;
                if (cb-- <= 0) break;
                out.println("========= caller: " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " =========");
                DecompileResults res = di.decompileFunction(f, 90, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
                out.println();
            }
        }
        out.close();
        println("wrote /home/melvin/Development/Ghidra/Aoc/can_use.txt");
    }
}
