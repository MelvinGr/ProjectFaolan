// Decompile every function referencing the ability/item requirement fields _requiredLevel / RequiredLevel and the
// level stats CurrentLevel / CharacterLevel - to find the usability check (item._requiredLevel vs player.<level>)
// and which PLAYER level field/stat is read.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpRequiredLevel.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpRequiredLevel extends GhidraScript {
    static final String[] NEEDLES = {"_requiredLevel", "RequiredLevel", "CurrentLevel", "CharacterLevel", "CastingLevel",
        "GetLevel", "m_nLevel", "e_Level", "GetCharacterLevel", "e_CurrentLevel"};

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/required_level.txt");
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
                if (s.equals(n)) { out.println("STRING \"" + s + "\" @ " + d.getAddress()); addrs.add(d.getAddress()); }
        }
        for (Symbol sym : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = sym.getName(false);
            for (String needle : NEEDLES)
                if (n.equals(needle)) out.println("SYMBOL " + sym.getName(true) + " @ " + sym.getAddress());
        }
        out.println();

        Set<String> seen = new HashSet<>();
        int budget = 30;
        for (Address sa : addrs) {
            for (Reference ref : getReferencesTo(sa)) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || f.getBody().getNumAddresses() > 2500 || !seen.add(f.getEntryPoint().toString())) continue;
                if (budget-- <= 0) break;
                out.println("========= " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " =========");
                DecompileResults res = di.decompileFunction(f, 90, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
                out.println();
            }
        }
        out.close();
        println("wrote /home/melvin/Development/Ghidra/Aoc/required_level.txt");
    }
}
