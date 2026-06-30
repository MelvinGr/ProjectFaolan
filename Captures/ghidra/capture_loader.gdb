set pagination off
set confirm off
set height 0
set logging file /tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/b0778add-a126-477b-8758-2620da06bf1b/scratchpad/loader.log
set logging overwrite on
set logging enabled on
attach 1858810
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
# Inside FUN_005b07e0 at the type-discriminator CMP [EBP+0x34],0xf696e: args are EBP-relative.
# [EBP+0x34]=resource type, [EBP+0x30]=resource id, [EBP+4]=caller. Log every meshindex (0x f696e) load.
break *0x005b0b11
commands
  silent
  if *(unsigned int*)($ebp+0x34) == 0xf696e
    printf "MESHLOAD id=%u  a2c=%u a30=%u a38=%u  ret=0x%x\n", *(unsigned int*)($ebp+0x30), *(unsigned int*)($ebp+0x2c), *(unsigned int*)($ebp+0x30), *(unsigned int*)($ebp+0x38), *(unsigned int*)($ebp+4)
  end
  continue
end
echo READY_LOADER\n
continue
