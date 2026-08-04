// Every instruction that touches [reg + OFF] - the generalised FindFieldWriters (which is pinned to the
// player's 0xA4 dead-flag). MODE=w lists writes only (the offset is operand 0), MODE=r reads only, MODE=rw both.
//
//   OFF=c8 MODE=w /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindOffAccess.java
//
// Use it when a decompiled expression names a struct field by offset and the question is which loader fills it -
// e.g. the spell template's flag word at +0xC8, whose bit 2 picks percent-of-pool vs flat mana cost (#629).
// Output: $FAOLAN_GHIDRA_OUT/off_access.txt (the hit count goes to stdout; the offset is common, so expect many).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class FindOffAccess extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String needle = "0x" + System.getenv("OFF").toLowerCase() + "]";
        String mode = System.getenv().getOrDefault("MODE", "rw").toLowerCase();
        PrintWriter out = new PrintWriter(OUT + "off_access.txt");
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        var n = 0;
        while (it.hasNext()) {
            Instruction ins = it.next();
            for (var op = 0; op < ins.getNumOperands(); op++) {
                String s = ins.getDefaultOperandRepresentation(op);
                if (!s.contains("[") || !s.contains(needle))
                    continue;
                // Operand 0 is the destination on every x86 form that has one, so it is the write side.
                if (mode.equals("w") && op != 0 || mode.equals("r") && op == 0)
                    continue;

                Function f = getFunctionContaining(ins.getAddress());
                out.printf("%s  %-42s  [%s]%n", ins.getAddress(), ins,
                        f == null ? "?" : f.getName() + "@" + f.getEntryPoint());
                n++;
                break;
            }
        }

        out.close();
        println("WROTE " + OUT + "off_access.txt (" + n + " hits)");
    }
}
