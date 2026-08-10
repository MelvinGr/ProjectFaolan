// Dump an MSVC dense-switch dispatch pair: the byte INDEX table and the dword TARGET table.
//
// MSVC compiles a switch whose cases are dense-but-not-contiguous as two tables:
//     MOVZX EAX,byte ptr [EAX + <BYTES>]      ; case index for (value - BASE)
//     JMP   dword ptr [EAX*4 + <TARGETS>]     ; the address that index selects
// so neither table alone says which VALUE reaches which branch - you have to compose them. This prints one line
// per value: `<value hex/dec> -> idx <n> -> <target address>`, which is what turns "these ids go to the % branch"
// into a list you can transcribe.
//
//   BYTES=111a060 COUNT=f0 TARGETS=111a054 BASE=31 \
//     /opt/ghidra/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpSwitchTable.java
//
// COUNT is the switch's own bound (the `CMP EAX,<n>` guarding the jump, inclusive - pass n+1) and BASE the value
// the `ADD EAX,-<base>` subtracts. All four are hex, no 0x.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

public class DumpSwitchTable extends GhidraScript {
    @Override public void run() throws Exception {
        long bytes = Long.parseLong(System.getenv("BYTES").trim(), 16);
        long targets = Long.parseLong(System.getenv("TARGETS").trim(), 16);
        int count = Integer.parseInt(System.getenv("COUNT").trim(), 16);
        long base = Long.parseLong(System.getenv("BASE").trim(), 16);

        for (int i = 0; i < count; i++) {
            Address b = toAddr(bytes + i);
            int idx = currentProgram.getMemory().getByte(b) & 0xff;
            int target = currentProgram.getMemory().getInt(toAddr(targets + idx * 4L));
            println(String.format("%d 0x%x -> idx %d -> 0x%08x", base + i, base + i, idx, target));
        }
    }
}
