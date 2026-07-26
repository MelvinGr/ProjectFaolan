// Scans all instructions for a scalar immediate operand (CONST env, hex) and reports the containing
// functions. Used to locate the TEA cipher by its delta 0x9E3779B9 (or the decrypt sum 0xC6EF3720).
//   CONST=9e3779b9 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindConst.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;

public class FindConst extends GhidraScript {
    @Override
    public void run() throws Exception {
        long target = Long.parseLong(System.getenv("CONST").trim(), 16) & 0xFFFFFFFFL;
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        int hits = 0;
        java.util.LinkedHashSet<String> fns = new java.util.LinkedHashSet<>();
        while (it.hasNext() && hits < 200) {
            Instruction insn = it.next();
            for (int op = 0; op < insn.getNumOperands(); op++) {
                Object[] objs = insn.getOpObjects(op);
                for (Object o : objs) {
                    if (o instanceof Scalar) {
                        long v = ((Scalar) o).getUnsignedValue() & 0xFFFFFFFFL;
                        if (v == target) {
                            hits++;
                            Function f = getFunctionContaining(insn.getAddress());
                            String name = f != null ? (f.getName() + "@" + f.getEntryPoint()) : ("?@" + insn.getAddress());
                            println("HIT " + insn.getAddress() + "  " + insn + "   [" + name + "]");
                            fns.add(name);
                        }
                    }
                }
            }
        }
        println("=== functions with const 0x" + Long.toHexString(target) + " ===");
        for (String s : fns) println("  " + s);
    }
}
