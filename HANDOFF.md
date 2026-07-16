# XetSharp — Engineering Handoff Notes

Principal-engineer handoff for whoever owns this next. The README covers *what* the project is;
this file is the *state of the work* — what's done and verified, what's pending, and the
non-obvious decisions with their reasons. The day-to-day gotchas live in the project skill at
`.claude/skills/working-on-xetsharp/SKILL.md`.

## 1. Architecture in one paragraph

`src/lib.rs` is a panic-safe C-ABI shim over xet-core (git submodule, pinned `v1.5.1`) compiled to
`xetcore_native`. `csbindgen` (in `build.rs`) regenerates `XetSharp/Native/NativeMethods.g.cs` on
every `cargo build`. `XetSharp/Client.cs` is the managed API; `NativeMethods.Loader.cs` resolves
the right native binary per OS/arch at runtime. `Huggingface/` is the `huggingface_hub`-style
orchestration layer (Hub API, Xet detection, CAS-token minting) that routes transfers through
XetSharp. Dependency direction is strictly: `Huggingface → XetSharp → xet-core`. Never invert it.

## 2. Done & verified checklist

- [x] Local CAS upload (chunk + dedup + xorb/shard write) — round-trip byte-identical
- [x] Local download / reconstruction from Merkle hash
- [x] Remote upload/download FFI (`xet_upload_file_remote` / `xet_download_file_remote`)
- [x] Token-refresh callback across FFI (C# delegate → Rust `TokenRefresher`) — test proves it fires
- [x] File logging (`xet_init_logging` → tracing to file, level via `XET_LOG`)
- [x] Cross-platform native loading (`SetDllImportResolver` + `runtimes/<rid>/native/`)
- [x] Native builds for 6 RIDs: linux-x64/arm64, win-x64/arm64, osx-x64/arm64 (CI-verified, correct arch)
- [x] Split NuGet set: meta `XetSharp` + `runtime.json` + per-RID `XetSharp.runtime.<rid>` (7 packages out of CI)
- [x] xUnit v3 interop suite — 8/8, every test crosses the FFI boundary
- [x] CI (`.github/workflows/ci.yml`): native matrix → test → pack, artifacts + logs uploaded `if: always()`
- [x] `Huggingface.HubClient`: `IsXetEnabledAsync`, `GetXetConnectionInfoAsync`, classic `DownloadFileAsync`, `DownloadFileXet` plumbing

## 3. Pending work (priority order)

- [ ] **Filename → Xet hash resolution.** `DownloadFileXet` takes a Merkle hash; real callers have a
      filename. The mapping comes from the Hub's `resolve` metadata — study how `huggingface_hub`
      (Python) extracts it, then make `DownloadFileXet` take a filename. Until then,
      `DownloadFileAsync` (classic resolve) downloads any model correctly; Xet is the fast path.
- [ ] **Upload commit flow.** Xet-uploading bytes to CAS is done; registering the file in a repo
      needs the Hub's preupload/commit API with the Xet pointer. Needs a write token to validate.
- [ ] **Live end-to-end against the real Hub.** Everything remote-side is verified up to the FFI/auth
      boundary; nobody has run a real HF upload/download yet. Needs `XET_ENDPOINT`/`XET_TOKEN`
      (mint via `HubClient`) — consider CI secrets + an opt-in test job.
- [ ] **Session-handle API** (opaque `xet_session_new/upload/finalize/free`) so many files share one
      dedup session. Today each call is its own session — correct but loses cross-file dedup.
- [x] **GitHub Release publishing** — pushing a `v*` tag attaches all 7 nupkgs to a GitHub Release
      (built-in `GITHUB_TOKEN`, no secrets). Note: package `<Version>` is hard-coded 0.1.0 in the
      csproj/runtime.json — bump those (or derive from the tag) before tagging a new version.
- [ ] **nuget.org / GitHub Packages feed publish** (needs `NUGET_API_KEY` secret or GH Packages
      auth) — release-page assets are downloadable but not `dotnet add package`-consumable.
- [x] **arm64 execution testing** — repo is now public, so the CI test matrix runs the full FFI
      suite on real `ubuntu-24.04-arm` and `windows-11-arm` runners (6 RIDs total). No qemu needed.
- [ ] Decide fate of `netstandard2.1` TFM on XetSharp (resolver is net5+-only there; see README).

## 4. Decisions & why (don't relitigate blindly)

| Decision | Why |
|---|---|
| `panic = "unwind"`, never `"abort"` | The FFI relies on `catch_unwind`; abort would kill the CLR host process on any Rust panic |
| Split NuGet (meta + per-RID runtime pkgs + `runtime.json`) | User requirement: a linux-x64 consumer must not download arm64 bytes. Universal package rejected for size |
| `netstandard2.0` TFM on runtime packages | Inert placeholder — csproj needs *a* TFM; `IncludeBuildOutput=false` ships no managed code; natives resolve by RID |
| Windows built on `windows-latest` + MSVC (not zigbuild) | `aws-lc-sys` under mingw/zig is flaky; MSVC + NASM is the supported path, and it gave us win-arm64 too |
| Token refresh via C function pointers + `GCHandle` state | Only refresh mechanism that works across FFI; mirrors Python's `WrappedTokenRefresher` |
| Local CAS mode used for tests | Identical chunk/dedup/serialize engine as remote, zero network — fast, deterministic CI |

## 5. Verification ritual (before claiming anything works)

```bash
docker exec <container> bash -lc 'cd /workspaces/XetSharp && cargo build && dotnet test XetSharp.Tests/XetSharp.Tests.csproj'
docker exec <container> bash -lc 'cd /workspaces/XetSharp && dotnet run --project XetSharp.Example'   # round-trip demo
```
Green means: 8 FFI tests pass AND the demo prints `round-trip bytes identical: True`.
For CI: `gh run list -R N-E-W-T-O-N/XetSharp`, then `gh run download <id>` — the pack job must
produce **meta + one runtime package per built RID** (a lone 10 KB meta package = the natives
didn't stage; that failure mode has happened and is guarded by the verify step).

## 6. Known sharp edges (details in the skill)

XML `--` in csproj comments; `NativeMethods.g.cs` is generated (never hand-edit); the token
refresher delegate must be synchronous (CS4010); invalid remote endpoints retry for many minutes
(use empty-token fast-fail in tests); artifact-upload path stripping in CI; `target/` loses exec
bits if the tree is moved across filesystems (delete `target/`, rebuild).
