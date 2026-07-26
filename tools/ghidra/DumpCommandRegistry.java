// The client's chat-command REGISTRY: one function constructs every command as a 12-byte record
//     [0] <Command>::vftable   [4] u32   [8] u32 permission mask
// and pushes it into the interpreter's list. This walks that function's instructions and emits one line per record -
// the class name behind the vtable pointer, both u32s, and whatever symbol/string the record's registration call was
// handed - so the whole catalog and its gating come out as data rather than 3,400 lines of decompiled C.
//
// FN=<entry hex> selects the registry function (default 0064e2d0, the one that installs the Fanatic/GM set).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Symbol;
import java.io.PrintWriter;
import java.util.*;

public class DumpCommandRegistry extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        long entry = Long.parseLong(System.getenv().getOrDefault("FN", "0064e2d0").trim(), 16);
        Function f = getFunctionContaining(toAddr(entry));
        if (f == null) {
            println("no function at " + Long.toHexString(entry));
            return;
        }

        PrintWriter out = new PrintWriter(OUT + "command_registry.txt");
        out.println("verb\tmask\tclass\tvftable\tat");

        Long f4 = null, f8 = null;
        String cls = null, at = null;
        long mask = 0;
        int n = 0;
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) {
            Instruction insn = it.next();

            // The verb reaches the registration call either in EAX or on the stack, depending on how the compiler
            // inlined that record's std::string construction - both spellings occur inside this one function, so
            // matching only the register form loses the whole first third of the catalog.
            if (insn.getMnemonicString().equals("PUSH")) {
                Long pushed = immediateOf(insn, 0);
                if (pushed != null && cls != null) {
                    String pushedVerb = describe(toAddr(pushed));
                    if (pushedVerb.startsWith("\"/")) {
                        out.println(pushedVerb.replace("\"", "") + "\t0x" + Long.toHexString(mask) + "\t" + cls + "\t" + at);
                        n++;
                        cls = null;
                    }
                }

                continue;
            }

            if (!insn.getMnemonicString().equals("MOV"))
                continue;

            String dst = insn.getDefaultOperandRepresentation(0);
            Long imm = immediateOf(insn, 1);

            // Order per record: the two mask words, then the vtable pointer, and only THEN the verb string handed to
            // the registration call. So the verb is emitted when it arrives, against the record just seen - reading it
            // as "the last string before the vtable" shifts every verb onto the previous command.
            if (dst.contains("[EAX + 0x4]"))
                f4 = imm == null ? 0L : imm; // EBX is zeroed throughout this function, so a register operand is 0
            else if (dst.contains("[EAX + 0x8]"))
                f8 = imm == null ? 0L : imm;
            else if (dst.equals("dword ptr [EAX]") && imm != null && f8 != null) {
                cls = nameAt(toAddr(imm)).replace("::vftable", "");
                at = "0x" + Long.toHexString(imm) + "\t" + insn.getAddress();
                mask = f8;
                f4 = f8 = null;
            } else if (dst.equals("EAX") && imm != null && cls != null) {
                String verb = describe(toAddr(imm));
                if (!verb.startsWith("\"/"))
                    continue; // not a string - the record's verb has not been reached yet
                out.println(verb.replace("\"", "") + "\t0x" + Long.toHexString(mask) + "\t" + cls + "\t" + at);
                n++;
                cls = null;
            }
        }

        out.flush();
        out.close();
        println("WROTE " + OUT + "command_registry.txt (" + n + " records)");
    }

    private Long immediateOf(Instruction insn, int op) {
        for (Object o : insn.getOpObjects(op))
            if (o instanceof Scalar)
                return ((Scalar) o).getUnsignedValue();
        return null;
    }

    // The symbol at an address, preferring the demangled C++ name Ghidra derives from RTTI.
    private String nameAt(Address a) {
        Symbol s = getSymbolAt(a);
        return s != null ? s.getName(true) : "?@" + a;
    }

    // The verb at an address. Ghidra has only defined SOME of these as string data - the rest are bare DAT_ symbols -
    // so fall back to reading printable ASCII up to the null. Without that fallback a third of the catalog silently
    // loses its verb and reads as an unnamed command.
    private String describe(Address a) {
        Data d = getDataAt(a);
        if (d != null) {
            StringDataInstance sdi = StringDataInstance.getStringDataInstance(d);
            if (sdi != null && sdi.getStringLength() > 0)
                return "\"" + sdi.getStringValue() + "\"";
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 64; i++) {
            int b;
            try {
                b = getByte(a.add(i)) & 0xff;
            } catch (Exception e) {
                break;
            }
            if (b == 0)
                break;
            if (b < 32 || b >= 127)
                return ""; // not text - this operand is some other pointer
            sb.append((char) b);
        }

        return sb.length() > 0 ? "\"" + sb + "\"" : "";
    }
}
