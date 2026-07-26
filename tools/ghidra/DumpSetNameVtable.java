// Dump the whole SetNameIIR_t vtable (0x01fca6a0) and decompile every slot, to find Activate - the method that
// applies the parsed name (this+0x48/0x4c/0x4e/string) onto the target entity (+0x2c4/+0x2d4). #363.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;

public class DumpSetNameVtable extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long VT = 0x01fca6a0L;
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "setname_vtable.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Address vt = toAddr(VT);
        for (int i = 0; i < 24; i++) {
            long p = getInt(vt.add(i * 4L)) & 0xffffffffL;
            if (p == 0 || p < 0x401000L || p > 0x1f00000L) { out.println("slot["+i+"] = 0x"+Long.toHexString(p)+" (skip)"); continue; }
            Function f = getFunctionContaining(toAddr(p));
            out.println("\n########## slot["+i+"] -> 0x"+Long.toHexString(p)+" "+(f==null?"null":f.getName()+" @"+f.getEntryPoint()+" size="+f.getBody().getNumAddresses()));
            if (f == null) continue;
            if (f.getBody().getNumAddresses() > 700) { out.println("  (large; entry "+f.getEntryPoint()+" - decompiling)"); }
            DecompileResults res = di.decompileFunction(f, 120, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<fail>");
        }
        out.close();
        println("done -> " + OUT + "setname_vtable.txt");
    }
}
