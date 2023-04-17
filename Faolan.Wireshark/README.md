# Faolan.Wireshark (v1 bootstrap)

This folder contains the initial code to start a Wireshark C plugin for **GameServer zlib stream decompression only**.

Current state:

- `src/faolan_zlib_stream.*`: standalone stream processor for
  - marker detection: `0x80 00 00 05`
  - 9-byte compression header buffering
  - incremental zlib inflate across chunked input
- `src/faolan_zlib_stream_cli.c`: tiny test runner covering
  - valid chunked compressed stream
  - non-compressed stream fallback
  - invalid compressed data failure path
- `wireshark/packet-faolan-gameserver-zlib.c`: initial dissector wiring with
  - conversation-scoped state
  - two directional stream states (`A -> B` and `B -> A`)
  - per-frame decompression diagnostics
  - raw decompressed byte blob field

No packet decoding/framing is implemented yet.

## Build and test the standalone harness

```zsh
cmake -S '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark' -B '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build'
cmake --build '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build' -j
'/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build/faolan_zlib_stream_cli'
```

Expected output:

- `OK: chunked success + non-compressed + invalid-data tests passed`

## Build and install the Wireshark plugin

This project now builds a loadable EPAN plugin module: `faolan_gs_zlib.so`.

Default install target:

- `/home/<user>/.local/lib/wireshark/plugins/<major.minor>/epan`

Build + install to your personal plugin directory:

```zsh
cmake -S '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark' -B '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build'
cmake --build '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build' -j
cmake --build '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build' --target install-faolan-plugin
```

To override destination directory:

```zsh
cmake -S '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark' -B '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build' \
  -DFAOLAN_PERSONAL_PLUGIN_DIR='/some/other/path/wireshark/plugins/4.6/epan'
```

## Verify plugin is loaded

```zsh
tshark -G plugins | grep -i faolan | cat
tshark -G fields | grep -E 'faolan_gs\.' | cat
```

## Test with an existing capture

Use decode-as on the known GameServer port (`7029` for this capture):

```zsh
tshark -r '/home/melvin/Development/C#/Project Faolan/Captures/account-stricken997-character-Strie-login-randomActions.pcapng' \
  -d tcp.port==7029,faolan_gs \
  -Y 'faolan_gs && faolan_gs.output_bytes > 0' \
  -T fields \
  -e frame.number \
  -e faolan_gs.direction \
  -e faolan_gs.compression_state \
  -e faolan_gs.output_bytes | head -n 20 | cat
```

In Wireshark UI:

1. Open the `.pcapng`
2. `Analyze -> Decode As...`
3. Map the GameServer TCP port to `FaolanGS`
4. Filter with `faolan_gs`

To inspect decompressed bytes directly:

1. Select a packet where `faolan_gs.output_bytes > 0`
2. In the packet details pane, expand `FaolanGS`
3. Click the `Decompressed Bytes` field
4. In the packet bytes pane, switch source to `Faolan decompressed stream bytes`

That data source is the raw inflated output for that frame (no packet decoding applied).

## Troubleshooting: `Compression State = Failed`

If you see `Failed` immediately or repeatedly:

1. Rebuild and reinstall the plugin.
2. Fully restart Wireshark (it caches loaded plugins per process).
3. Verify the loaded plugin path/version.

```zsh
cmake --build '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build' -j
cmake --build '/home/melvin/Development/C#/Project Faolan/Faolan.Wireshark/build' --target install-faolan-plugin
tshark -G plugins | grep -i faolan | cat
```

Notes:

- Server direction usually becomes `Enabled`; client direction may stay `NotCompressed`.
- In this capture set, that mixed direction state is expected.

## Next steps

1. Add explicit handling for TCP retransmission/re-dissect edge cases
2. Decide whether direction mapping should use endpoint addresses in addition to ports
3. Add a cap for decompressed bytes shown per frame to avoid UI/memory spikes
4. Integrate this dissector file into a real Wireshark plugin build tree
