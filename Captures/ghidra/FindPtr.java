import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
public class FindPtr extends GhidraScript {
    @Override public void run() throws Exception {
        long target = Long.parseLong(System.getenv("PTR"),16);
        Memory mem = currentProgram.getMemory();
        for (MemoryBlock blk : mem.getBlocks()) {
            if (!blk.isInitialized()) continue;
            Address a = blk.getStart(), end = blk.getEnd();
            while (a.compareTo(end) < 0) {
                try {
                    long v = Integer.toUnsignedLong(mem.getInt(a));
                    if (v == target) {
                        println("PTR @ "+a+" -> 0x"+Long.toHexString(target));
                        // dump 12 vtable slots around it (this is likely a vtable)
                        for (int s=-2; s<10; s++) {
                            Address sl = a.add(s*4L);
                            long fp = Integer.toUnsignedLong(mem.getInt(sl));
                            println(String.format("    slot[%+d] @%s = 0x%08x", s, sl, fp));
                        }
                    }
                } catch (Exception e) {}
                a = a.add(4);
            }
        }
    }
}
