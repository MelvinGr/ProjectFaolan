// Prints the ASCII string bytes at each VA in ADDRS (comma-separated hex, no 0x). Utility for resolving
// &DAT_xxxx operands Ghidra didn't auto-define as strings (e.g. console-command verb/category names).
// Usage: ADDRS=01f22798,01f2316c analyzeHeadless <proj> aoc -process AgeOfConan.exe -noanalysis \
//          -scriptPath tools/ghidra -postScript ReadStrAt.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.MemoryAccessException;

public class ReadStrAt extends GhidraScript {
    public void run() throws Exception {
        for (String s : System.getenv("ADDRS").split(",")) {
            Address a = toAddr(Long.parseLong(s.trim(), 16));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 128; i++) {
                int b;
                try { b = getByte(a.add(i)) & 0xff; }
                catch (MemoryAccessException e) { break; }
                if (b == 0) break;
                sb.append((b >= 32 && b < 127) ? (char) b : '.');
            }
            println("STR @" + s.trim() + " = \"" + sb + "\"");
        }
    }
}
