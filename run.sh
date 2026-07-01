#!/bin/sh

set -eu

case "${1:-}" in
    --faolan)
        dotnet run --project ./Faolan/Faolan.csproj
        ;;

    --web)
        dotnet run --project ./Faolan.Web/Faolan.Web.csproj
        ;;

    --cdn)
        dotnet run --project ./Faolan.Web.Content/Faolan.Web.Content.csproj -- \
            --store "/mnt/btrfs/@games/mirror-archive" \
            --ip 127.0.0.1 --port 7081
        ;;

    --client)
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --exe '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe' \
            --console \
            --universeaddr 127.0.0.1:7010 \
            --mirror 7081
        ;;

    --client2)
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --exe '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan 2/AgeOfConan.exe' \
            --console \
            --universeaddr 127.0.0.1:7010 \
            --mirror 7081
        ;;

    --claude)
        claude --dangerously-load-development-channels server:faolan-chat
        ;;

    --help|-h|"")
        echo "Usage: $0 [--web|--cdn|--client|--client2|--claude]" >&2
        exit 0
        ;;

    *)
        echo "Unknown command: $1" >&2
        echo "Usage: $0 [--web|--cdn|--client|--client2|--claude]" >&2
        exit 1
        ;;
esac

