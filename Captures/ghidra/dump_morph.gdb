set pagination off
set confirm off
set height 0
set $count = 0
attach 1779180
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
break *0x00fee405
commands
  silent
  set $start = $esi
  set $end = $eax
  set $d = $edi
  set $w = *(float*)($ebx+0x1c)
  set $len = *(int*)($ebx+0x14)
  if $len > 15
    set $nameptr = *(char**)($ebx)
  else
    set $nameptr = (char*)$ebx
  end
  set $np = ($end-$start)/8
  if $count < 250 && $np > 0 && $np < 100000
    printf "MORPH #%d name=\"%s\" w=%f sA=%f sB=%f npairs=%d\n", $count, $nameptr, $w, *(float*)($d+8), *(float*)($d+0xc), $np
    eval "dump binary memory morphdump/m_%d.bin 0x%x 0x%x", $count, $start, $end
    set $count = $count + 1
  end
  continue
end
echo READY_CONTINUING\n
continue
