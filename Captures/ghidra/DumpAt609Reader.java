// Resolve which client reader consumes the snapshot-tails @609 objective-detail block (#186).
// Candidates: List B driver FUN_00798c70 / record FUN_00922c90, and the QuestFullUpdateIIR (0x63268258)
// ReadSubClass (the @609 bytes have the QuestFullUpdate-content shape). Dump all + QuestFullUpdate's vtable
// slot 8 via the proven vtable-xref method (slot 8 = ReadSubClass; FUN_01121c00 in SimpleChar's vtable).
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;

public class DumpAt609Reader extends GhidraScript {
    static final long KNOWN_READER = 0x01121c00L;
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";

    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }
    int findSlot(Address vt, long target) throws Exception {
        for (int i = 0; i < 200; i++) if ((getInt(vt.add(i*4L))&0xffffffffL)==target) return i;
        return -1;
    }
    void dump(DecompInterface di, PrintWriter out, Function f) {
        if (f == null) { out.println("  <no fn>"); return; }
        out.println("---- " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " ----");
        try { DecompileResults r = di.decompileFunction(f, 200, monitor);
            out.println(r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "  <fail>");
        } catch (Exception ex) { out.println("  <ex: " + ex + ">"); }
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/tmp/ghidra-aoc/at609_reader.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address kv = vtableAddr(KNOWN_VTABLE);
        int slot = kv == null ? -1 : findSlot(kv, KNOWN_READER);
        out.println("ReadSubClass slot = " + slot + "\n");

        // QuestFullUpdate-ish vtables
        out.println("===== Quest* vtables =====");
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = s.getName(true);
            if (n.endsWith("::vftable") && n.toLowerCase().contains("quest")) {
                Address vt = s.getAddress();
                long p = slot >= 0 ? (getInt(vt.add(slot*4L))&0xffffffffL) : 0;
                out.println("  " + n + " @" + vt + "  slot8 -> 0x" + Long.toHexString(p));
            }
        }
        out.println();
        // QuestFullUpdate reader
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = s.getName(true);
            if (n.endsWith("::vftable") && n.toLowerCase().contains("questfullupdate")) {
                long p = getInt(s.getAddress().add(slot*4L))&0xffffffffL;
                out.println("################## " + n + " slot8");
                dump(di, out, getFunctionContaining(toAddr(p)));
                out.println();
            }
        }
        // Re-dump List B driver + record (fresh)
        out.println("################## List B driver FUN_00798c70");
        dump(di, out, getFunctionContaining(toAddr(0x00798c70L)));
        out.println("\n################## List B record FUN_00922c90");
        dump(di, out, getFunctionContaining(toAddr(0x00922c90L)));
        out.close();
        println("DumpAt609Reader done -> /tmp/ghidra-aoc/at609_reader.txt");
    }
}
