import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
public class WhatAt extends GhidraScript {
  public void run() throws Exception {
    for (String h : System.getenv("ADDRS").split(",")) {
      Address a = toAddr(Long.parseLong(h.trim(),16));
      Function f = getFunctionContaining(a);
      Data d = getDataContaining(a);
      println(h+": fn="+(f!=null?f.getName()+"@"+f.getEntryPoint():"-")+" data="+(d!=null?d.toString():"-")+" sym="+getSymbolAt(a));
    }
  }
}
