// Resolve <CLASS>::vftable by its QUALIFIED name (FindSym/VtableSlots match only the leaf name "vftable",
// so they never find it), print its first slots, and decompile one of them - slot 8 is ReadSubClass for every
// IIR class (see docs/re + DumpVtableReaders.java).
//   CLASS=GameTimeIIR_t [SLOT=8] /opt/ghidra/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript VtReader.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Symbol;

public class VtReader extends GhidraScript {
    @Override public void run() throws Exception {
        String cls = System.getenv("CLASS");
        String want = cls + "::vftable";
        int slot = System.getenv("SLOT") == null ? 8 : Integer.parseInt(System.getenv("SLOT").trim());

        Address vt = null;
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            if (s.getName(true).equals(want)) { vt = s.getAddress(); break; }
        }
        if (vt == null) { println("no symbol " + want); return; }
        println("vftable " + want + " @ " + vt);

        for (int i = 0; i < 16; i++) {
            long p = getInt(vt.add(i * 4L)) & 0xffffffffL;
            Function f = getFunctionContaining(toAddr(p));
            println("  slot[" + i + "] 0x" + Long.toHexString(p) + (f != null ? "  " + f.getName() + "@" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() : "  (not a function)"));
        }

        long target = getInt(vt.add(slot * 4L)) & 0xffffffffL;
        Function f = getFunctionContaining(toAddr(target));
        println("\n===== slot[" + slot + "] decompiled =====");
        if (f == null) { println("(not a function)"); return; }
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        DecompileResults res = di.decompileFunction(f, 120, monitor);
        println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "(decompile failed)");
    }
}
