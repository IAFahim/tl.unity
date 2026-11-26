#!/usr/bin/env bash
set -uo pipefail
echo "== lscpu"; lscpu
echo "== per cpu"; lscpu -e=CPU,CORE,SOCKET,NODE,ONLINE,MAXMHZ,MINMHZ 2>/dev/null || lscpu -e
echo "== nproc $(nproc)"
echo "== affinity $(taskset -pc $$)"
for f in /sys/devices/system/cpu/cpu[0-9]*/topology/thread_siblings_list; do c=${f#/sys/devices/system/cpu/cpu}; c=${c%%/*}; echo "cpu$c siblings $(cat $f) maxkhz $(cat /sys/devices/system/cpu/cpu$c/cpufreq/cpuinfo_max_freq 2>/dev/null || echo ?)"; done | sort -V
spin='import time
t=time.perf_counter(); x=0
for i in range(20000000): x+=i
print(round(time.perf_counter()-t,3))'
echo "== one thread per cpu, seconds for 2e7 adds"
for c in $(seq 0 $(( $(nproc) - 1 ))); do echo "cpu$c $(taskset -c $c python3 -c "$spin")"; done
echo "== n threads at once on cpus 0..n-1, slowest seconds"
for n in $(seq 1 $(nproc)); do
  pids=(); for c in $(seq 0 $(( n - 1 ))); do taskset -c $c python3 -c "$spin" > /tmp/topo.$c & pids+=($!); done
  wait "${pids[@]}"; echo "n=$n $(cat $(for c in $(seq 0 $(( n - 1 ))); do echo /tmp/topo.$c; done) | sort -n | tail -1)"
done
echo "== n unpinned threads at once, slowest seconds"
for n in $(seq 1 $(nproc)); do
  pids=(); for c in $(seq 1 $n); do python3 -c "$spin" > /tmp/topo.u$c & pids+=($!); done
  wait "${pids[@]}"; echo "n=$n $(cat $(for c in $(seq 1 $n); do echo /tmp/topo.u$c; done) | sort -n | tail -1)"
done
rm -f /tmp/topo.*
