import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
public class VtAt extends GhidraScript {
    public void run() throws Exception {
        long base = Long.parseLong(System.getenv("VTADDR"), 16);
        for (int i = -2; i < 20; i++) {
            long p = getInt(toAddr(base + i * 4L)) & 0xffffffffL;
            String s;
            if (p >= 0x400000 && p < 0x2000000) {
                Function f = getFunctionContaining(toAddr(p));
                s = (f != null ? f.getName()+"@"+f.getEntryPoint() : "code?");
            } else s = "(non-code)";
            println("slot " + i + " @" + Long.toHexString(base + i*4L) + " -> 0x" + Long.toHexString(p) + " " + s);
        }
    }
}
