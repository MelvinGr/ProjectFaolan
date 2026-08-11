// #271: dump every GameActionData TYPE's descriptor key list. From FUN_008c3910: type ids run 0xCF09..0xD026,
// descriptor = DAT_025077c8 + (typeId-0xCF09)*0x41, key list at descriptor+0xc (u32 entries, low byte = index into
// the stat-key table DAT_025073c0, terminated by an entry == 0xFF). Resolves each key + flags the SetSpellRecastTime
// type (keys 0x2f9 recast + 0x463). Output: gameaction_types.txt.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import java.io.PrintWriter;
import java.util.*;

public class DumpGameActionTypes extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    long TABLE = 0x025077c8L;   // descriptor table base
    long STATKEYS = 0x025073c0L; // byte-index -> real stat key
    int STRIDE = 0x104;  // FUN_008c3910: IMUL EAX,EAX,0x104. NOT 0x41 - and since 0x104 == 4*0x41 a wrong
                         // 0x41 stride still divides evenly, so it silently yields 4x-inflated ids (crashed the client).
    // ⭐ The window is the CLIENT'S OWN registered range, not a guess, and #485 asked for it to be widened on the
    // premise that it "silently truncates". It does not. `FUN_008c3910` states the bound TWICE in one function:
    //
    //     if (0x11d < typeId - 0xcf09U)  assert("i_nGameAction >= e_Type_GC_GameAction && "
    //                                           "i_nGameAction < e_Type_GC_GameAction_End", "SpellData.cpp", 0x629)
    //     if (typeId < 0xcf09 || 0xd026 < typeId)  typeId = 0xcf09;   // out of range -> clamped to the first entry
    //
    // 0xCF09 + 0x11D == 0xD026, so the assert bound and the clamp agree: 286 types and nothing past them. The
    // descriptor table is indexed `(typeId - 0xCF09) * STRIDE`, so an id outside the window has no descriptor to
    // read - widening this would read past the table, not find more types.
    //
    // ⚠️ The "types past 0xD026" #485 lists (Fatality 0xD331, SummonMount 0xD2D5, ...) are the OTHER numbering:
    // `docs/re/gameaction_types.json` carries both a `typeId` and an `enumId` and they diverge (Hit is typeId 53005
    // / enumId 53002). All eight are inside this window at their enum ids - Fatality is 0xD013, SummonMount 0xCFFC.
    int FIRST = 0xCF09, LAST = 0xD026;

    int u32(long a) throws Exception { return getInt(toAddr(a)); }

    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "gameaction_types.txt");
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
        println("WROTE " + OUT + "gameaction_types.txt");
    }
}
