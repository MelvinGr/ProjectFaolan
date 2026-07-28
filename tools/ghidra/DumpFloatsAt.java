// Dumps the 4- and 8-byte float at each address in the ADDRS env var (hex, comma-separated), plus the raw
// words, into $FAOLAN_GHIDRA_OUT/floats.txt (falling back to the system temp dir).
//
//   ADDRS=02d24704,02d24714 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpFloatsAt.java
//
// For reading the tuning constants a decompiled formula loads (armor mitigation, combat scaling, ...): the
// decompiler shows `DAT_02d24704` and nothing about its value, and a constant that is ZERO in the image is the
// tell that it is filled at runtime from the client's tweak table (RDB 1000516) rather than baked in.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import java.io.PrintWriter;

public class DumpFloatsAt extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String env = System.getenv("ADDRS");
        if (env == null || env.isEmpty()) { println("set ADDRS=addr,addr"); return; }

        PrintWriter out = new PrintWriter(OUT + "floats.txt");
        for (String hex : env.split(",")) {
            hex = hex.trim();
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex, 16));
            try {
                int raw = currentProgram.getMemory().getInt(a);
                long raw64 = currentProgram.getMemory().getLong(a);
                out.printf("%s  int=0x%08X  f32=%s  f64=%s%n", hex, raw,
                        Float.toString(Float.intBitsToFloat(raw)), Double.toString(Double.longBitsToDouble(raw64)));
            } catch (Exception e) {
                out.printf("%s  <unreadable: %s>%n", hex, e.getMessage());
            }
        }

        out.close();
        println("WROTE " + OUT + "floats.txt");
    }
}
