#!/usr/bin/env bash
# 上流 JP-Bootcamp の攻撃スクリプトを、起動中のエミュレータに向けて1本ずつ実行する。
#   ./attack.sh list              攻撃の一覧（上流の attacks/ から生成）
#   ./attack.sh <番号> [引数…]    攻撃を実行（例: ./attack.sh 00、./attack.sh 06 --yes）
#   ./attack.sh reset             攻撃で変わった状態を戻す（attacks/lib/reset_bench.py）
# 上流のコードはコピーせず、setup が取得したチェックアウト内のスクリプトを、setup が作った
# venv の python でその場で実行する。FLATSAT_HOST=1 により、通信は Space IDS
# （127.0.0.1:5010）を経由して基板（エミュレータ）へ届き、IDS の検査を受ける。
if [ "${BASH_SOURCE[0]}" != "$0" ]; then
  printf '注記: attack.sh は source せず ./attack.sh で実行してください。今回は別プロセスで実行します。\n' >&2
  bash -- "${BASH_SOURCE[0]:-$0}" "$@" || return $?
  return 0
fi
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"

ATTACKS=emu/run/upstream/JP-Bootcamp/attacks
VENV_PY=emu/run/venv/bin/python
STATE_URL=http://127.0.0.1:8080/api/state

usage() {
  printf '%s\n' \
    '使い方:' \
    '  ./attack.sh list              攻撃の一覧' \
    '  ./attack.sh <番号> [引数…]    攻撃を実行（例: ./attack.sh 00）' \
    '  ./attack.sh reset             攻撃で変わった基板の状態を戻す' \
    'エミュレータを ./start-emu.sh で起動し、操作パネルが READY になってから使う。' >&2
  exit 2
}

require_setup() {
  if [ ! -f emu/run/.setup-done ] || [ ! -x "$VENV_PY" ] || [ ! -d "$ATTACKS/lib" ]; then
    printf 'セットアップが済んでいません。先に ./start-emu.sh（または ./emu/setup.sh）を実行してください。\n' >&2
    exit 1
  fi
}

# 操作パネルの /api/state で READY を確かめる。未起動・未 READY なら案内して終わる。
require_ready() {
  local state
  if ! state="$(python3 -I -c '
import json, sys, urllib.request
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
with opener.open(sys.argv[1], timeout=3) as response:
    print(json.load(response).get("state", ""))
' "$STATE_URL" 2>/dev/null)"; then
    printf '%s\n' \
      'エミュレータが起動していません（操作パネル 127.0.0.1:8080 に接続できません）。' \
      '別のターミナルで ./start-emu.sh を実行し、操作パネルが READY になってから、もう一度実行してください。' >&2
    exit 1
  fi
  if [ "$state" != READY ]; then
    printf 'エミュレータはまだ READY ではありません（現在の状態: %s）。\n' "${state:-不明}" >&2
    printf '操作パネル（http://localhost:8080）で READY になるのを待ってから、もう一度実行してください。\n' >&2
    exit 1
  fi
}

# 基板の実際の状態（位置と GS モード）を、上流ライブラリそのもので根気よく
# 読み直す。第1引数 = 読み直しを続ける秒数。戻っていることが実状態で確認
# できれば 0、できなければ 1 を返す。
verify_reset_state() {
  FLATSAT_HOST=1 PYTHONUNBUFFERED=1 "$VENV_PY" - "$ATTACKS/lib" "$1" <<'PYEOF'
import sys, time
sys.path.insert(0, sys.argv[1])
from flatsat import FlatSat
import benchstate

TOKYO = (35.7122, 139.7038)
deadline = time.time() + int(sys.argv[2])
attempt = 0
while True:
    attempt += 1
    st = None
    for enc in (True, False):
        try:
            with FlatSat(encrypt=enc) as fs:
                got = benchstate.read_state(fs, tries=4)
        except Exception as e:
            print(f"  [{attempt}] 読み直しに失敗 (encrypt={enc}): {e}", flush=True)
            continue
        if got.latitude is not None:
            st = got
            break
        if st is None:
            st = got
    if st is not None:
        print(f"  [{attempt}] position={st.latitude},{st.longitude}"
              f" gs_mode={st.gs_mode}", flush=True)
        if (st.latitude is not None
                and abs(st.latitude - TOKYO[0]) <= 0.01
                and abs(st.longitude - TOKYO[1]) <= 0.01
                and st.gs_mode is not True):
            sys.exit(0)
    if time.time() >= deadline:
        sys.exit(1)
    time.sleep(3)
PYEOF
}

# 攻撃ディレクトリ（NN_名前）の中の *_usb.py を1つ返す。
attack_script() {
  local scripts=("$1"/*_usb.py)
  if [ "${#scripts[@]}" -ne 1 ] || [ ! -f "${scripts[0]}" ]; then
    printf '%s に実行するスクリプト（*_usb.py）が1つに定まりません。\n' "$1" >&2
    exit 1
  fi
  printf '%s\n' "${scripts[0]}"
}

# スクリプト冒頭のコメント「# <ファイル名> -- 説明」（続く行も含む）から概要を取り出す。
describe() {
  awk -v head="# ${1##*/} -- " '
    found && /^#[[:space:]]*$/ { exit }
    found { sub(/^#[[:space:]]*/, ""); text = text " " $0; next }
    index($0, head) == 1 { text = substr($0, length(head) + 1); found = 1 }
    END { print text }' "$1"
}

list_attacks() {
  local dir name
  printf '上流 %s の攻撃（./attack.sh <番号> で実行）:\n' "$ATTACKS"
  for dir in "$ATTACKS"/[0-9][0-9]_*/; do
    dir=${dir%/}
    name=${dir##*/}
    printf '  %s  %-26s %s\n' "${name%%_*}" "${name#*_}" "$(describe "$(attack_script "$dir")")"
  done
  printf '  %-30s %s\n' reset '攻撃で変わった基板の状態を戻す（attacks/lib/reset_bench.py）'
  printf '%s\n' \
    '1本ずつ実行し、次の攻撃の前に ./attack.sh reset を実行すること（上流 attacks/README.md の注意）。'
}

require_setup
[ "$#" -ge 1 ] || usage
case "$1" in
  list)
    list_attacks
    ;;
  reset)
    require_ready
    printf '基板の状態を戻します: %s/lib/reset_bench.py（FLATSAT_HOST=1、IDS 経由）\n' "$ATTACKS"
    status=0
    FLATSAT_HOST=1 PYTHONUNBUFFERED=1 "$VENV_PY" "$ATTACKS/lib/reset_bench.py" || status=$?
    if [ "$status" != 0 ]; then
      # reset_bench が「戻らなかった」と判定する理由は2つある。
      # (1) 確認待ち（約15秒）が GPS_OVERRIDE の後の LED 点滅（実時間で数十秒）
      #     より短く、コマンドが届いていても時間切れになる。
      # (2) 基板の USB デバッグが ON の間（攻撃00・09 の後）、基板は上り
      #     ペイロードを復号せず平文として読む。reset_bench は暗号化して送る
      #     ため、GPS_OVERRIDE の暗号文そのものが位置として書き込まれてしまう
      #     （実機でも同じ。reset_bench の最後のコマンドでデバッグは OFF に
      #     なるので、2回目の reset では正しく戻る）。
      # どちらも、基板の実状態を読み直し、必要なら reset をもう一度実行する
      # ことで判定し直せる。
      printf '%s\n' 'reset_bench では確認できませんでした。基板の実際の状態を読み直して判定します（最大180秒）…'
      if verify_reset_state 180; then
        printf '%s\n' '基板の実状態で確認できました: 位置は東京に戻り、GS モードも入っていません（reset のコマンドは届いていました）。'
        exit 0
      fi
      printf '%s\n' 'まだ戻っていません。reset をもう一度実行します（USB デバッグが OFF になった後の2回目は、位置も正しく書き込まれます）…'
      FLATSAT_HOST=1 PYTHONUNBUFFERED=1 "$VENV_PY" "$ATTACKS/lib/reset_bench.py" || true
      if verify_reset_state 240; then
        printf '%s\n' '基板の実状態で確認できました: 位置は東京に戻り、GS モードも入っていません。'
        exit 0
      fi
      printf '%s\n' \
        '読み直しても、戻ったことを確認できませんでした。基板は遅れて処理を終えることがあります。' \
        '1分ほど待ってから、もう一度 ./attack.sh reset を実行するか、C3 の PING で応答を確かめてください。' \
        '戻らないときは操作パネルの RESET でファームを起動し直してください。' >&2
    fi
    exit "$status"
    ;;
  -h | --help | help)
    usage
    ;;
  *)
    if ! [[ "$1" =~ ^[0-9]{1,2}$ ]]; then
      printf '番号・list・reset のいずれかを指定してください: %s\n' "$1" >&2
      usage
    fi
    num="$(printf '%02d' "$((10#$1))")"
    shift
    dirs=("$ATTACKS/${num}_"*/)
    if [ ! -d "${dirs[0]}" ]; then
      printf '攻撃 %s は見つかりません。./attack.sh list で一覧を確認してください。\n' "$num" >&2
      exit 2
    fi
    script="$(attack_script "${dirs[0]%/}")"
    require_ready
    printf '攻撃 %s を実行します: %s（FLATSAT_HOST=1、IDS 経由）\n' "$num" "$script"
    case "$num" in
      00) printf '注意: 00 は掃引の副作用で基板の USB デバッグを ON にします。終わったら ./attack.sh reset で戻してください。\n' ;;
      01) printf '注意: 01 はファザーで、基板が応答しなくなることがあります（上流の注意）。\n' ;;
      06) printf '注意: 06 は RESETC で基板を再起動させます（確認を省くには --yes）。\n' ;;
    esac
    status=0
    FLATSAT_HOST=1 PYTHONUNBUFFERED=1 "$VENV_PY" "$script" "$@" || status=$?
    if [ "$status" = 0 ]; then
      printf '攻撃 %s が終わりました。次の攻撃の前に ./attack.sh reset で基板の状態を戻してください' "$num"
      printf '（基板が応答しなくなった場合は、操作パネルの RESET）。\n'
    else
      printf '攻撃 %s は exit %s で終わりました。\n' "$num" "$status" >&2
    fi
    exit "$status"
    ;;
esac
