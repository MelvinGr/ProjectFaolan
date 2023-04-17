#!/bin/sh

GAME_DIR="/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan"
PROTON="/mnt/btrfs/@games/debian-installation/steamapps/common/Proton - Experimental/proton"

export STEAM_COMPAT_DATA_PATH="/mnt/btrfs/@games/debian-installation/steamapps/compatdata/217750"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.steam/steam"
export SteamAppId=217750
export SteamGameId=217750
export WINEDEBUG=-all

(
  cd "$GAME_DIR" || exit 1

  if [ -f LocalConfig.xml ]; then
    mv LocalConfig.xml LocalConfig.xml.disabled
  fi

  "$PROTON" run 'S:\common\Age of Conan\AgeOfConan.exe'
)
