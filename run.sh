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
            --console \
            --universeaddr 127.0.0.1:7010 \
            --mirror 7081
        ;;

    --help|-h|"")
        echo "Usage: $0 [--web|--content|--client]"
        exit 0
        ;;

    *)
        echo "Unknown command: $1" >&2
        echo "Usage: $0 [--web|--content|--client]" >&2
        exit 1
        ;;
esac

