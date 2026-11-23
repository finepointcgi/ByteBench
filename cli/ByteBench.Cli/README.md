# ByteBench CLI

`bytebench` is the command-line client for [ByteBench](https://github.com/finepointcgi/ByteBench): issues, pull requests,
repositories and CI pipelines from a terminal. It talks to the ByteBench REST API with a personal access token.

## Install

```sh
dotnet tool install -g ByteBench.Cli     # needs the .NET 10 runtime
dotnet tool update -g ByteBench.Cli
bytebench --version
```

Self-contained single-file binaries (no .NET needed) for macOS, Linux and Windows are attached to each `cli-v*` GitHub release,
with SHA-256 checksums.

## Use

```sh
echo "$TOKEN" | bytebench login --server https://bytebench.example.com
bytebench whoami
bytebench issues list --project WEB
bytebench --help
```

Shell completion: `bytebench completion bash|zsh|fish`.

Full documentation: the "AI tools (MCP) and the command line" page of the ByteBench documentation.
