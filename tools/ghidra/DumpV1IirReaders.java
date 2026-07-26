// Recover the ReadSubClass wire-readers for the 11 V1 (undecoded) IIR types via the vtable-slot method
// (same as DumpVtableReaders): find the ReadSubClass slot in a known IIR vtable, then read that slot in each
// target vtable and decompile. Output: v1_readers.txt.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;

public class DumpV1IirReaders extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long KNOWN_READER = 0x01121c00L;            // SimpleCharFullUpdateIIR_t::ReadSubClass
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";
    static final String[] TARGETS = {
        "RegenIIR_c::vftable",
        "LockedToChangedIIR_t::vftable",
        "AreaFormulaIIR_t::vftable",
        "UpdateInventoryIIR_t::vftable",
        "ChainKillIIR_t::vftable",
        "TemplateActionIIR_t::vftable",
        "FeedbackIIR_t::vftable",
        "CrimeIIR_c::vftable",
        "KnubotStartTradeIIR_c::vftable",
        "KnubotFinishTradeIIR_c::vftable",
        "SpellBookIIR_t::vftable",
    };

    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            if (s.getName(true).equals(full)) return s.getAddress();
        }
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
        PrintWriter out = new PrintWriter(OUT + "v1_readers.txt");
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
            out.println("  slot[" + slot + "] -> 0x" + Long.toHexString(p) + " in " + (f == null ? "null" : f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses()));
            if (f == null) { out.println(); continue; }
            try {
                DecompileResults res = di.decompileFunction(f, 150, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            out.println();
        }
        out.close();
        println("DumpV1IirReaders done -> " + OUT + "v1_readers.txt");
    }
}
