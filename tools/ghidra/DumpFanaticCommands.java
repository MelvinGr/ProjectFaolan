// The client's GM ("Fanatic") command catalog: every <Name>Command*::vftable symbol, the function that writes it
// (its constructor - where the verb, the description and the required GM level are registered), and that function's
// decompilation. CLASSRX env narrows the class-name regex (default: every Fanatic* command).
//
// Why the vftable and not the string: a command's verb is usually an LDB key ("FanaticCloakCommand_Name"), not a
// literal, so a string scan finds only the handful with hardcoded text. Every command DOES install its vtable, and
// the only writer of that pointer is the constructor.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;
import java.util.regex.*;

public class DumpFanaticCommands extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        Pattern rx = Pattern.compile(System.getenv().getOrDefault("CLASSRX", "^Fanatic.*Command.*::vftable$"));
        PrintWriter out = new PrintWriter(OUT + "fanatic_commands.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();

        SymbolTable st = currentProgram.getSymbolTable();
        List<Symbol> vtables = new ArrayList<>();
        for (Symbol s : st.getAllSymbols(true)) {
            String n = s.getName(true);
            if (rx.matcher(n).find())
                vtables.add(s);
        }
        vtables.sort(Comparator.comparing(s -> s.getName(true)));
        println("matched " + vtables.size() + " vtable symbol(s)");

        Set<Long> dumped = new HashSet<>();
        for (Symbol v : vtables) {
            out.println("\n#################### " + v.getName(true) + " @" + v.getAddress());
            for (Reference r : rm.getReferencesTo(v.getAddress())) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f == null) {
                    out.println("  <- @" + r.getFromAddress() + " [" + r.getReferenceType() + "] <no function>");
                    continue;
                }
                out.println("  <- @" + r.getFromAddress() + " [" + r.getReferenceType() + "] in " + f.getName() + "@" + f.getEntryPoint());
                if (!dumped.add(f.getEntryPoint().getOffset()))
                    continue;
                DecompileResults res = di.decompileFunction(f, 200, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "    <decompile failed>");
            }
        }

        out.flush();
        out.close();
        println("WROTE " + OUT + "fanatic_commands.txt");
    }
}
