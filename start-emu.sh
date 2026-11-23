#!/usr/bin/env bash
# source（. ./start-emu.sh）された場合は、下の cd と exec が呼び出し元のシェルに
# 及ばないよう、別プロセスの bash で実行し直し、その終了コードを返す。
if [ "${BASH_SOURCE[0]}" != "$0" ]; then
  printf '注記: start-emu.sh は source せず ./start-emu.sh で実行してください。今回は別プロセスで起動します。\n' >&2
  bash -- "${BASH_SOURCE[0]:-$0}" || return $?
  return 0
fi
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"

SESSION=jp-emu
SUPERVISOR_LOG=emu/run/logs/supervisor.log
SUPERVISOR_EXIT=emu/run/logs/supervisor.exit

# tmux のウィンドウ0から呼ばれる内部モード。supervisor を子プロセスとして動かし、
# 終わったらセッションごと閉じる。
# - supervisor の出力はファイルへ書き、それを tail で表示する。セッションが kill されると
#   この端末は消え、端末へ直接書いていると後始末の途中で書き込みエラーになるため。
# - supervisor は SIGHUP を無視した状態で起動し（無視は引き継がれる）、HUP・INT・TERM は
#   ここで SIGINT として渡す。kill-session でも Ctrl+C と同じ後始末を通すため。
if [ "${1:-}" = --tmux-pane ]; then
  mkdir -p emu/run/logs
  rm -f "$SUPERVISOR_EXIT"
  : > "$SUPERVISOR_LOG"
  printf '%s\n' \
    'jp-bootcamp-webemu（tmux セッション jp-emu）' \
    '  操作パネル http://localhost:8080 ／ C3 http://localhost:8000' \
    '  終了: このウィンドウで Ctrl+C ／ 離れる（動かしたまま）: Ctrl+B のあと d' \
    '  ウィンドウ切替: Ctrl+B のあと番号（0 supervisor、1 ids、2 c3、3 renode、4 console）か w で一覧' \
    '' || true
  trap '' HUP
  python3 emu/supervisor.py >>"$SUPERVISOR_LOG" 2>&1 &
  pid=$!
  trap 'kill -INT "$pid" 2>/dev/null || true' HUP INT TERM
  tail -n +1 -F "$SUPERVISOR_LOG" 2>/dev/null &
  tail_pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    wait "$pid" || true
  done
  status=0
  wait "$pid" || status=$?
  printf '%s\n' "$status" > "$SUPERVISOR_EXIT" || true
  kill "$tail_pid" 2>/dev/null || true
  tmux kill-session -t "=$SESSION" 2>/dev/null || true
  exit "$status"
fi

if [ ! -f emu/run/.setup-done ]; then
  bash emu/setup.sh
fi

# 対話端末から tmux の外で起動したときだけ、tmux セッションを作って中で動かす
# （tmux が無い環境、非対話の起動、JP_EMU_TMUX=0 のときは従来どおり直接起動する）。
if [ "${JP_EMU_TMUX:-1}" != 0 ] && [ -z "${TMUX:-}" ] && [ -t 0 ] && [ -t 1 ] &&
  command -v tmux >/dev/null 2>&1; then
  # セッションに接続する。戻ってきたら、デタッチか supervisor の終了かを見分けて表示する。
  attach_and_report() {
    local attach=0 status=1
    tmux -u attach-session -t "=$SESSION" || attach=$?
    if tmux has-session -t "=$SESSION" 2>/dev/null; then
      if [ "$attach" != 0 ]; then
        printf 'tmux への接続に失敗しました（exit %s）。\n' "$attach" >&2
      fi
      printf '%s\n' \
        "エミュレータは tmux セッション $SESSION の中で動作を続けています。" \
        "  戻る: tmux attach -t $SESSION" \
        '  終了: 戻ってから、ウィンドウ0（supervisor）で Ctrl+C'
      exit 0
    fi
    # kill-session で閉じた場合は、supervisor の後始末がまだ続いていることがある。
    if [ ! -f "$SUPERVISOR_EXIT" ]; then
      printf 'supervisor の終了を待っています。\n'
      for _ in $(seq 200); do
        [ ! -f "$SUPERVISOR_EXIT" ] || break
        sleep 0.1
      done
    fi
    if [ -f "$SUPERVISOR_EXIT" ]; then
      status="$(cat -- "$SUPERVISOR_EXIT")"
    fi
    if [ "$status" = 0 ]; then
      printf 'supervisor が終了し、tmux セッション %s を閉じました。\n' "$SESSION"
    else
      printf 'supervisor が異常終了しました（exit %s）。出力の末尾（全体は %s）:\n' "$status" "$SUPERVISOR_LOG" >&2
      tail -n 20 -- "$SUPERVISOR_LOG" >&2 || true
    fi
    exit "$status"
  }
  if tmux has-session -t "=$SESSION" 2>/dev/null; then
    printf 'tmux セッション %s は起動済みです。接続します（離れるには Ctrl+B のあと d）。\n' "$SESSION"
    attach_and_report
  fi
  mkdir -p emu/run/logs
  rm -f "$SUPERVISOR_EXIT"
  if ! tmux new-session -d -s "$SESSION" -n supervisor -c "$PWD" -e "PATH=$PATH" \
    'exec bash ./start-emu.sh --tmux-pane'; then
    printf 'tmux セッションを作れなかったため、tmux を使わずに起動します。\n' >&2
    exec python3 emu/supervisor.py
  fi
  for name in ids c3 renode console; do
    file="emu/run/logs/$name.log"
    [ "$name" != console ] || file=emu/run/logs/console.bin
    tmux new-window -d -t "=$SESSION:" -n "$name" -c "$PWD" \
      "printf '== %s ==\\n' '$file'; exec tail -n 0 -F '$file'" ||
      true
  done
  tmux select-window -t "=$SESSION:supervisor" || true
  attach_and_report
fi

exec python3 emu/supervisor.py
