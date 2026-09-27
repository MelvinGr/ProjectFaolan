# Ghidra RPC-opcode recovery

Recovers the RPC method tables from `AgeOfConan.exe`. The result and full tables are in
`docs/re/aoc-rpc-method-opcodes.md`; the summary is in `docs/re/aoc-opcode-ids.md`.

## Reproduce
Import the client's `AgeOfConan.exe` (from its install dir; PE32 i386, stripped) into a Ghidra project, analyze it
once, then run each post-script against the analyzed program without re-analysis:

```
<ghidra>/support/analyzeHeadless <ghidra-project-dir> aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath <this dir> -postScript <Script>.java
```

Every script writes to `$FAOLAN_GHIDRA_OUT`, falling back to the system temp dir.

1. **DumpRpcTables.java** -> `$FAOLAN_GHIDRA_OUT/rpc_tables.txt`. Every `<Class>Proxy_t::<Method>` /
   `Dispatch_t::DispatchMethod` string in address order (the c2s registration order), plus the proxy constructors
   (the `min allowed` base check). Shows the c2s order equals the `aoc-client-rpc-methods.json` order.
2. **DumpDispatch.java** -> `$FAOLAN_GHIDRA_OUT/dispatch.txt`. Decompiles each
   `<Class>InterfaceDispatch_t::DispatchMethod`, a `switch(methodIndex)` whose case N calls handler N. The switch is
   the authoritative s2c table.
3. Parse the switch cases into `disp_cases.json` (case -> handler FUN address) and the handler addresses into
   `handler_addrs.txt`, then run **DumpHandlerNames.java** (reads `$FAOLAN_GHIDRA_OUT/handler_addrs.txt`) ->
   `$FAOLAN_GHIDRA_OUT/handler_names.txt` (handler FUN -> its `<Class>Dispatch_t::<Method>` string).
4. Join case and name into `s2c_tables.json` and the markdown tables.

The intermediate outputs are not committed; only the `.java` post-scripts are. The assembled ground-truth tables live
in `docs/re/aoc-rpc-method-opcodes.md` (committed), so the result is available without re-running Ghidra.
