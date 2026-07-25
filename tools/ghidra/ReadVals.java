// Dump each address (ADDRS env, comma-separated hex) as int / float / double at once - for reading
// initializer constants out of a class ctor when you do not yet know the field's type.
//   ADDRS=020b58d8,020b58d0 /opt/ghidra/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript ReadVals.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

public class ReadVals extends GhidraScript {
    @Override public void run() throws Exception {
        for (String hex : System.getenv("ADDRS").split(",")) {
            hex = hex.trim();
            if (hex.isEmpty()) continue;
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex, 16));
            try {
                int i = currentProgram.getMemory().getInt(a);
                long l = currentProgram.getMemory().getLong(a);
                println("@" + hex + "  int=" + i + "  float=" + Float.intBitsToFloat(i) + "  double=" + Double.longBitsToDouble(l) + "  rawq=0x" + Long.toHexString(l));
            } catch (Exception ex) {
                println("@" + hex + "  <unreadable: " + ex + ">");
            }
        }
    }
}
