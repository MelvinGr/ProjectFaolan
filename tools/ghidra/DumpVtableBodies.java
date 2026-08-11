// VT=<vtable base hex> [FROM=] [TO=]: dump the FULL disassembly of every vtable slot's target, following one
// adjustor thunk (`SUB ECX,[ECX-4]; JMP target`). Writes $FAOLAN_GHIDRA_OUT/vtable_bodies.txt, one
// `#### slot N fn=... [thunk from ...]` header per slot, so a parser downstream can pick out whatever shape it
// is after (Gitea #801 wanted `LEA ECX,[EAX + <signal offset>]`, which the decompiler drops).
//   VT=1fa4524 FROM=0 TO=211 analyzeHeadless ... -postScript DumpVtableBodies.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DumpVtableBodies extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override public void run() throws Exception {
        long vt = Long.parseLong(System.getenv("VT").trim(), 16);
        int from = Integer.parseInt(System.getenv().getOrDefault("FROM", "0").trim());
        int to = Integer.parseInt(System.getenv().getOrDefault("TO", "255").trim());
        PrintWriter out = new PrintWriter(OUT + "vtable_bodies.txt");
        for (int i = from; i <= to; i++) {
            long fn;
            try { fn = currentProgram.getMemory().getInt(toAddr(vt + i * 4L)) & 0xFFFFFFFFL; }
            catch (Exception e) { out.println("#### slot " + i + " <unreadable>"); continue; }
            long target = fn;
            Function f = fnAt(fn);
            if (f != null && f.getBody().getNumAddresses() == 8) {
                Instruction first = currentProgram.getListing().getInstructionAt(toAddr(fn));
                Instruction second = first != null ? first.getNext() : null;
                if (first != null && second != null && first.toString().startsWith("SUB ECX,dword ptr [ECX + -0x4]")
                        && second.toString().startsWith("JMP 0x")) {
                    target = Long.parseLong(second.toString().substring(6), 16);
                }
            }
            out.println(String.format("#### slot %d fn=%08x target=%08x", i, fn, target));
            Function t = fnAt(target);
            if (t == null) { out.println("  <no function>"); continue; }
            InstructionIterator it = currentProgram.getListing().getInstructions(t.getBody(), true);
            while (it.hasNext()) { Instruction in = it.next(); out.println("  " + in.getAddress() + ":  " + in); }
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "vtable_bodies.txt");
    }

    private Function fnAt(long a) {
        Function f = getFunctionAt(toAddr(a));
        return f != null ? f : getFunctionContaining(toAddr(a));
    }
}
