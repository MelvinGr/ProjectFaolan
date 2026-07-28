// Lists every reference TO each address in the ADDRS env var (hex, comma-separated), with the referring
// function, into $FAOLAN_GHIDRA_OUT/xrefs.txt (falling back to the system temp dir).
//
//   ADDRS=02d24704 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript XrefsTo.java
//
// The companion to DumpFloatsAt: when a tuning constant reads 0 in the image it is filled at runtime, and the
// WRITER is what names it - typically the loader that walks the client's tweak table (RDB 1000516).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;

public class XrefsTo extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String env = System.getenv("ADDRS");
        if (env == null || env.isEmpty()) { println("set ADDRS=addr,addr"); return; }

        PrintWriter out = new PrintWriter(OUT + "xrefs.txt");
        for (String hex : env.split(",")) {
            hex = hex.trim();
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex, 16));
            out.println("========== refs to " + hex + " ==========");
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(a)) {
                Function f = getFunctionContaining(r.getFromAddress());
                out.printf("  %s  %s  in %s%n", r.getFromAddress(), r.getReferenceType(),
                        f == null ? "(no function)" : f.getName() + " @" + f.getEntryPoint());
            }
        }

        out.close();
        println("WROTE " + OUT + "xrefs.txt");
    }
}
