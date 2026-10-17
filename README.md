# Haytham Core

A reusable Minecraft launcher core in C#: a content-addressed store (CAS) with garbage collection, version metadata, Java runtimes, mod loader installation, server runtimes, accounts and the game process.

Haytham Core is maintained by CairnPack and is under early development; nothing is usable yet. It knows nothing about modpacks: its launch entry point takes a game directory, a version and a loader.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build HaythamCore.slnx
```

## Microsoft sign-in

Haytham Core has no default Microsoft client ID. Every launcher that uses it must register its own Azure application and pass the client ID in.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
