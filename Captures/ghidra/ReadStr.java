import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
public class ReadStr extends GhidraScript {
    @Override public void run() throws Exception {
        for (String hex : System.getenv("ADDRS").split(",")) {
            Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex.trim(),16));
            StringBuilder sb=new StringBuilder();
            for(int i=0;i<200;i++){ int c=getByte(a.add(i))&0xFF; if(c==0)break; sb.append((char)c);}
            println("STR @"+hex+" = \""+sb+"\"");
        }
    }
}
