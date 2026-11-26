#!/bin/sh
# Run by ByteBench in a throw-away helper container that shares the workspace's network namespace
# (docker run --network container:<workspace> --cap-drop ALL --cap-add NET_ADMIN --user 0 <this image> bb-apply-egress).
# The workspace container itself never has NET_ADMIN, so its user can't undo these rules.
#
#   BB_EGRESS_MODE     public-only (default) | none | open
#   BB_ALLOW           comma-separated ip:port pairs that stay reachable in every mode (ByteBench's git endpoint)
#
# public-only: loopback, replies and BB_ALLOW; everything private/link-local/CGNAT/multicast is rejected; the public internet is open.
# none:        loopback, replies and BB_ALLOW only.
# open:        no restrictions (not recommended).
set -eu
mode="${BB_EGRESS_MODE:-public-only}"

ipt() { iptables -w 5 "$@"; }
ipt -F OUTPUT
ipt -A OUTPUT -o lo -j ACCEPT
ipt -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
if [ -n "${BB_ALLOW:-}" ]; then
    old_ifs=$IFS; IFS=,
    for pair in $BB_ALLOW; do
        ip=${pair%:*}; port=${pair##*:}
        ipt -A OUTPUT -p tcp -d "$ip" --dport "$port" -j ACCEPT
    done
    IFS=$old_ifs
fi
case "$mode" in
    open) ;;
    none) ipt -A OUTPUT -j REJECT ;;
    public-only)
        for cidr in 0.0.0.0/8 10.0.0.0/8 100.64.0.0/10 127.0.0.0/8 169.254.0.0/16 172.16.0.0/12 192.0.0.0/24 192.168.0.0/16 198.18.0.0/15 224.0.0.0/4 240.0.0.0/4; do
            ipt -A OUTPUT -d "$cidr" -j REJECT
        done
        ;;
    *) echo "unknown BB_EGRESS_MODE: $mode" >&2; exit 2 ;;
esac
# IPv6 is switched off for the workspace (sysctl at start); belt and braces if it is still there.
if command -v ip6tables >/dev/null 2>&1; then
    ip6tables -w 5 -F OUTPUT 2>/dev/null || true
    ip6tables -w 5 -A OUTPUT -o lo -j ACCEPT 2>/dev/null || true
    if [ "$mode" != open ]; then ip6tables -w 5 -A OUTPUT -j REJECT 2>/dev/null || true; fi
fi
echo "egress policy applied: $mode"
