#!/usr/bin/env bash
# Installs Hopital on the Pi from the folder this script is in.
# deploy.ps1 copies that folder over and runs this over ssh; it can also be run by hand:
#   bash install.sh                # install and (re)start the receiver service
#   bash install.sh --no-service   # install only; run Hopital yourself
set -euo pipefail

src="$(cd "$(dirname "$0")" && pwd)"
dest="$HOME/hopital"
service=true
[[ "${1:-}" == "--no-service" ]] && service=false

mkdir -p "$dest/received"
# install writes a new file, so a running copy keeps going until it is restarted.
install -m 755 "$src/Hopital" "$dest/Hopital"
rm -rf "$dest/scenarios" "$dest/samples"
cp -r "$src/scenarios" "$src/samples" "$dest/"
sudo ln -sf "$dest/Hopital" /usr/local/bin/hopital
echo "Installed $("$dest/Hopital" --version) in $dest (also on the PATH as 'hopital')"

if $service; then
  sed -e "s|@USER@|$USER|g" -e "s|@HOME@|$HOME|g" "$src/hopital-receiver.service" \
    | sudo tee /etc/systemd/system/hopital-receiver.service > /dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable hopital-receiver --quiet
  sudo systemctl restart hopital-receiver
  sleep 2
  systemctl --no-pager --lines=8 status hopital-receiver || true
fi
