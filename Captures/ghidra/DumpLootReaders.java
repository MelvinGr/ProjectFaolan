// Decode the loot-window display readers for #226 via the VTABLE-xref method:
//   LootRemoveItemsIIR_c::ReadSubClass  - the s2c packet that drops a single item from the open bag window
//   LootBagFullUpdateIIR_c::ReadSubClass - the bag contents list (why the window shows N-1 items)
// The ReadSubClass slot index is the slot in the KNOWN SimpleCharFullUpdateIIR_t vtable whose pointer == its
// reader (FUN_01121c00); read that slot in each target vtable. Decompile each reader + its direct callees.
// Run:
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpLootReaders.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpLootReaders extends GhidraScript {
    static final long KNOWN_READER = 0x01121c00L;
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";
    static final String[] TARGETS = {
        "LootRemoveItemsIIR_c::vftable",
        "LootBagFullUpdateIIR_c::vftable",
    };

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
            DecompileResults res = di.decompileFunction(f, 180, monitor);
            return res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<decompile fail>";
        } catch (Exception ex) { return "<exception: " + ex + ">"; }
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/loot_readers.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address knownVt = symbolAddr(KNOWN_VTABLE);
        int slot = knownVt == null ? -1 : findSlot(knownVt, KNOWN_READER);
        out.println("ReadSubClass slot index = " + slot);
        if (slot < 0) { out.println("abort"); out.close(); return; }

        for (String t : TARGETS) {
            Address vt = symbolAddr(t);
            out.println("\n################## " + t + " -> " + vt);
            if (vt == null) { out.println("  <no vtable symbol>"); continue; }
            long p = getInt(vt.add(slot * 4L)) & 0xffffffffL;
            Function reader = getFunctionContaining(toAddr(p));
            out.println("ReadSubClass -> 0x" + Long.toHexString(p) + " = " + (reader == null ? "null" : reader.getName()));
            if (reader == null) continue;
            out.println(decomp(reader));

            Set<Function> callees = new LinkedHashSet<>();
            for (Instruction ins = getInstructionAt(reader.getEntryPoint());
                 ins != null && reader.getBody().contains(ins.getAddress()); ins = ins.getNext())
                for (Reference ref : ins.getReferencesFrom()) {
                    Function c = getFunctionAt(ref.getToAddress());
                    if (c != null && c != reader) callees.add(c);
                }
            for (Function c : callees) {
                out.println("\n----------------- callee " + c.getName() + " @" + c.getEntryPoint() + " (size " + c.getBody().getNumAddresses() + ") -----------------");
                out.println(decomp(c));
            }
        }
        out.close();
        println("DumpLootReaders done -> /home/melvin/Development/Ghidra/Aoc/loot_readers.txt");
    }
}
