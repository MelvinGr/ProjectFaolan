// Prints a C++ class's vtable address and the functions it points at, plus every function that REFERENCES the vtable
// (constructors, destructors, and anything that type-checks against it). Written for #570 - naming the Quest_c record's
// `@+0x14` flag bits - where the question is "who reads this field", and the answers are reachable two ways: the
// class's own methods (the vtable), and the code that handles instances of it (the references).
//
// The vtable address is also what a LIVE search needs: scanning the running client's heap for that pointer finds every
// instance of the class, and the field then sits at a known offset from the hit. That is how this goes from a static
// candidate list to a watchpoint on a real object.
//
//   CLASS=Quest_c /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpClassVtable.java
//
// Output: $FAOLAN_GHIDRA_OUT/class_vtable_<CLASS>.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryAccessException;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.Symbol;
import java.io.PrintWriter;
import java.util.*;

public class DumpClassVtable extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final int MAX_SLOTS = 64; // a vtable walk has to stop somewhere; well past any class here

    public void run() throws Exception {
        String cls = System.getenv().getOrDefault("CLASS", "Quest_c");
        String wanted = cls + "::vftable";

        Symbol vtable = null;
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(true)) {
            if (s.getName().equals(wanted) || s.getName(true).equals(wanted)) {
                vtable = s;
                break;
            }
        }

        if (vtable == null) {
            println("no symbol named " + wanted);
            return;
        }

        PrintWriter out = new PrintWriter(OUT + "class_vtable_" + cls + ".txt");
        out.printf("%s = %s%n%n", wanted, vtable.getAddress());

        out.println("=== virtual methods (vtable slots) ===");
        Address at = vtable.getAddress();
        for (int slot = 0; slot < MAX_SLOTS; slot++) {
            Address entry = at.add((long) slot * 4);
            long target;
            try {
                target = Integer.toUnsignedLong(currentProgram.getMemory().getInt(entry));
            } catch (MemoryAccessException e) {
                break;
            }

            Address fnAddr = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(target);
            Function fn = currentProgram.getFunctionManager().getFunctionAt(fnAddr);
            if (fn == null) break; // ran off the end of the table

            out.printf("  [%2d] %s  %s%n", slot, fnAddr, fn.getName());
        }

        out.println();
        out.println("=== functions REFERENCING the vtable (ctors/dtors/type checks - i.e. code that handles instances) ===");
        Set<String> seen = new TreeSet<>();
        for (Reference ref : currentProgram.getReferenceManager().getReferencesTo(vtable.getAddress())) {
            Function fn = currentProgram.getFunctionManager().getFunctionContaining(ref.getFromAddress());
            seen.add(fn == null
                    ? ref.getFromAddress() + "  (not in a function)"
                    : fn.getEntryPoint() + "  " + fn.getName());
        }

        for (String line : seen)
            out.println("  " + line);

        out.printf("%n%d referencing site(s).%n", seen.size());
        out.close();
        println("wrote " + OUT + "class_vtable_" + cls + ".txt");
    }
}
