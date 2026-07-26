// Decode the 0x203F GuildFullUpdate renown block (#219). It is NOT an IIR ReadSubClass: the block is the tail
// of the i_pcGuild argument deserialized by CSPlayerInterfaceDispatch_t::DispatchGuildUpdate. Locate that
// handler via its assert string ("...DispatchGuildUpdate() not possible, stream bad after 'i_pcGuild'!" @
// 0x1c68c00), decompile it + the functions it calls (one reads the guild object -> the renown stat-list).
// Run:
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpGuildDispatch.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpGuildDispatch extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long ASSERT_STR = 0x1c68c00L; // "...DispatchGuildUpdate()...stream bad after 'i_pcGuild'!"

    DecompInterface di;

    String decomp(Function f) {
        try {
            DecompileResults res = di.decompileFunction(f, 200, monitor);
            return res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<decompile fail>";
        } catch (Exception ex) { return "<exception: " + ex + ">"; }
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "guild_dispatch.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        // Locate the assert string by its bytes (the file offset 0x1c68c00 is not a virtual address).
        byte[] needle = "DispatchGuildUpdate() not possible, stream bad after 'i_pcGuild'".getBytes("ASCII");
        Address strAddr = currentProgram.getMemory().findBytes(
            currentProgram.getMinAddress(), needle, null, true, monitor);
        out.println("assert string @ " + strAddr);
        // CSPlayerInterface dispatch case 63 (0x203F DispatchGuildUpdate) = FUN_013428d0 (disp_cases.json).
        Function handler = getFunctionContaining(toAddr(0x013428d0L));
        out.println("DispatchGuildUpdate handler = " + (handler == null ? "null" : handler.getName() + " @" + handler.getEntryPoint()));
        if (handler == null) { out.close(); return; }

        out.println("\n================= DispatchGuildUpdate =================");
        out.println(decomp(handler));

        // Decompile the directly-called functions (the i_pcGuild reader is one of them; the renown stat-list
        // reader is nested under it). One level of callees, largest first (the guild-read is substantial).
        Set<Function> callees = new LinkedHashSet<>();
        for (Instruction ins = getInstructionAt(handler.getEntryPoint());
             ins != null && handler.getBody().contains(ins.getAddress()); ins = ins.getNext()) {
            for (Reference ref : ins.getReferencesFrom()) {
                Function c = getFunctionAt(ref.getToAddress());
                if (c != null && c != handler) callees.add(c);
            }
        }
        List<Function> ordered = new ArrayList<>(callees);
        ordered.sort((a, b) -> Long.compare(b.getBody().getNumAddresses(), a.getBody().getNumAddresses()));
        for (Function c : ordered) {
            out.println("\n----------------- callee " + c.getName() + " @" + c.getEntryPoint() + " (size " + c.getBody().getNumAddresses() + ") -----------------");
            out.println(decomp(c));
        }
        out.close();
        println("DumpGuildDispatch done -> " + OUT + "guild_dispatch.txt");
    }
}
