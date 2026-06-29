set pagination off
set confirm off
set height 0
attach 1779180
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
break *0x00fee1b6
commands
  silent
  set $C = *(int*)($ebx+0x16c)
  set $base = *(int*)($C+0x1a0)
  set $cnt = *(int*)($C+0x1a4)
  printf "NODES base=0x%x count=%d\n", $base, $cnt
  eval "dump binary memory nodes.bin 0x%x 0x%x", $base, $base+$cnt*0x50
  detach
  quit
end
echo READY_NODES\n
continue
