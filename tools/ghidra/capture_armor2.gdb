set pagination off
set confirm off
set height 0
set logging file /tmp/armor_capture2.log
set logging overwrite on
set logging enabled on
attach 1854199
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
# FUN_005b07e0 = the visual mesh loader (thiscall, ECX = resource ref struct {id@+0x10, type@+0x14}).
# Log only meshindex (type 0x f696e) loads, with the caller return address (= which builder path).
break *0x005b07e0
commands
  silent
  set $type = *(unsigned int*)($ecx+0x14)
  if $type == 0xf696e
    printf "MESHIDX id=%u (0x%x)  caller=0x%x\n", *(unsigned int*)($ecx+0x10), *(unsigned int*)($ecx+0x10), *(unsigned int*)($esp)
  end
  continue
end
echo READY_ARMOR2\n
continue
