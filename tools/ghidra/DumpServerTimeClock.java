// Recovers the client's SERVER-TIME clock: the global `ServerTime_c` singleton, the one signal that ever writes it,
// and the emitter that fires that signal - which is what identifies the wire message carrying it (#480).
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpServerTimeClock.java
//
// Writes $FAOLAN_GHIDRA_OUT/servertime_clock.txt (falling back to the system temp dir). It writes a FILE rather than
// printing because analyzeHeadless prefixes only the FIRST line of a multi-line println with "INFO <script>>", so the
// usual `grep <script>` on its output drops every line of decompiled C but the first.
//
// What it establishes, and why each step is needed:
//
//   1. `ServerTime_c::vftable` is an RTTI-named symbol, so the singleton's ADDRESS is whatever the module ctor stores
//      it into. That ctor is the only function that writes the object's fields by absolute address (every other
//      access is `this`-relative and therefore invisible to a data xref), which is what makes it findable at all.
//   2. The u64 at `+0x48` is the clock. `MagicBuffIIR_c::Activate` (FUN_007a1580) reads it directly - not through an
//      accessor that adds elapsed time - and computes `(payloadStamp - thatU64) / 10`, overwriting the buff's
//      Duration when the result is positive and shorter. So the field's UNIT is milliseconds (Duration is
//      centiseconds) and its value is only ever as fresh as the last update.
//   3. The ctor connects a slot to a global signal, and that slot (FUN_007a0ea0) is the ONLY writer of `+0x48`:
//          this[0x48] = (__int64)incoming / 1000;   this[0x40] = timeGetTime();   this[0x50] = this[0x48] * 0.001;
//      i.e. the wire value arrives in MICROSECONDS and is stored as milliseconds.
//   4. The signal's emitter is `GamecodeClient_t` vtable slot 131 -> DispatchPong (GameCharInterface 0x2082), whose
//      body we already send as [u32 echoedCounter][u32 uptimeHi][u32 uptimeLo] - a 64-bit microsecond uptime counter.
//      The chain closes: the client's clock is our pong's uptime, and a buff's expiry stamp rides on it.
//
// A signal's emit does NOT show up as a caller of the slot, so step 4 cannot be reached by xrefs from the slot. It is
// reached from the SIGNAL object: every connect/disconnect/emit loads its address as an immediate, so scanning
// instructions for that immediate enumerates all of them, and the emitter is the one that also takes arguments.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpServerTimeClock extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    DecompInterface di;
    PrintWriter out;

    void decompile(String label, Address entry) throws Exception {
        Function f = getFunctionContaining(entry);
        out.println("---------- " + label + " @" + entry + " " + (f != null ? f.getName() : "(no function)"));
        if (f == null) return;
        DecompileResults res = di.decompileFunction(f, 120, monitor);
        DecompiledFunction df = res == null ? null : res.getDecompiledFunction();
        out.println(df != null ? df.getC() : "(decompile failed)");
    }

    // Every instruction anywhere in the image with `value` as an immediate operand, with its containing function.
    List<Address> immediateUses(long value) {
        List<Address> hits = new ArrayList<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction in = it.next();
            for (int op = 0; op < in.getNumOperands(); op++)
                for (Object o : in.getOpObjects(op))
                    if (o instanceof Scalar && (((Scalar) o).getUnsignedValue() & 0xFFFFFFFFL) == (value & 0xFFFFFFFFL)) {
                        hits.add(in.getAddress());
                        op = in.getNumOperands();
                        break;
                    }
        }
        return hits;
    }

    Address symbolNamed(String fullName) {
        SymbolIterator it = currentProgram.getSymbolTable().getAllSymbols(true);
        while (it.hasNext()) {
            Symbol s = it.next();
            if (fullName.equals(s.getName(true))) return s.getAddress();
        }
        return null;
    }

    @Override
    public void run() throws Exception {
        di = new DecompInterface();
        // Without explicit options the decompiler silently returns null for every function, which reads as "this
        // function is empty" rather than "the tool is misconfigured".
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        out = new PrintWriter(OUT + "servertime_clock.txt");

        Address vft = symbolNamed("ServerTime_c::vftable");
        out.println("=== ServerTime_c::vftable = " + vft);
        if (vft == null) {
            out.println("!! no such symbol - has RTTI analysis run on this program?");
            out.close();
            return;
        }

        // The module ctor is the one function that stores the vftable pointer into the singleton.
        out.println("\n=== writers of the vftable pointer (the module ctor is among them) ===");
        Address ctor = null;
        for (Address a : immediateUses(vft.getOffset())) {
            Function f = getFunctionContaining(a);
            out.println("  " + a + "  [" + (f != null ? f.getName() + "@" + f.getEntryPoint() : "?") + "]");
            if (f != null && ctor == null) ctor = f.getEntryPoint();
        }
        if (ctor != null) decompile("ServerTime_c module ctor", ctor);

        // The signal the ctor connects to. Its address is loaded as an immediate by every connect, disconnect AND
        // emit; the emit is the one that forwards arguments (see the header).
        out.println("\n=== every function loading the ctor's signal-object immediates ===");
        out.println("(cross-check by hand: a connect passes a slot fn, the emit forwards (uint, __int64))");
        if (ctor != null) {
            Function f = getFunctionContaining(ctor);
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            Set<Long> seen = new LinkedHashSet<>();
            while (it.hasNext()) {
                Instruction in = it.next();
                for (int op = 0; op < in.getNumOperands(); op++)
                    for (Object o : in.getOpObjects(op))
                        if (o instanceof Scalar) {
                            long v = ((Scalar) o).getUnsignedValue() & 0xFFFFFFFFL;
                            // Only plausible .data addresses, and not the object's own fields.
                            if (v > 0x02000000L && v < 0x03000000L && Math.abs(v - vft.getOffset()) > 0x400) seen.add(v);
                        }
            }
            for (long sig : seen) {
                out.println("\n  -- signal candidate " + Long.toHexString(sig));
                for (Address a : immediateUses(sig)) {
                    Function g = getFunctionContaining(a);
                    out.println("     " + a + "  [" + (g != null ? g.getName() + "@" + g.getEntryPoint() : "?") + "]");
                }
            }
        }

        out.println("\n=== the consumer that fixes the UNIT: MagicBuffIIR_c::Activate ===");
        decompile("MagicBuffIIR_c::Activate", toAddr(0x007a1580L));

        out.close();
        println("wrote " + OUT + "servertime_clock.txt");
    }
}
