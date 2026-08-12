// For each GameTweak<T> value global (ADDRS, comma-separated hex), find the ctor call that INITIALISES it and
// print the NAME STRING it was constructed from.
//
// The client keeps tuning-sheet values in `GameTweak<T>` objects built once at first use:
//
//     MOV EDX,<char* name> ; MOV EDI,<tweak object> ; CALL <GameTweak ctor>
//
// and the VALUE sits at `object + 8`, which is what the reading code loads. So a global that reads as an opaque
// `DAT_xxxxxxxx` is a NAMED tuning value, and the name is two instructions above its constructor - but the
// constructor is nowhere near the read site, so a local window never finds it.
//
//   ADDRS=3b9f870,3b9f880 CTOR=0053ab80 /opt/ghidra/support/analyzeHeadless <proj> aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpTweakInit.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.scalar.Scalar;
import java.util.*;

public class DumpTweakInit extends GhidraScript {
    // How far back from the ctor call to look for the two immediates it takes (the name and the object).
    private static final int WINDOW = 8;

    @Override public void run() throws Exception {
        long ctor = Long.parseLong(System.getenv().getOrDefault("CTOR", "0053ab80").trim(), 16);
        Set<Long> wanted = new HashSet<>();
        for (String hex : System.getenv().getOrDefault("ADDRS", "").split(","))
            if (!hex.trim().isEmpty()) wanted.add(Long.parseLong(hex.trim(), 16));

        // ⚠️ STREAMED through a small ring buffer, not collected. Buffering the program's ~2.5M instructions
        // into a list is an immediate `OutOfMemoryError: Java heap space` under headless's default heap - and the
        // only thing this needs is the handful of instructions before each call.
        Instruction[] ring = new Instruction[WINDOW];
        int n = 0;
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction in = it.next();
            ring[n++ % WINDOW] = in;
            if (!in.getMnemonicString().equals("CALL") || !targets(in, ctor)) continue;

            Long object = null, name = null;
            for (int back = 2; back <= WINDOW && back <= n; back++) {
                Instruction prev = ring[(n - back) % WINDOW];
                if (prev == null) continue;
                Scalar s = scalarOf(prev);
                if (s == null) continue;
                String reg = prev.getNumOperands() > 0 ? prev.getDefaultOperandRepresentation(0) : "";
                if (reg.equals("EDI") && object == null) object = s.getUnsignedValue();
                else if (reg.equals("EDX") && name == null) name = s.getUnsignedValue();
            }

            // The VALUE the reading code loads is at object + 8.
            if (object == null || name == null) continue;
            long value = object + 8;
            if (!wanted.isEmpty() && !wanted.contains(value) && !wanted.contains(object)) continue;

            println(in.getAddress() + "\tobject 0x" + Long.toHexString(object) + "\tvalue 0x" + Long.toHexString(value)
                    + "\t\"" + stringAt(toAddr(name)) + "\"");
        }
    }

    private boolean targets(Instruction call, long fn) {
        Scalar s = scalarOf(call);
        if (s != null && s.getUnsignedValue() == fn) return true;
        for (var ref : call.getReferencesFrom())
            if (ref.getToAddress().getOffset() == fn) return true;
        return false;
    }

    private Scalar scalarOf(Instruction in) {
        for (int op = 0; op < in.getNumOperands(); op++)
            for (Object o : in.getOpObjects(op))
                if (o instanceof Scalar s) return s;
        return null;
    }

    private String stringAt(Address a) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 96; i++) {
            int b;
            try { b = getByte(a.add(i)) & 0xff; } catch (Exception e) { return sb.toString(); }
            if (b == 0) break;
            if (b < 32 || b > 126) return sb.toString();
            sb.append((char) b);
        }
        return sb.toString();
    }
}
