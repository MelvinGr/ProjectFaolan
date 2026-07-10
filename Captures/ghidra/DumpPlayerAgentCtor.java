// Decompile the PlayerAgentProxy constructor (the register()/setName() sequence that assigns each method its
// wire opcode + 32-bit id) plus the base RPC send/register functions. The constructor references the method log
// strings AND the "PlayerAgentProxy_t::called with a too low id!" string; we find functions referencing the
// Authenticate log string (@01f819d0) and the too-low-id string, and decompile each, plus SendMethodInvocation
// (FUN_0046c910) and the base id check (FUN_0046c250). Output: playeragent_ctor.txt.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;
import java.util.*;

public class DumpPlayerAgentCtor extends GhidraScript {
    DecompInterface di;
    PrintWriter out;
    Set<Long> dumped = new HashSet<>();

    Address findNext(Address start, byte[] needle) throws Exception {
        return currentProgram.getMemory().findBytes(start, needle, null, true, monitor);
    }
    void decompAt(long off, String why) {
        Function f = getFunctionAt(toAddr(off));
        if (f == null) f = getFunctionContaining(toAddr(off));
        if (f == null || !dumped.add(f.getEntryPoint().getOffset())) return;
        out.println("######## " + why + " :: " + f.getName() + " @" + f.getEntryPoint()
                    + " size=" + f.getBody().getNumAddresses() + " ########");
        try {
            DecompileResults res = di.decompileFunction(f, 120, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
        } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
        out.println();
    }
    // decompile every function that references the 4-byte little-endian pointer to `strAddr`.
    void decompReferencers(long strAddr, String why) throws Exception {
        byte[] ptr = new byte[] { (byte)(strAddr & 0xFF), (byte)((strAddr>>8)&0xFF), (byte)((strAddr>>16)&0xFF), (byte)((strAddr>>24)&0xFF) };
        Address p = findNext(currentProgram.getMinAddress(), ptr);
        int g = 0;
        while (p != null && g++ < 30) {
            Function f = getFunctionContaining(p);
            if (f != null) decompAt(f.getEntryPoint().getOffset(), why + " (ref @" + p + ")");
            p = findNext(p.add(1), ptr);
        }
    }

    @Override
    public void run() throws Exception {
        di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/playeragent_ctor.txt");

        // The PlayerAgentProxy_t "too low id" C-string is at 01f81730 with an 8-byte prefix -> real text at 01f81738.
        decompReferencers(0x01f81738L, "PlayerAgentProxy too-low-id ref");
        decompReferencers(0x01f81730L, "PlayerAgentProxy too-low-id(raw) ref");
        // The Authenticate method log string @01f819d0 - referenced by both its method body AND the ctor.
        decompReferencers(0x01f819d0L, "Authenticate-string ref");
        // Base RPC plumbing.
        decompAt(0x0046c910L, "SendMethodInvocation");
        decompAt(0x0046c250L, "base too-low-id/register");

        out.flush(); out.close();
        println("WROTE /home/melvin/Development/Ghidra/Aoc/playeragent_ctor.txt (" + dumped.size() + " functions)");
    }
}
