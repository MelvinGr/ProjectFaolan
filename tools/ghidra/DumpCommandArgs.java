// The ARGUMENT GRAMMAR behind every verb in the client's chat-command registry.
//
// DumpCommandRegistry.java recovers verb -> class -> vftable -> permission mask, and stops there. That is what left
// `/teleportself` refusing the client's own map UI (#913): we had the right verb under the right mask and parsed its
// argument as a map name, because nothing recorded that retail's first two parameters are `posx posz`.
//
// Every Fanatic command class declares its parameters in one vtable slot, as a run of
//     append(openLiteral); append(closeLiteral); append(helpText); append(paramName)
// calls - four string appends per parameter, after a two-append preamble that wraps the command's own description.
// So the ordered sequence of `append(str, len)` calls in that slot IS the grammar, and the usage line rebuilds as
// the concatenation of open + name + close. That reconstruction SELF-VALIDATES: a correctly parsed class comes out
// with balanced brackets, so `ok` in the output is a real check and not a hope.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpCommandArgs.java
//
// FN=<hex> selects the registry function (default 0064e2d0). APPEND=<hex> the string-append helper (default 0040ec10).
// Output: $FAOLAN_GHIDRA_OUT/command_args.tsv
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Symbol;
import java.io.PrintWriter;
import java.util.*;

public class DumpCommandArgs extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final int MAX_SLOTS = 64;

    long appendFn;

    long debugVftable;

    PrintWriter raw;

    @Override
    public void run() throws Exception {
        long entry = Long.parseLong(System.getenv().getOrDefault("FN", "0064e2d0").trim(), 16);
        appendFn = Long.parseLong(System.getenv().getOrDefault("APPEND", "0040ec10").trim(), 16);
        debugVftable = Long.parseLong(System.getenv().getOrDefault("DEBUGVFT", "0").trim(), 16);
        if (System.getenv("RAW") != null)
            raw = new PrintWriter(OUT + "command_args_raw.tsv");

        Function f = getFunctionContaining(toAddr(entry));
        if (f == null) {
            println("no function at " + Long.toHexString(entry));
            return;
        }

        // Pass 1: the registry, verb -> vftable. Same walk as DumpCommandRegistry, kept here rather than shared so
        // this script runs standalone; the two agree because they read the same instruction stream.
        List<String[]> rows = new ArrayList<>(); // verb, mask, class, vftable
        Long f8 = null;
        String cls = null, vft = null;
        long mask = 0;
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) {
            Instruction insn = it.next();
            if (insn.getMnemonicString().equals("PUSH")) {
                Long pushed = immediateOf(insn, 0);
                if (pushed != null && cls != null) {
                    String verb = describe(toAddr(pushed));
                    if (verb.startsWith("/")) {
                        rows.add(new String[] { verb, "0x" + Long.toHexString(mask), cls, vft });
                        cls = null;
                    }
                }
                continue;
            }

            if (!insn.getMnemonicString().equals("MOV"))
                continue;

            String dst = insn.getDefaultOperandRepresentation(0);
            Long imm = immediateOf(insn, 1);
            if (dst.contains("[EAX + 0x8]"))
                f8 = imm == null ? 0L : imm;
            else if (dst.equals("dword ptr [EAX]") && imm != null && f8 != null) {
                cls = nameAt(toAddr(imm)).replace("::vftable", "");
                vft = "0x" + Long.toHexString(imm);
                mask = f8;
                f8 = null;
            } else if (dst.equals("EAX") && imm != null && cls != null) {
                String verb = describe(toAddr(imm));
                if (!verb.startsWith("/"))
                    continue;
                rows.add(new String[] { verb, "0x" + Long.toHexString(mask), cls, vft });
                cls = null;
            }
        }

        // Pass 2: per class, the appends in whichever slot declares the parameters. Cached, because the teleport
        // family alone is four verbs on one class and re-walking a vtable per verb would be pure waste.
        Map<String, String[]> byVftable = new HashMap<>(); // vftable -> {short, long, usage, params, ok}
        PrintWriter out = new PrintWriter(OUT + "command_args.tsv");
        out.println("verb\tmask\tclass\tvftable\tok\tusage\tshortdesc\tlongdesc\tparams");
        int good = 0;
        for (String[] r : rows) {
            String[] g = byVftable.get(r[3]);
            if (g == null) {
                g = grammarOf(Long.parseLong(r[3].substring(2), 16));
                byVftable.put(r[3], g);
            }
            if (g[4].equals("ok"))
                good++;
            out.println(r[0] + "\t" + r[1] + "\t" + r[2] + "\t" + r[3] + "\t" + g[4] + "\t" + g[2] + "\t" + g[0] + "\t" + g[1] + "\t" + g[3]);
        }

        out.flush();
        out.close();
        if (raw != null) {
            raw.flush();
            raw.close();
        }
        println("WROTE " + OUT + "command_args.tsv (" + rows.size() + " verbs, " + good + " with a parsed grammar)");
    }

    // {shortDesc, longDesc, usage, params, ok} for one class's vtable.
    private String[] grammarOf(long vftable) {
        List<String> singles = new ArrayList<>(); // slots that append exactly one string = the descriptions
        List<String> best = new ArrayList<>();
        for (int i = 0; i < MAX_SLOTS; i++) {
            Address slot;
            long ptr;
            try {
                slot = toAddr(vftable + 4L * i);
                ptr = getInt(slot) & 0xffffffffL;
            } catch (Exception e) {
                break;
            }
            Function fn = getFunctionAt(toAddr(ptr));
            if (fn == null)
                break; // past the end of the vtable

            List<String> appends = appendsIn(fn);
            if (raw != null && appends.size() > 2)
                for (int k = 0; k < appends.size(); k++)
                    raw.println(Long.toHexString(vftable) + "\t" + i + "\t" + k + "\t" + appends.get(k));
            if (debugVftable != 0 && vftable == debugVftable)
                println("DEBUG slot[" + i + "] " + toAddr(ptr) + " appends=" + appends.size() + " " + appends);
            if (appends.size() == 1)
                singles.add(appends.get(0));
            else if (appends.size() > best.size())
                best = appends;
        }

        String shortD = singles.size() > 0 ? singles.get(0) : "";
        String longD = singles.size() > 1 ? singles.get(1) : "";

        if (best.isEmpty())
            return new String[] { shortD, longD, "", "", "no-appends" };

        // Two preamble appends wrap the command's own description, then FOUR per parameter:
        //     openLiteral, closeLiteral, helpText, paramName
        // A handful of classes end ragged - the last parameter's name arrives through a different string helper, so
        // a 4-tuple is left short. Emit every COMPLETE tuple and say how many appends were left over, rather than
        // discarding a class with ten good parameters because its eleventh is half-declared.
        StringBuilder usage = new StringBuilder();
        StringBuilder params = new StringBuilder();
        int i = 2;
        int n = 0;
        for (; i + 3 < best.size(); i += 4) {
            String open = best.get(i), close = best.get(i + 1), help = best.get(i + 2), name = best.get(i + 3);
            usage.append(open).append(name).append(close);
            if (params.length() > 0)
                params.append(" | ");
            params.append(name).append(": ").append(help.trim());
            n++;
        }

        int leftover = best.size() - i;
        String u = usage.toString().trim();
        String status = n == 0 ? "noargs" : leftover > 0 ? "partial:" + leftover : "ok";
        if (n > 0 && !balanced(u))
            status = status + "+unbalanced";

        return new String[] { shortD, longD, u, params.toString(), status };
    }

    // The ordered string literals a function hands to the append helper.
    private List<String> appendsIn(Function fn) {
        List<String> found = new ArrayList<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(fn.getBody(), true);
        List<Long> recentPushes = new ArrayList<>();
        while (it.hasNext()) {
            Instruction insn = it.next();
            String m = insn.getMnemonicString();
            if (m.equals("PUSH")) {
                Long v = immediateOf(insn, 0);
                recentPushes.add(v == null ? -1L : v);
                if (recentPushes.size() > 8)
                    recentPushes.remove(0);
                continue;
            }

            if (m.equals("CALL")) {
                // A call's target operand is an Address, not a Scalar - immediateOf() reads it as absent and every
                // append silently goes unseen, which reads as "this class declares no parameters".
                Address[] flows = insn.getFlows();
                Long target = flows.length > 0 ? flows[0].getOffset() : null;
                // append(str, len): the string pointer is the LAST push, the length the one before it.
                if (target != null && target == appendFn && recentPushes.size() >= 2) {
                    long strPtr = recentPushes.get(recentPushes.size() - 1);
                    if (strPtr > 0)
                        found.add(describe(toAddr(strPtr)));
                }

                // Only a CALL consumes the pending arguments. Clearing on every other instruction loses every append
                // whose thiscall loads ECX *after* pushing - which is the majority spelling, and left every class
                // reporting the two-append preamble and nothing else.
                recentPushes.clear();
            }
        }

        return found;
    }

    private static boolean balanced(String s) {
        int depth = 0;
        for (char c : s.toCharArray()) {
            if (c == '[')
                depth++;
            else if (c == ']' && --depth < 0)
                return false;
        }
        return depth == 0;
    }

    private Long immediateOf(Instruction insn, int op) {
        for (Object o : insn.getOpObjects(op))
            if (o instanceof Scalar)
                return ((Scalar) o).getUnsignedValue();
        return null;
    }

    private String nameAt(Address a) {
        Symbol s = getSymbolAt(a);
        return s != null ? s.getName(true) : "?@" + a;
    }

    // Most of these strings are bare DAT_ symbols rather than defined string data, so the printable-ASCII fallback
    // is the common path, not the exception.
    private String describe(Address a) {
        Data d = getDataAt(a);
        if (d != null) {
            StringDataInstance sdi = StringDataInstance.getStringDataInstance(d);
            if (sdi != null && sdi.getStringLength() > 0)
                return flatten(sdi.getStringValue());
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 128; i++) {
            int b;
            try {
                b = getByte(a.add(i)) & 0xff;
            } catch (Exception e) {
                break;
            }
            if (b == 0)
                break;
            if (b < 32 || b >= 127)
                return "";
            sb.append((char) b);
        }

        return sb.toString();
    }

    // Several retail help strings are genuinely MULTI-LINE (`/setscriptgroup` documents its parameter format over
    // four lines). Emitted as-is they split one TSV row into five, so every consumer sees a truncated catalog.
    private static String flatten(String s) {
        return s.replaceAll("[\\x00-\\x1f]+", " ").trim();
    }
}
