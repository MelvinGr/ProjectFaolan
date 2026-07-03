// How does the client determine the LOCAL player's class/profession (for GameAction_Profession usability)?
// Find refs to CharacterClass / RDBPlayerClassData_c / GameAction_Profession and decompile the functions that
// READ a player class/profession field, to see which stat/packet field the class comes from.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpPlayerClass.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpPlayerClass extends GhidraScript {
    static final String[] NEEDLES = {
        "CharacterClass", "RDBPlayerClassData_c", "GameAction_Profession", "GetClassName",
        "RDBPlayerClassData_c::PlayerClassMapDataReady"
    };

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/player_class.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        Set<Address> addrs = new HashSet<>();
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) {
            Data d = data.next();
            Object v = d.getValue();
            if (v == null) continue;
            String s = v.toString();
            for (String n : NEEDLES)
                if (s.contains(n)) { out.println("STRING \"" + s + "\" @ " + d.getAddress()); addrs.add(d.getAddress()); }
        }
        // Also enumerate symbols with these names (vtables / methods).
        for (Symbol sym : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = sym.getName(true);
            for (String needle : NEEDLES)
                if (n.contains(needle)) out.println("SYMBOL " + n + " @ " + sym.getAddress());
        }
        out.println();

        Set<String> seen = new HashSet<>();
        int budget = 25;
        for (Address sa : addrs) {
            for (Reference ref : getReferencesTo(sa)) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || !seen.add(f.getEntryPoint().toString())) continue;
                if (budget-- <= 0) break;
                out.println("========= " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " (ref from " + ref.getFromAddress() + ") =========");
                DecompileResults res = di.decompileFunction(f, 90, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
                out.println();
            }
        }
        out.close();
        println("wrote /home/melvin/Development/Ghidra/Aoc/player_class.txt");
    }
}
