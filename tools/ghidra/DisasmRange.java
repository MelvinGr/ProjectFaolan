// START=hex, END=hex : disassemble [START,END) into $FAOLAN_GHIDRA_OUT/disasm.txt (system temp by default).
// Writes a FILE because analyzeHeadless prefixes only the first line of a multi-line println.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DisasmRange extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    public void run() throws Exception {
        long s = Long.parseLong(System.getenv("START"), 16);
        long e = Long.parseLong(System.getenv("END"), 16);
        Address a = toAddr(s), z = toAddr(e);
        Listing l = currentProgram.getListing();
        try (PrintWriter w = new PrintWriter(OUT + "disasm.txt")) {
            InstructionIterator it = l.getInstructions(a, true);
            while (it.hasNext()) {
                Instruction in = it.next();
                if (in.getAddress().compareTo(z) >= 0) break;
                w.println(in.getAddress() + ": " + in);
            }
        }
        println("wrote " + OUT + "disasm.txt");
    }
}
