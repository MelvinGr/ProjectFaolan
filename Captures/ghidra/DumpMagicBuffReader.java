// Decode the ApplySpell (0x6B87E316 = MagicBuffIIR) payload grammar for #232: the effect blob the client reads.
//   MagicBuffIIR_c::ReadSubClass  (vtable slot 8) - the wire field reads
//   the Activate method whose format string is "MagicBuffIIR_c::Activate, UpdateBuff with parameters: BuffTemplate "
//     - it names the decoded fields (BuffTemplate, duration, magnitude, ...).
// Run:
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpMagicBuffReader.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpMagicBuffReader extends GhidraScript {
    static final long KNOWN_READER = 0x01121c00L;
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";

    DecompInterface di;

    Address symbolAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }

    int findSlot(Address vt, long target) throws Exception {
        for (int i = 0; i < 256; i++)
            if ((getInt(vt.add(i * 4L)) & 0xffffffffL) == target) return i;
        return -1;
    }

    String decomp(Function f) {
        try {
            DecompileResults r = di.decompileFunction(f, 200, monitor);
            return r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "<fail>";
        } catch (Exception ex) { return "<exception: " + ex + ">"; }
    }

    void dumpWithCallees(PrintWriter out, Function f, String label) {
        out.println("\n================= " + label + " = " + f.getName() + " =================");
        out.println(decomp(f));
        Set<Function> callees = new LinkedHashSet<>();
        for (Instruction ins = getInstructionAt(f.getEntryPoint());
             ins != null && f.getBody().contains(ins.getAddress()); ins = ins.getNext())
            for (Reference ref : ins.getReferencesFrom()) {
                Function c = getFunctionAt(ref.getToAddress());
                if (c != null && c != f) callees.add(c);
            }
        for (Function c : callees) {
            out.println("\n----- callee " + c.getName() + " @" + c.getEntryPoint() + " (size " + c.getBody().getNumAddresses() + ") -----");
            out.println(decomp(c));
        }
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/magicbuff_reader.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address knownVt = symbolAddr(KNOWN_VTABLE);
        int slot = knownVt == null ? -1 : findSlot(knownVt, KNOWN_READER);
        out.println("ReadSubClass slot index = " + slot);

        Address vt = symbolAddr("MagicBuffIIR_c::vftable");
        out.println("MagicBuffIIR_c::vftable -> " + vt);
        if (vt != null && slot >= 0) {
            Function reader = getFunctionContaining(toAddr(getInt(vt.add(slot * 4L)) & 0xffffffffL));
            if (reader != null) dumpWithCallees(out, reader, "MagicBuffIIR_c::ReadSubClass");
        }

        // The Activate method (names the fields via its format string).
        byte[] needle = "UpdateBuff with parameters".getBytes("ASCII");
        Address strAddr = currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), needle, null, true, monitor);
        out.println("\n'UpdateBuff with parameters' string @ " + strAddr);
        if (strAddr != null) {
            long va = strAddr.getOffset();
            byte[] ptr = { (byte) va, (byte) (va >> 8), (byte) (va >> 16), (byte) (va >> 24) };
            Address hit = currentProgram.getMinAddress();
            Function activate = null;
            while ((hit = currentProgram.getMemory().findBytes(hit.next(), ptr, null, true, monitor)) != null) {
                Function f = getFunctionContaining(hit);
                if (f != null) { activate = f; break; }
            }
            if (activate != null) {
                out.println("\n================= MagicBuffIIR_c::Activate = " + activate.getName() + " =================");
                out.println(decomp(activate));
            } else out.println("  (no function references the string)");
        }
        out.close();
        println("DumpMagicBuffReader done -> /home/melvin/Development/Ghidra/Aoc/magicbuff_reader.txt");
    }
}
