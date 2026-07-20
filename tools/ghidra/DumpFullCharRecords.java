// Second pass: the per-record sub-readers inside FullPlayerCharacterIIR's list regions (the "UnknownBefore"
// progression block). Pins the record grammars for: the 0x3F1 object list (FUN_0091cf10/FUN_0091df50),
// the 112-byte record list (FUN_00922c90), the StatList_t entry (FUN_0139d210), the FUN_0077d220/d5b0
// list records (FUN_0077db70), and the length-prefixed string/blob reader (FUN_00cbfb80). #91 / .bin removal.
// Run:
//   /opt/ghidra/support/analyzeHeadless Captures/ghidra/output aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpFullCharRecords.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpFullCharRecords extends GhidraScript {
    static final long[] FNS = {
        0x0091cf10L, 0x0091df50L, // first 0x3F1 object list (0x120-byte objects)
        0x00922c90L,              // FUN_00798c70's 112-byte record reader
        0x0139d210L,              // StatList_t entry reader
        0x0077d5b0L, 0x0077db70L, // FUN_0077d220 list records
        0x00cbfb80L,              // length-prefixed blob/string reader (known)
        0x0048f720L,              // ReadU64? (used by trailing pair-list)
    };

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/fullchar_records.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long a : FNS) {
            Function f = getFunctionContaining(toAddr(a));
            out.println("################## FUN_" + Long.toHexString(a)
                + (f == null ? "  <no fn>" : "  @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses()));
            if (f == null) { out.println(); continue; }
            out.println("  -- callees --");
            for (Function c : f.getCalledFunctions(monitor)) out.println("    " + c.getName() + " @" + c.getEntryPoint());
            try {
                DecompileResults res = di.decompileFunction(f, 200, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            out.println();
        }
        out.close();
        println("DumpFullCharRecords done -> Captures/ghidra/output/fullchar_records.txt");
    }
}
