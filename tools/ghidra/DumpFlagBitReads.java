// Enumerate every BIT of a flags word the client actually reads, and where.
//
// Written for Gitea #632: the spell record's flags word (client `SpellTemplate_t + 0xC8`, record word 13) has two
// named bits out of 32, and "which of the other 30 does the client read at all?" had no way to be asked. A bit
// nothing reads is not a decode gap; a bit read in four places is a lead with four contexts attached.
//
// ⚠️ **A bit test has two spellings and a census that knows one of them lies.** The client writes both:
//
//     MOVZX EAX, byte ptr [ESI + 0xCA] ; AND AL,1          <- byte spelling, bit 16
//     MOV   EAX, dword ptr [ESI + 0xC8] ; SHR EAX,0x10 ; AND EAX,1   <- shift-and-mask, the SAME bit
//
// A scan for the byte form finds 14 sites for this word; the shift form is how most of it is read. So this walks
// BOTH: a dword load at OFFSET followed by `SHR`/`SAR` + a 1-mask, and a byte load at OFFSET+k followed by a
// power-of-two mask (bit = k*8 + log2(mask)).
//
//   OFFSET=c8 WINDOW=12 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpFlagBitReads.java
//
// ⚠️ It cannot know the base register holds the struct you mean - `+0xC8` is a common offset. Read the reported
// ADDRESSES, not just the counts: a coherent cluster in one subsystem's address range is the signal, and a lone
// hit in an unrelated one is a different struct wearing the same displacement.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

public class DumpFlagBitReads extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        long offset = Long.parseLong(System.getenv().getOrDefault("OFFSET", "c8").trim(), 16);
        int window = Integer.parseInt(System.getenv().getOrDefault("WINDOW", "12").trim());

        // bit -> the sites that read it.
        Map<Integer, List<String>> byBit = new TreeMap<>();
        // Loads we found but could not tie to a bit - reported rather than dropped, because a whole-word read is a
        // different (and interesting) shape from a bit test.
        List<String> wholeWord = new ArrayList<>();

        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        while (it.hasNext() && !monitor.isCancelled()) {
            Function f = it.next();
            List<Instruction> body = new ArrayList<>();
            for (Instruction in : currentProgram.getListing().getInstructions(f.getBody(), true))
                body.add(in);

            for (int i = 0; i < body.size(); i++) {
                Instruction load = body.get(i);
                int k = displacementDelta(load, offset);
                if (k < 0 || k > 3)
                    continue;

                Register dest = destRegister(load);
                if (dest == null)
                    continue;

                Integer bit = null;
                int shift = k * 8;
                for (int j = i + 1; j < body.size() && j <= i + window && bit == null; j++) {
                    Instruction in = body.get(j);
                    if (!touches(in, dest))
                        break; // the value moved on; a later mask is about something else

                    String m = in.getMnemonicString();
                    Scalar imm = lastScalar(in);
                    if (imm == null)
                        continue;

                    if (m.equals("SHR") || m.equals("SAR")) {
                        shift += (int) imm.getUnsignedValue();
                        continue;
                    }

                    if (m.equals("AND") || m.equals("TEST")) {
                        long mask = imm.getUnsignedValue();
                        if (mask != 0 && (mask & (mask - 1)) == 0)
                            bit = shift + Long.numberOfTrailingZeros(mask);
                    }
                }

                String site = load.getAddress() + "  " + f.getName() + "@" + f.getEntryPoint() + "   " + load;
                if (bit == null)
                    wholeWord.add(site);
                else
                    byBit.computeIfAbsent(bit, b -> new ArrayList<>()).add(site);
            }
        }

        PrintWriter out = new PrintWriter(OUT + "flag_bit_reads.txt");
        out.println("bits of the word at +0x" + Long.toHexString(offset) + " that the client READS, by bit");
        out.println("(both spellings: dword + SHR/AND, and byte at +k with a power-of-two mask)");
        out.println();
        for (Map.Entry<Integer, List<String>> e : byBit.entrySet()) {
            out.println("---- bit " + e.getKey() + "  (0x" + Long.toHexString(1L << e.getKey()) + ")   x" + e.getValue().size());
            for (String s : e.getValue())
                out.println("        " + s);
            out.println();
        }

        out.println("---- loads at this offset with NO bit mask found within " + window + " instructions (x" + wholeWord.size() + ")");
        for (String s : wholeWord)
            out.println("        " + s);

        out.close();
        println("wrote " + OUT + "flag_bit_reads.txt (" + byBit.size() + " distinct bits, " + wholeWord.size() + " unmasked loads)");
    }

    // k when `in` addresses [reg + offset + k] for 0 <= k <= 3, else -1.
    private static int displacementDelta(Instruction in, long offset) {
        for (int op = 0; op < in.getNumOperands(); op++) {
            Register base = null;
            Long disp = null;
            for (Object o : in.getOpObjects(op)) {
                if (o instanceof Register r && base == null)
                    base = r;
                else if (o instanceof Scalar s)
                    disp = s.getUnsignedValue();
            }

            if (base != null && disp != null && disp >= offset && disp <= offset + 3)
                return (int) (disp - offset);
        }

        return -1;
    }

    private static Register destRegister(Instruction in) {
        String m = in.getMnemonicString();
        if (!m.equals("MOV") && !m.equals("MOVZX") && !m.equals("MOVSX"))
            return null;

        Object[] first = in.getOpObjects(0);

        return first.length == 1 && first[0] instanceof Register r ? r : null;
    }

    private static boolean touches(Instruction in, Register reg) {
        for (int op = 0; op < in.getNumOperands(); op++)
            for (Object o : in.getOpObjects(op))
                if (o instanceof Register r && (r.equals(reg) || r.getBaseRegister().equals(reg.getBaseRegister())))
                    return true;

        return false;
    }

    private static Scalar lastScalar(Instruction in) {
        Scalar found = null;
        for (int op = 0; op < in.getNumOperands(); op++)
            for (Object o : in.getOpObjects(op))
                if (o instanceof Scalar s)
                    found = s;

        return found;
    }
}
