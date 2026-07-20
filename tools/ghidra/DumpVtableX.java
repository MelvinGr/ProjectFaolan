import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Symbol;

public class DumpVtableX extends GhidraScript {
    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }
    public void run() throws Exception {
        String name = System.getenv("VT");
        Address vt = vtableAddr(name);
        println("vtable " + name + " -> " + vt);
        if (vt == null) return;
        for (int i = 0; i < 32; i++) {
            long p = getInt(vt.add(i * 4L)) & 0xffffffffL;
            if (p == 0 || p < 0x400000 || p > 0x2000000) break;
            Function f = getFunctionContaining(toAddr(p));
            println("  slot " + i + " -> 0x" + Long.toHexString(p) + "  " + (f != null ? (f.getName()+"@"+f.getEntryPoint()) : "?"));
        }
    }
}
