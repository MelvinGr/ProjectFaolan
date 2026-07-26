#! /bin/sh

set -eu

command="${1:-}"

# Default UniverseAddr: dm09-nj4.ageofconan.com:7010

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
            --CdnMirror:Store "/mnt/btrfs/@games/mirror-archive" \
            --CdnMirror:Ip 127.0.0.1 \
            --CdnMirror:Port 7081 \
            "$@"
        ;;

    --client)
        shift
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --console \
            --universeaddr 127.0.0.1:7010 \
            --cdn http://127.0.0.1:7081 \
            --launcher-notes 'http://127.0.0.1:7081/_stats?UniverseName=%s&Language=%s' \
            --debug-channels \
            "$@"
        ;;

    --client2)
        shift
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --exe '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan 2/AgeOfConan.exe' \
            --console \
            --universeaddr 127.0.0.1:7010 \
            --cdn http://127.0.0.1:7081 \
            --launcher-notes 'http://127.0.0.1:7081/_stats?UniverseName=%s&Language=%s' \
            --debug-channels \
            "$@"
        ;;

    --claude)
        shift
        claude --dangerously-load-development-channels server:faolan-chat \
            "$@"
        ;;

    --clean)
        shift
        find . -path ./.git -prune -o -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
        ;;

    *)
        echo "Unknown command: $command" >&2
        echo "Usage: $0 [--faolan|--web|--cdn|--client|--client2|--claude|--clean] [extra args...]" >&2
        exit 1
        ;;
esac
