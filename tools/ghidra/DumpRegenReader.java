// Recover RegenIIR::ReadSubClass (IIR type 0x28D0AC99) via the vtable-xref method (slot 8), to pin the
// 13-byte payload field boundaries decoded statistically from the captures ([u32 a][u8 f][u32 b][u32 c]).
// Run:
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpRegenReader.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;

public class DumpRegenReader extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final int SLOT = 8; // ReadSubClass vtable slot (proven in DumpVtableReaders)

    @Override public void run() throws Exception {
        java.io.PrintWriter out = new java.io.PrintWriter(OUT + "regen_reader.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        // 1. find every symbol mentioning Regen (locate the exact vtable name + the constructor that sets the type id)
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = s.getName(true);
            if (n.toLowerCase().contains("regen")) out.println("SYM " + n + " @" + s.getAddress());
        }
        out.println();

        // 2. for each Regen vtable, read slot 8 -> the reader -> decompile
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = s.getName(true);
            if (!n.toLowerCase().contains("regen") || !n.contains("vftable")) continue;
            Address vt = s.getAddress();
            long p = getInt(vt.add(SLOT * 4L)) & 0xffffffffL;
            Function f = getFunctionContaining(toAddr(p));
            out.println("################## " + n + " -> vtable " + vt + " slot[" + SLOT + "] -> 0x" + Long.toHexString(p)
                + " in " + (f == null ? "null" : f.getName() + " @" + f.getEntryPoint()));
            if (f == null) continue;
            DecompileResults res = di.decompileFunction(f, 150, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
            out.println();
        }
        out.close();
        println("DumpRegenReader done -> " + OUT + "regen_reader.txt");
    }
}
