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
//   FN=0149e450 /opt/ghidra/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript DumpTooltipTokens.java
//
// FN defaults to the tooltip builder; hex, no 0x. Also worth running over the shared value formatters the builder
// CALLS (FUN_0149aa30, the damage/heal range), which carry tokens of their own. Output feeds
// docs/re/gameaction-tooltip-tokens.tsv.
//
// ⚠️ It anchors on the GETTER CALL, not on the `PUSH 0xaa`. Two branches can share one category push and then load
// different tokens (`IncreasesHitPercent` / `DecreasesHitPercent` at 0x014a01aa), so a per-push window reports the
// first of the pair and silently drops the second.
//
// ⚠️⚠️ And it emits EVERY token pointer in the window, not the nearest one. A branch that picks between two tokens
// with a conditional loads BOTH before the getter runs:
//
//     JLE  <decrease arm>
//     MOV  EDX,0x208d480          ; "UpgradeCombo_IncreaseDamage"    <- fall-through arm
//     ...
//     MOV  EDX,0x208d49c          ; "UpgradeCombo_DecreaseDamage"    <- the arm the backward scan reaches first
//     PUSH 0xaa
//     CALL 0x0060f2f0
//
// so a nearest-match scan reports the Decrease half of the pair and drops the Increase half - which is exactly how
// `UpgradeCombo_Increase{Damage,Cooldown}` and `FinishingComboAttack` went missing from the artifact (#782). The
// cost of emitting all of them is a row that names a candidate SET rather than one token, which is what the
// kind -> token file already says a row means.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.util.*;

public class DumpTooltipTokens extends GhidraScript {
    // How far either side of the PUSH to look for the token pointer. The pointer load sits within a handful of
    // instructions of the push in every shipped call site; a wider window starts picking up the NEXT branch's.
    private static final int WINDOW = 12;

    // The by-TOKEN localised-string getters. Both take the token pointer in a register and the category on the
    // stack; which one a branch uses is a calling-convention detail, not a semantic one.
    private static final long[] GETTERS = { 0x0060ec00L, 0x0060f2f0L };

    @Override public void run() throws Exception {
        String fn = System.getenv("FN");
        long entry = Long.parseLong(fn == null ? "0149e450" : fn.trim(), 16);

        Function f = getFunctionContaining(toAddr(entry));
        if (f == null) { println("no fn"); return; }

        List<Instruction> body = new ArrayList<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) body.add(it.next());

        for (int i = 0; i < body.size(); i++) {
            Instruction in = body.get(i);
            if (!in.getMnemonicString().equals("CALL") || !isGetter(in))
                continue;

            // EVERY preceding immediate in the window that resolves to a printable string - see the header: a
            // conditional loads both arms' tokens before the getter runs, so the nearest one is only half the answer.
            List<String> tokens = new ArrayList<>(), addrs = new ArrayList<>();
            for (int j = i - 1; j >= 0 && j > i - WINDOW; j--) {
                Scalar s = scalarOf(body.get(j));
                if (s == null || s.getUnsignedValue() < 0x400000) continue;
                String text = stringAt(toAddr(s.getUnsignedValue()));
                // A `%` means it is a printf FORMAT the branch glues its pieces with, not a token name - the
                // composing branches (53070, 53211) push both into the same window. Formats are recorded beside
                // their branch in docs/rdb/spell-tooltip.md; a token never contains one.
                if (text == null || text.indexOf('%') >= 0 || tokens.contains(text)) continue;
                tokens.add(text);
                addrs.add("0x" + Long.toHexString(s.getUnsignedValue()));
            }

            if (tokens.isEmpty())
                println(in.getAddress() + "\t-\t-");
            else
                for (int k = 0; k < tokens.size(); k++)
                    println(in.getAddress() + "\t" + addrs.get(k) + "\t" + tokens.get(k));
        }
    }

    private boolean isGetter(Instruction call) {
        Scalar s = scalarOf(call);
        if (s == null) {
            // A CALL to a resolved function shows as a reference rather than a scalar operand.
            for (var ref : call.getReferencesFrom())
                for (long g : GETTERS)
                    if (ref.getToAddress().getOffset() == g) return true;
            return false;
        }
        for (long g : GETTERS)
            if (s.getUnsignedValue() == g) return true;
        return false;
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
