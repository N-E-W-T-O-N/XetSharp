---
name: working-on-xetsharp
description: Use when building, testing, debugging, or extending the XetSharp repo (Rust FFI + .NET bindings for HuggingFace xet-core) — especially before running builds/tests, editing .csproj files, writing tests that hit remote endpoints, or debugging CI pack failures / MSB4025 / hangs.
---

# Working on XetSharp

Tribal knowledge the README and inline comments do NOT carry. Read HANDOFF.md for project state;
read src/lib.rs header for the FFI safety rules (panic/catch_unwind, bridge_sync, string ownership).

## Dev workflow: edit in WSL, build in container

The repo lives in the WSL distro (`/home/user/XetSharp`), but the **bare distro has only git** — no
dotnet/cargo. The toolchain lives in the Docker devcontainer.

```bash
docker ps -a | grep vsc-xetsharp          # find container (random name), `docker start <id>` if exited
docker exec <id> bash -lc 'cd /workspaces/XetSharp && cargo build && dotnet test XetSharp.Tests/XetSharp.Tests.csproj'
```

Edit files via the WSL path; never install the toolchain into the distro; if Docker is down, ask —
don't work around it.

## Trap table (each of these has actually happened here)

| Symptom | Cause | Fix |
|---|---|---|
| `MSB4025: XML comment cannot contain '--'` | Wrote `--release`/`--target` inside a `<!-- -->` csproj comment | Reword without double hyphens |
| Build script `Permission denied (os error 13)` even after rebuild | `target/` copied across filesystems loses exec bits | `rm -rf target` and rebuild (a plain rebuild reuses the broken files) |
| Test/demo hangs for 10+ minutes | Remote call to unreachable endpoint → xet-core retry/backoff loop | Never point tests at fake endpoints; exercise the remote FFI via input-validation fast-fails (empty token) |
| CI green but nupkg artifact is a lone ~10 KB `XetSharp` package | Natives didn't stage: upload-artifact v4 strips paths, so pack's `Condition="Exists(...)"` misses | Pack job must restage per the RID→triple map in ci.yml; the verify step should have failed — check it wasn't weakened |
| CS4010 on `TokenRefreshCallback` | Tried `async () =>` lambda | Delegate is synchronous (native calls it via fn pointer). Block inside: `GetXetConnectionInfoAsync(...).GetAwaiter().GetResult()` |
| Hand edits to `XetSharp/Native/NativeMethods.g.cs` vanish | File is csbindgen-generated on every `cargo build` | Never edit; change `src/lib.rs`, rebuild. Additions go in the `NativeMethods.Loader.cs` partial |

## Rules that guard correctness

- **Every test in XetSharp.Tests must cross the FFI boundary** — a test that only exercises managed
  code proves nothing about the interop. Use local CAS mode (same engine as remote, no network).
- **Dependency direction:** `Huggingface → XetSharp → xet-core`. Never reference the other way.
- **`panic = "unwind"` stays.** `"abort"` turns any Rust panic into CLR process death.
- **Packaging is split** (meta + `runtime.json` + per-RID runtime packages). A new platform =
  new `packaging/XetSharp.runtime.<rid>` project + `runtime.json` entry + CI matrix row. The
  `netstandard2.0` TFM in runtime packages is an inert placeholder — leave it.

## Verifying and debugging CI

```bash
gh run list -R N-E-W-T-O-N/XetSharp
gh run download <run-id> -R N-E-W-T-O-N/XetSharp   # natives + logs + test .trx + nupkgs
```
Definition of green: 6 native binaries (check arch, not just existence), 8/8 tests, and
**meta + one runtime nupkg per built RID** (~4 MB each). Release natives are ~8–10 MB; a 164 MB
binary means someone packed a debug build.
