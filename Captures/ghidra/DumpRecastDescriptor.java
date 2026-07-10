// #271: find the SetSpellRecastTime GameActionData type id + its descriptor key-list ORDER (the positional value
// order on the wire). FUN_008c3910: descriptor = DAT_025077c8 + (typeId-0xcf09)*0x41 dwords (0x104 bytes), typeIds
// 0xcf09..0xd026, keyList at descriptor+0xc (u32 indices until 0xFF), realKey = statKeyTable DAT_025073c0[idx&0xFF].
// The recast type is the one whose mapped keyList carries BOTH 0x463 (spell) and 0x2f9 (recast cs).
import ghidra.app.script.GhidraScript;
import java.util.*;

public class DumpRecastDescriptor extends GhidraScript {
    long DESC = 0x025077c8L, STRIDE = 0x104, LO = 0xcf09L, HI = 0xd026L, STATKEY = 0x025073c0L;
    int u32(long a) throws Exception { return getInt(toAddr(a)); }

    // statKeyTable (DAT_025073c0) is runtime-init'd by FUN_00897bd0 (zero in the exe), so index->key is recovered
    // from that function's assignments: index i -> 0x2d2+i for i<=0x4c, then a jump list. We only need the two the
    // recast handler reads: statKeyTable[0x27]=0x2f9 (recast cs), statKeyTable[0x58]=0x463 (spell id).
    static final int IDX_RECAST = 0x27, IDX_SPELL = 0x58;

    public void run() throws Exception {
        // Diagnostic: raw hex of the first few descriptors so the real layout is visible.
        for (long id = LO; id < LO + 6; id++) {
            long desc = DESC + (id - LO) * STRIDE;
            StringBuilder sb = new StringBuilder("desc[0x" + Long.toHexString(id) + " @0x" + Long.toHexString(desc) + "] ");
            for (int o = 0; o < 0x30; o += 4) sb.append(Integer.toHexString(u32(desc + o))).append(' ');
            println(sb.toString());
        }
        // Scan every type: dump the keyList index run (terminator = full u32 == 0xff), flag 0x27+0x58 matches.
        println("\n== keyLists (desc+8 != 0) ==");
        for (long id = LO; id <= HI; id++) {
            long desc = DESC + (id - LO) * STRIDE;
            if (u32(desc + 8) == 0) continue;
            List<Integer> idx = new ArrayList<>();
            for (int k = 0; k < 62; k++) {
                int e = u32(desc + 0xc + k * 4L);
                if (e == 0xFF) break;
                idx.add(e & 0xFF);
            }
            boolean match = idx.contains(IDX_RECAST) && idx.contains(IDX_SPELL);
            if (!idx.isEmpty() && (match || idx.size() <= 12))
                println((match ? "*** " : "    ") + "id=0x" + Long.toHexString(id) + " desc+8=0x" + Integer.toHexString(u32(desc + 8)) + " keys=" + hx(idx));
        }
        println("\n== done ==");
    }

    String hx(List<Integer> l) { StringBuilder s = new StringBuilder(); for (int x : l) s.append("0x").append(Integer.toHexString(x)).append(' '); return s.toString(); }
}
