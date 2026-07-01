#!/bin/sh

set -eu

case "${1:-}" in
    --web)
        dotnet run --project ./Faolan.Web/Faolan.Web.csproj
        ;;

    --content)
        dotnet run --project ./Faolan.ContentMirror/Faolan.ContentMirror.csproj -- \
            --store "/mnt/btrfs/@games/mirror-archive"
        ;;

    --client)
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --exe '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe' \
            --console \
            --universeaddr 127.0.0.1:7010 \
            --mirror 7081 &
        ;;

    --client2)
        dotnet run --project ./Faolan.ClientLauncher/Faolan.ClientLauncher.csproj --no-build -- \
            --exe '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan 2/AgeOfConan.exe' \
            --console \
            --universeaddr 127.0.0.1:7010 \
            --mirror 7081 &
        ;;

    --claude)
        claude --dangerously-load-development-channels server:faolan-chat
        ;;

    --help|-h|"")
        echo "Usage: $0 [--web|--content|--client]"
        exit 0
        ;;

    *)
        echo "Unknown command: $1" >&2
        echo "Usage: $0 [--web|--content|--client|--client2|--claude]" >&2
        exit 1
        ;;
esac

