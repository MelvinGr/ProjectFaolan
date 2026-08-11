// Print the NUL-terminated string at each of a list of addresses.
//
// The smallest possible tool, and it exists because the thing you most often need mid-decode is "what is the
// char* this branch pushes" - and every other route to that (a full decompile, the string index, a listing dump)
// answers a much bigger question slowly.
//
//   ADDRS=208ce00,208d31c,208d330 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpStringsAt.java
//
// Hex, no 0x. A byte that is not printable ASCII ends the string, so a wrong address prints as empty rather than
// as pages of noise.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

public class DumpStringsAt extends GhidraScript {
    private static final int MAX = 200;

    @Override public void run() throws Exception {
        String env = System.getenv("ADDRS");
        if (env == null || env.isEmpty()) { println("set ADDRS=hex,hex"); return; }

        for (String hex : env.split(",")) {
            hex = hex.trim();
            if (hex.isEmpty()) continue;

            Address a = toAddr(Long.parseLong(hex, 16));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < MAX; i++) {
                int b;
                try { b = currentProgram.getMemory().getByte(a.add(i)) & 0xff; }
                catch (Exception e) { break; }
                if (b == 0 || b < 0x20 || b > 0x7e) break;
                sb.append((char) b);
            }

            println("0x" + hex + " = \"" + sb + "\"");
        }
    }
}
