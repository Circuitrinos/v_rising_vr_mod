#!/bin/bash
# Installs or updates the VRisingVR passthrough server on a Steam Frame. Run it on the headset as the
# steamos user (Developer Mode, SSH). Everything goes into your home folder, so it survives SteamOS updates.
# The first install creates a random token; the mod needs it in its Passthrough.Token setting.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
app="$HOME/.local/share/vrisingvr-passthrough"
conf="$HOME/.config/vrisingvr-passthrough"
units="$HOME/.config/systemd/user"

mkdir -p "$app" "$conf" "$units"
install -m 755 "$here/vrisingvr-passthrough.py" "$app/"
install -m 644 "$here/vrisingvr-passthrough.service" "$units/"
if [ ! -s "$conf/token" ]; then
    (umask 077 && python3 -c 'import secrets; print(secrets.token_hex(32))' > "$conf/token")
    echo "Created a new token in $conf/token"
fi
chmod 600 "$conf/token"

systemctl --user daemon-reload
systemctl --user enable vrisingvr-passthrough.service
systemctl --user restart vrisingvr-passthrough.service
echo "VRisingVR passthrough server installed and running on port 47816."
echo "Put the token from $conf/token into the mod's Passthrough.Token setting."
