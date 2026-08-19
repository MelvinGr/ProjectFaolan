// PTR=<hex addr> : scan initialized data for 32-bit little-endian occurrences of PTR (vtable slots without xrefs).
// Writes OUT/ptr_scan.txt with each hit address and the nearest preceding RTTI/vtable label if any.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import java.io.*;

public class ScanPtrData extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";
    @Override
    public void run() throws Exception {
        long target = Long.parseLong(System.getenv("PTR").trim(), 16);
        byte[] pat = new byte[] { (byte) target, (byte) (target >> 8), (byte) (target >> 16), (byte) (target >> 24) };
        PrintWriter w = new PrintWriter(new FileWriter(OUT + "ptr_scan.txt"));
        int n = 0;
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (!b.isInitialized() || b.isExecute()) continue;
            byte[] buf = new byte[(int) b.getSize()];
            b.getBytes(b.getStart(), buf);
            for (int i = 0; i + 3 < buf.length; i++) {
                if (buf[i] == pat[0] && buf[i + 1] == pat[1] && buf[i + 2] == pat[2] && buf[i + 3] == pat[3]) {
                    Address at = b.getStart().add(i);
                    w.println(b.getName() + " @" + at);
                    n++;
                }
            }
        }
        w.close();
        println("WROTE " + OUT + "ptr_scan.txt (" + n + " hits)");
    }
}
