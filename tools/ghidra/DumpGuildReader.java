// Recover GuildUpdateIIR_c::ReadSubClass (the 0x203F GuildFullUpdate / renown-block reader, #219) via the
// VTABLE-xref method (ghidra_vtable_xref_readers): the ReadSubClass slot index is the slot in the KNOWN
// SimpleCharFullUpdateIIR_t vtable whose pointer == its reader (FUN_01121c00); read that same slot in
// GuildUpdateIIR_c::vftable -> the guild reader. Decompile it + its directly-called functions (the
// counter-region framing is likely a sub-reader), so the 220-byte renown counter region can be decoded.
// Run:
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpGuildReader.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpGuildReader extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long KNOWN_READER = 0x01121c00L;            // SimpleCharFullUpdateIIR_t::ReadSubClass
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";

    Address symbolAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }

    List<Address> symbolsContaining(String frag) {
        List<Address> r = new ArrayList<>();
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).contains(frag)) r.add(s.getAddress());
        return r;
    }

    int findSlot(Address vt, long target) throws Exception {
        for (int i = 0; i < 256; i++)
            if ((getInt(vt.add(i * 4L)) & 0xffffffffL) == target) return i;
        return -1;
    }

    DecompInterface di;

    String decomp(Function f) {
        try {
            DecompileResults res = di.decompileFunction(f, 180, monitor);
            return res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<decompile fail>";
        } catch (Exception ex) { return "<exception: " + ex + ">"; }
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "guild_reader.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address knownVt = symbolAddr(KNOWN_VTABLE);
        int slot = knownVt == null ? -1 : findSlot(knownVt, KNOWN_READER);
        out.println("ReadSubClass slot index = " + slot);

        // Locate the guild vtable (exact symbol, else any symbol mentioning the class).
        Address vt = symbolAddr("GuildUpdateIIR_c::vftable");
        if (vt == null) {
            out.println("no exact ::vftable symbol; candidates containing 'GuildUpdateIIR':");
            for (Address a : symbolsContaining("GuildUpdateIIR")) {
                out.println("  " + a + "  " + getSymbolAt(a));
                if (vt == null && getSymbolAt(a).getName(true).contains("vftable")) vt = a;
            }
        }
        out.println("GuildUpdateIIR_c::vftable -> " + vt);
        if (vt == null || slot < 0) { out.println("abort"); out.close(); return; }

        long p = getInt(vt.add(slot * 4L)) & 0xffffffffL;
        Function reader = getFunctionContaining(toAddr(p));
        out.println("ReadSubClass -> 0x" + Long.toHexString(p) + " = " + (reader == null ? "null" : reader.getName()));
        out.println("\n================= GuildUpdateIIR_c::ReadSubClass =================");
        if (reader == null) { out.close(); return; }
        out.println(decomp(reader));

        // Decompile the functions the reader directly calls (the framing sub-readers).
        Set<Function> callees = new LinkedHashSet<>();
        for (Instruction ins = getInstructionAt(reader.getEntryPoint());
             ins != null && reader.getBody().contains(ins.getAddress()); ins = ins.getNext()) {
            for (Reference ref : ins.getReferencesFrom()) {
                Function c = getFunctionAt(ref.getToAddress());
                if (c != null) callees.add(c);
            }
        }
        for (Function c : callees) {
            out.println("\n----------------- callee " + c.getName() + " @" + c.getEntryPoint() + " (size " + c.getBody().getNumAddresses() + ") -----------------");
            out.println(decomp(c));
        }
        out.close();
        println("DumpGuildReader done -> " + OUT + "guild_reader.txt");
    }
}
