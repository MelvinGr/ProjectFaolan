// #271: dump every GameActionData TYPE's descriptor key list. From FUN_008c3910: type ids run 0xCF09..0xD026,
// descriptor = DAT_025077c8 + (typeId-0xCF09)*0x41, key list at descriptor+0xc (u32 entries, low byte = index into
// the stat-key table DAT_025073c0, terminated by an entry == 0xFF). Resolves each key + flags the SetSpellRecastTime
// type (keys 0x2f9 recast + 0x463). Output: gameaction_types.txt.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import java.io.PrintWriter;
import java.util.*;

public class DumpGameActionTypes extends GhidraScript {
    long TABLE = 0x025077c8L;   // descriptor table base
    long STATKEYS = 0x025073c0L; // byte-index -> real stat key
    int STRIDE = 0x41;
    int FIRST = 0xCF09, LAST = 0xD026;

    int u32(long a) throws Exception { return getInt(toAddr(a)); }

    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/gameaction_types.txt");
        // stat-key table: byte index -> real key (0..255)
        int[] keyTable = new int[256];
        for (int i = 0; i < 256; i++) keyTable[i] = u32(STATKEYS + i * 4L);

        for (int t = FIRST; t <= LAST; t++) {
            long desc = TABLE + (long)(t - FIRST) * STRIDE;
            long keyListPtr = desc + 0xc;
            List<Integer> keys = new ArrayList<>();
            boolean bad = false;
            for (int k = 0; k < 40; k++) {
                int entry;
                try { entry = u32(keyListPtr + k * 4L); } catch (Exception e) { bad = true; break; }
                if ((entry & 0xFF) == 0xFF || entry == 0xFF) break;
                int idx = entry & 0xFF;
                keys.add(keyTable[idx]);
            }
            StringBuilder sb = new StringBuilder();
            for (int key : keys) sb.append(String.format("0x%x ", key));
            String flag = "";
            if (keys.contains(0x2f9)) flag = keys.contains(0x463) ? "   <== SetSpellRecastTime? (0x2f9+0x463)" : "   <== has 0x2f9 (recast)";
            out.println(String.format("type=0x%04x (idx %3d) keys=[ %s]%s", t, t - FIRST, sb.toString(), flag));
        }
        out.flush(); out.close();
        println("WROTE /home/melvin/Development/Ghidra/Aoc/gameaction_types.txt");
    }
}
