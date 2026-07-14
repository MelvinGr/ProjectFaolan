// Recover SetNameIIR_t::ReadSubClass (the in-world n3 IIR name reader) via the vtable slot-8 xref (#363).
// Run:
//   /opt/ghidra/support/analyzeHeadless Captures/ghidra/output aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpSetNameReader.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;

public class DumpSetNameReader extends GhidraScript {
    static final long KNOWN_READER = 0x01121c00L;            // SimpleCharFullUpdateIIR_t::ReadSubClass
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";
    static final String[] TARGETS = {
        "SetNameIIR_t::vftable", "SetNameIIR::vftable", "SetNameIIR_c::vftable",
    };

    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }
    int findSlot(Address vt, long target) throws Exception {
        for (int i = 0; i < 200; i++) {
            long p = getInt(vt.add(i * 4L)) & 0xffffffffL;
            if (p == target) return i;
        }
        return -1;
    }
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/setname_reader.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Address knownVt = vtableAddr(KNOWN_VTABLE);
        int slot = knownVt == null ? -1 : findSlot(knownVt, KNOWN_READER);
        out.println("ReadSubClass slot index = " + slot);
        if (slot < 0) { out.println("could not locate slot; abort"); out.close(); return; }
        for (String t : TARGETS) {
            Address vt = vtableAddr(t);
            out.println("################## " + t + " -> " + vt);
            if (vt == null) { out.println("  <no vtable symbol>"); out.println(); continue; }
            long p = getInt(vt.add(slot * 4L)) & 0xffffffffL;
            Function f = getFunctionContaining(toAddr(p));
            out.println("  slot[" + slot + "] -> 0x" + Long.toHexString(p) + " in " + (f == null ? "null" : f.getName() + " @" + f.getEntryPoint()));
            if (f == null) { out.println(); continue; }
            try {
                DecompileResults res = di.decompileFunction(f, 150, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            out.println();
        }
        out.close();
        println("done -> Captures/ghidra/output/setname_reader.txt");
    }
}
