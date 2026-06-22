// Decompiles a fixed list of functions by entry address (hex, comma-separated in FNS env var).
//   FNS=005230b0,00515420 /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath Captures/ghidra -postScript DecompileFns.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DecompileFns extends GhidraScript {
    @Override
    public void run() throws Exception {
        String env = System.getenv("FNS");
        if (env == null || env.isEmpty()) { println("set FNS=addr,addr"); return; }

        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);

        for (String hex : env.split(",")) {
            hex = hex.trim();
            if (hex.isEmpty()) continue;
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex, 16));
            Function f = getFunctionContaining(a);
            println("========== FUN @" + hex + " " + (f != null ? f.getName() : "(none)") + " ==========");
            if (f == null) continue;
            DecompileResults res = di.decompileFunction(f, 60, monitor);
            DecompiledFunction df = res.getDecompiledFunction();
            println(df != null ? df.getC() : "(decompile failed)");
        }
    }
}
