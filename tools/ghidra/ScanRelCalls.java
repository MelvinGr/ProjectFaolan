// PTR=<hex addr> : scan EXECUTABLE bytes for E8 rel32 CALLs whose computed target == PTR,
// including bytes Ghidra never disassembled. Complements ScanPtrData (absolute pointers in data).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;

public class ScanRelCalls extends GhidraScript {
    @Override
    public void run() throws Exception {
        long target = Long.parseLong(System.getenv("PTR").trim(), 16);
        int n = 0;
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (!b.isInitialized() || !b.isExecute()) continue;
            byte[] buf = new byte[(int) b.getSize()];
            b.getBytes(b.getStart(), buf);
            long base = b.getStart().getOffset();
            for (int i = 0; i + 4 < buf.length; i++) {
                if (buf[i] != (byte) 0xE8) continue;
                long rel = (buf[i + 1] & 0xFFL) | (buf[i + 2] & 0xFFL) << 8 | (buf[i + 3] & 0xFFL) << 16 | ((long) buf[i + 4]) << 24;
                if (base + i + 5 + rel == target) {
                    println("CALL at " + b.getStart().add(i) + " -> " + Long.toHexString(target));
                    n++;
                }
            }
        }
        println("rel-call hits: " + n);
    }
}
