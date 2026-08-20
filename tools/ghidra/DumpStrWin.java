// AT=<hex addr> [SPAN=<bytes>] : every printable run in a WINDOW around an address, with its address.
//
// The neighbourhood tool. A hit from FindStr/FindSym tells you a string exists; what usually matters is what sits
// AROUND it, because this binary stores related strings contiguously - a stat-name table, a tweak-key table, a
// window-name registry. Written for #509, where finding `DailyChallenge` was worth little and the twenty strings
// on either side of it turned out to be the entire daily-challenge stat block in declaration order
// (DailyResetTimestamp / ... / CounterDailyChallenges), which is what made the subsystem decodable at all.
//
//   AT=01f25898 SPAN=700 /opt/ghidra/support/analyzeHeadless <ghidra-proj> aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpStrWin.java
//
// Prints to stdout (one line per run, so `grep "DumpStrWin.java>"` is safe here - see the multi-line caveat in
// the capture/Ghidra notes for scripts that print bodies).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
public class DumpStrWin extends GhidraScript {
    public void run() throws Exception {
        Address base=currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(System.getenv("AT"),16));
        int span=System.getenv("SPAN")!=null?Integer.parseInt(System.getenv("SPAN")):2000;
        Address a=base.subtract(span/2);
        StringBuilder cur=new StringBuilder();
        for(int i=0;i<span;i++){
            int c=getByte(a.add(i))&0xFF;
            if(c>=32&&c<127) cur.append((char)c);
            else { if(cur.length()>=5) println(String.format("%s  %s", a.add(i-cur.length()), cur)); cur.setLength(0);}
        }
    }
}
