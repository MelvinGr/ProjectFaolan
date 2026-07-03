// The spell-cast validation: CoreMagicHolder::CastMagicSpell + the YouCanNotUseYet / FeatLevelRequirement /
// CanNotUse checks. Decompile the functions that reference these strings/symbols to see the requirement check and
// which PLAYER level field/stat the spell's required level is compared against.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpCastMagic.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpCastMagic extends GhidraScript {
    static final String[] STR = {"YouCanNotUseYet", "YouCanNotUse", "FeatLevelRequirement", "CanNotUse",
        "CoreMagicHolder::CastMagicSpell", "CastMagicSpell"};

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/cast_magic.txt");
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
            for (String n : STR)
                if (s.contains(n)) { out.println("STRING \"" + s + "\" @ " + d.getAddress()); addrs.add(d.getAddress()); }
        }
        // Symbols (RTTI method names).
        Set<Address> symAddrs = new HashSet<>();
        for (Symbol sym : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = sym.getName(true);
            if (n.contains("CastMagicSpell") || n.contains("CoreMagicHolder")) {
                out.println("SYMBOL " + n + " @ " + sym.getAddress());
                Function f = getFunctionContaining(sym.getAddress());
                if (f != null) symAddrs.add(f.getEntryPoint());
            }
        }
        out.println();

        Set<String> seen = new HashSet<>();
        int budget = 22;
        // Functions referencing the strings.
        for (Address sa : addrs) {
            for (Reference ref : getReferencesTo(sa)) {
                Function f = getFunctionContaining(ref.getFromAddress());
                if (f == null || f.getBody().getNumAddresses() > 4000 || !seen.add(f.getEntryPoint().toString())) continue;
                if (budget-- <= 0) break;
                dump(di, out, f);
            }
        }
        // Symbol functions.
        for (Address a : symAddrs) {
            Function f = getFunctionContaining(a);
            if (f == null || !seen.add(f.getEntryPoint().toString())) continue;
            dump(di, out, f);
        }
        out.close();
        println("wrote /home/melvin/Development/Ghidra/Aoc/cast_magic.txt");
    }

    void dump(DecompInterface di, PrintWriter out, Function f) {
        out.println("========= " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " =========");
        DecompileResults res = di.decompileFunction(f, 90, monitor);
        out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
        out.println();
    }
}
