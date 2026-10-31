#! /bin/sh

set -eu

command="${1:-}"

# Default UniverseAddr: dm09-nj4.ageofconan.com:7010
# --debug-channels

case "$command" in
    --faolan)
        shift
        dotnet run --project ./Faolan/Faolan.csproj -- \
            "$@"
        ;;

    --web)
        shift
        dotnet run --project ./Faolan.Web/Faolan.Web.csproj -- \
            "$@"
        ;;

    --cdn)
        shift
        dotnet run --project ./Faolan.Web.Content/Faolan.Web.Content.csproj -- \
            "$@"
        ;;

    --client)
        shift
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --dx10 \
            --console \
            --patch-universeaddr \
            --patch-cdn \
            --launcher-notes 'http://192.168.178.12:7081/_stats?UniverseName=%s&Language=%s' \
            "$@"
        ;;

    --client2)
        shift
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --exe '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan 2/AgeOfConan.exe' \
            --console \
            --patch-universeaddr \
            --patch-cdn \
            --launcher-notes 'http://192.168.178.12:7081/_stats?UniverseName=%s&Language=%s' \
            --debug-channels \
            "$@"
        ;;

    # One capture, told as a story (--report timeline). The RDB dir is what lets it decrypt the LDB
    # dialogue/quest text inline; without it those lines print as LDB[category][id].
    --timeline)
        shift
        dotnet run --project ./Faolan.Importer/Faolan.Importer.csproj -- \
            --report timeline \
            --report-arg "${1:?usage: $0 --timeline <capture>.tshark.json}" \
            --Content:RdbDir "${RDB_DIR:-/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB}"
        ;;

    --claude)
        shift
        #claude --dangerously-load-development-channels server:faolan-chat \
	claude \
            "$@"
        ;;

    --clean)
        shift
        find . -path ./.git -prune -o -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
        ;;

    *)
        echo "Unknown command: $command" >&2
        echo "Usage: $0 [--faolan|--web|--cdn|--client|--client2|--timeline <dump>|--claude|--clean] [extra args...]" >&2
        exit 1
        ;;
esac
