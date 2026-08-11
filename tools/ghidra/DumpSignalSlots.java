// VT=<vtable base hex> [FROM=<first slot index>] [TO=<last slot index>]: for each vtable slot, follow the
// adjustor thunk (`SUB ECX,[ECX-4]; JMP target`) and report the SIGNAL the override fires - the
// `CALL <singleton getter>; LEA ECX,[EAX + <off>]; JMP <emit>` shape a no-arg dispatch compiles to.
// Written for Gitea #801 (CSPlayerInterface 0xC3 / 0xC8): the LEA is what distinguishes two overrides whose
// decompilations are identical, so the map slot -> signal offset has to be read off the DISASSEMBLY.
//   VT=1fa4524 FROM=0 TO=211 analyzeHeadless ... -postScript DumpSignalSlots.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DumpSignalSlots extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override public void run() throws Exception {
        long vt = Long.parseLong(System.getenv("VT").trim(), 16);
        int from = Integer.parseInt(System.getenv().getOrDefault("FROM", "0").trim());
        int to = Integer.parseInt(System.getenv().getOrDefault("TO", "255").trim());
        PrintWriter out = new PrintWriter(OUT + "signal_slots.txt");
        for (int i = from; i <= to; i++) {
            Address slot = toAddr(vt + i * 4L);
            long fn;
            try { fn = currentProgram.getMemory().getInt(slot) & 0xFFFFFFFFL; }
            catch (Exception e) { out.println(i + "\t<unreadable slot>"); continue; }
            String note = walk(fn, 0);
            out.println(String.format("slot %d (+0x%x)\tfn=%08x\t%s", i, i * 4, fn, note));
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "signal_slots.txt");
    }

    // Returns a one-line summary of what the override does; follows one level of adjustor thunk.
    private String walk(long fn, int depth) {
        Function f = getFunctionAt(toAddr(fn));
        if (f == null) f = getFunctionContaining(toAddr(fn));
        if (f == null) return "<no function>";
        StringBuilder sb = new StringBuilder();
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        int n = 0;
        String getter = null, lea = null, tail = null;
        while (it.hasNext() && n < 8) {
            Instruction in = it.next();
            String s = in.toString();
            if (n == 1 && depth == 0 && getter == null && lea == null && s.startsWith("JMP 0x")
                    && sb.toString().startsWith("SUB ECX,dword ptr [ECX + -0x4]")) {
                long t = Long.parseLong(s.substring(6), 16);
                if (t != fn) return "thunk->" + Long.toHexString(t) + " :: " + walk(t, depth + 1);
            }
            if (s.startsWith("CALL 0x") && getter == null) getter = s.substring(7);
            if (s.startsWith("LEA ECX,[EAX + ")) lea = s.substring(15, s.length() - 1);
            if (s.startsWith("JMP 0x")) tail = s.substring(6);
            sb.append(s).append("; ");
            n++;
        }
        if (getter != null && lea != null && tail != null)
            return "signal +" + lea + " (getter " + getter + ", emit " + tail + ")";
        return "size=" + f.getBody().getNumAddresses() + " :: " + sb;
    }
}
