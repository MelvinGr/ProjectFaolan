// Prints every NUL-terminated ASCII run in an address RANGE, with its address. The neighbours of a string are
// often its table: a data-table's column names, a command registry's verbs and an enum's labels are all laid out
// consecutively, so reading around a known string recovers the whole set that ReadStrAt (one address at a time)
// and FindStr (one needle at a time) cannot.
//   FROM=01f71f00 TO=01f72200 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpStringsInRange.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

public class DumpStringsInRange extends GhidraScript {
    @Override
    public void run() throws Exception {
        long from = Long.parseLong(System.getenv("FROM").trim(), 16);
        long to = Long.parseLong(System.getenv("TO").trim(), 16);

        var sb = new StringBuilder();
        long start = from;
        for (long a = from; a <= to; a++) {
            int b;
            try {
                b = currentProgram.getMemory().getByte(toAddr(a)) & 0xFF;
            } catch (Exception e) {
                b = 0;
            }

            if (b >= 0x20 && b < 0x7F) {
                if (sb.length() == 0)
                    start = a;
                sb.append((char)b);
                continue;
            }

            // Runs of 4+ printable characters are the ones worth seeing; shorter ones are mostly alignment noise.
            if (sb.length() >= 4)
                println(String.format("%08x  %s", start, sb));
            sb.setLength(0);
        }

        if (sb.length() >= 4)
            println(String.format("%08x  %s", start, sb));
    }
}
