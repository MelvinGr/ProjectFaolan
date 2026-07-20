import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
public class ReadFloat extends GhidraScript {
    @Override public void run() throws Exception {
        for (String hex : System.getenv("ADDRS").split(",")) {
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex.trim(),16));
            int bits = currentProgram.getMemory().getInt(a);
            println("FLOAT @"+hex+" = "+Float.intBitsToFloat(bits)+"  (raw 0x"+Integer.toHexString(bits)+")");
        }
    }
}
