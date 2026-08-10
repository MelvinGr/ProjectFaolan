// The TOKEN NAMES the client's item/spell tooltip builder looks its localised sentences up by.
//
// LDB category 170 is keyed two ways: about 30 entries by the GameAction TYPE ID itself, and the rest by the
// ELF-28 hash of a token name. The tooltip builder is where those names are - each call site pushes the category
// (0xAA) and hands the getter a static char*:
//
//     PUSH 0xaa
//     MOV  ESI,0x208d1d4          ; "IncreaseAOERadius"
//     CALL 0x0060ec00             ; GetLocalisedString(category, token)
//
// elf28("IncreaseAOERadius") == 22958195, which is the shipped "Increases the area of effect by %d meters".
//
//   FN=0149e450 CATEGORY=aa /opt/ghidra/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript DumpTooltipTokens.java
//
// FN defaults to the tooltip builder and CATEGORY to 0xAA; both are hex, no 0x. Output feeds
// docs/re/gameaction-tooltip-tokens.tsv.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.util.*;

public class DumpTooltipTokens extends GhidraScript {
    // How far either side of the PUSH to look for the token pointer. The pointer load sits within a handful of
    // instructions of the push in every shipped call site; a wider window starts picking up the NEXT branch's.
    private static final int WINDOW = 12;

    @Override public void run() throws Exception {
        String fn = System.getenv("FN");
        String category = System.getenv("CATEGORY");
        long entry = Long.parseLong(fn == null ? "0149e450" : fn.trim(), 16);
        long wanted = Long.parseLong(category == null ? "aa" : category.trim(), 16);

        Function f = getFunctionContaining(toAddr(entry));
        if (f == null) { println("no fn"); return; }

        List<Instruction> body = new ArrayList<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) body.add(it.next());

        for (int i = 0; i < body.size(); i++) {
            Instruction in = body.get(i);
            if (!in.getMnemonicString().equals("PUSH") || scalarOf(in) == null || scalarOf(in).getUnsignedValue() != wanted)
                continue;

            String token = null;
            String at = null;
            // Forwards first: the pointer load normally follows the category push.
            for (int step = 1; step <= WINDOW && token == null; step++) {
                for (int j : new int[] { i + step, i - step }) {
                    if (j < 0 || j >= body.size()) continue;
                    Scalar s = scalarOf(body.get(j));
                    if (s == null || s.getUnsignedValue() < 0x400000) continue;
                    String text = stringAt(toAddr(s.getUnsignedValue()));
                    if (text == null) continue;
                    token = text;
                    at = "0x" + Long.toHexString(s.getUnsignedValue());
                    break;
                }
            }

            println(in.getAddress() + "\t" + (at == null ? "-" : at) + "\t" + (token == null ? "-" : token));
        }
    }

    private Scalar scalarOf(Instruction in) {
        for (int op = 0; op < in.getNumOperands(); op++) {
            Object[] objects = in.getOpObjects(op);
            for (Object o : objects)
                if (o instanceof Scalar s) return s;
        }
        return null;
    }

    // A printable ASCII run of 3+ chars, NUL-terminated - the shape every token in this table has. Returns null for
    // anything else, which is what filters the numeric immediates out of the same window.
    private String stringAt(Address a) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 64; i++) {
            int b;
            try { b = getByte(a.add(i)) & 0xff; } catch (Exception e) { return null; }
            if (b == 0) break;
            if (b < 32 || b > 126) return null;
            sb.append((char) b);
        }
        return sb.length() >= 3 ? sb.toString() : null;
    }
}
