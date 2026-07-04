//! C-ABI shim exposing xet-core's chunking / deduplication / CAS-write engine to .NET.
//!
//! The binding surface is intentionally small and follows three rules that make FFI safe:
//!   1. No Rust panic may cross the boundary  -> every export wraps its body in `catch_unwind`.
//!   2. Async is bridged to sync at the edge   -> we drive futures with `runtime.bridge_sync`.
//!   3. Ownership of returned strings is explicit -> Rust allocates, C# must call `xet_string_free`.
//!
//! Upload and download are supported against both a local CAS directory and a remote CAS endpoint
//! (e.g. the HuggingFace Hub), sharing the identical chunking/dedup pipeline:
//!   * `xet_upload_file`   / `xet_upload_file_remote`   -> chunk + dedup + store.
//!   * `xet_download_file` / `xet_download_file_remote` -> reconstruct a file from its Merkle hash.
//!
//! `xet_init_logging` routes xet-core's `tracing` output to a file so callers can follow along.
//!
//! Remote operations use the caller-supplied token directly; automatic token *refresh* (a C#
//! callback marshalled across FFI) is a planned follow-up, only needed for sessions that outlive
//! the token's expiry — see README.

use std::ffi::{c_char, c_int, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::sync::{Arc, OnceLock};

use anyhow::{anyhow, bail, Context, Result};
use serde_json::json;
use xet_client::cas_client::auth::AuthConfig;
use xet_data::processing::configurations::{SessionContext, TranslatorConfig};
use xet_data::processing::{FileDownloadSession, FileUploadSession, Sha256Policy, XetFileInfo};
use xet_runtime::core::XetContext;

/// Routes xet-core's `tracing` output to `log_path`. Safe to call once; later calls are no-ops.
/// Level is controlled by the `XET_LOG` env var (default `info`).
fn init_logging(log_path: &str) -> Result<()> {
    use tracing_subscriber::EnvFilter;

    // Keep the non-blocking writer's worker guard alive for the process lifetime.
    static GUARD: OnceLock<tracing_appender::non_blocking::WorkerGuard> = OnceLock::new();

    let file = std::fs::File::create(log_path)
        .with_context(|| format!("creating log file {log_path}"))?;
    let (writer, guard) = tracing_appender::non_blocking(file);
    let _ = GUARD.set(guard);

    let filter = EnvFilter::try_from_env("XET_LOG").unwrap_or_else(|_| EnvFilter::new("info"));
    // try_init fails if a global subscriber is already installed; that's fine — treat as success.
    let _ = tracing_subscriber::fmt()
        .with_ansi(false)
        .with_writer(writer)
        .with_env_filter(filter)
        .try_init();
    Ok(())
}

/// Process-wide xet context (tokio thread pool + config), created once on first use.
fn xet_context() -> Result<XetContext> {
    static CTX: OnceLock<XetContext> = OnceLock::new();
    if let Some(ctx) = CTX.get() {
        return Ok(ctx.clone());
    }
    let ctx = XetContext::default().map_err(|e| anyhow!("failed to start xet runtime: {e}"))?;
    // Ignore the race where another thread set it first; either value is equivalent.
    let _ = CTX.set(ctx);
    Ok(CTX.get().expect("context set above").clone())
}

/// Drives the full clean/upload pipeline for a single file, using a caller-provided closure to
/// build the [`TranslatorConfig`] (which decides local vs. remote target). Returns a JSON document
/// describing the resulting Xet file info and dedup metrics.
fn run_upload<F>(build_config: F, file_path: PathBuf) -> Result<String>
where
    F: FnOnce(&XetContext) -> Result<TranslatorConfig> + Send + 'static,
{
    let ctx = xet_context()?;
    if !file_path.is_file() {
        bail!("file not found: {}", file_path.display());
    }

    let inner_ctx = ctx.clone();
    let work = async move {
        let config = Arc::new(build_config(&inner_ctx)?);
        let session = FileUploadSession::new(config)
            .await
            .context("creating upload session")?;

        let infos = session
            .upload_files(vec![(file_path, Sha256Policy::Compute)])
            .await
            .context("chunking/uploading file")?;

        let metrics = session.finalize().await.context("finalizing session")?;

        let info = infos
            .into_iter()
            .next()
            .ok_or_else(|| anyhow!("upload returned no file info"))?;

        let result = json!({
            "hash": info.hash,
            "file_size": info.file_size,
            "sha256": info.sha256,
            "metrics": {
                "total_bytes": metrics.total_bytes,
                "new_bytes": metrics.new_bytes,
                "deduped_bytes": metrics.deduped_bytes,
                "total_chunks": metrics.total_chunks,
                "new_chunks": metrics.new_chunks,
                "deduped_chunks": metrics.deduped_chunks,
                "xorb_bytes_uploaded": metrics.xorb_bytes_uploaded,
                "shard_bytes_uploaded": metrics.shard_bytes_uploaded,
                "total_bytes_uploaded": metrics.total_bytes_uploaded,
            }
        });
        Ok::<String, anyhow::Error>(result.to_string())
    };

    ctx.runtime
        .bridge_sync(work)
        .map_err(|e| anyhow!("runtime error: {e}"))?
}

/// Uploads a file into a local CAS directory.
fn upload_file_local(cas_dir: &str, file_path: &str) -> Result<String> {
    let cas_dir = PathBuf::from(cas_dir);
    std::fs::create_dir_all(&cas_dir)
        .with_context(|| format!("creating CAS directory {}", cas_dir.display()))?;

    run_upload(
        move |ctx| TranslatorConfig::local_config(ctx, &cas_dir).context("building local TranslatorConfig"),
        PathBuf::from(file_path),
    )
}

/// Builds a `SessionContext` for a remote CAS endpoint authenticated with `token`.
///
/// `token_expiration` is epoch seconds; `0` means "no expiry" (the token is used as-is).
/// `repo` may be empty when the endpoint does not require a repo scope.
fn remote_session(endpoint: &str, token: &str, token_expiration: u64, repo: &str) -> Result<SessionContext> {
    if endpoint.is_empty() {
        bail!("remote endpoint must not be empty");
    }
    if token.is_empty() {
        bail!("auth token must not be empty");
    }

    // With a token but no refresher, xet-core uses the token as-is until `token_expiration`.
    let expiry = if token_expiration == 0 { None } else { Some(token_expiration) };
    let auth = AuthConfig::maybe_new(Some(token.to_string()), expiry, None);

    Ok(SessionContext {
        endpoint: endpoint.to_string(),
        auth,
        custom_headers: None,
        repo_paths: if repo.is_empty() { vec!["".into()] } else { vec![repo.to_string()] },
        session_id: None,
    })
}

/// Uploads a file to a remote CAS endpoint (e.g. the HuggingFace Hub) authenticated with `token`.
fn upload_file_remote(
    endpoint: &str,
    token: &str,
    token_expiration: u64,
    repo: &str,
    file_path: &str,
) -> Result<String> {
    let session = remote_session(endpoint, token, token_expiration, repo)?;
    run_upload(
        move |ctx| TranslatorConfig::new(ctx, session).context("building remote TranslatorConfig"),
        PathBuf::from(file_path),
    )
}

/// Drives reconstruction of a single file (identified by its Merkle `hash`) to `dest`, using a
/// caller-provided closure to build the [`TranslatorConfig`]. Returns the number of bytes written.
fn run_download<F>(build_config: F, hash: String, file_size: u64, dest: PathBuf) -> Result<String>
where
    F: FnOnce(&XetContext) -> Result<TranslatorConfig> + Send + 'static,
{
    if hash.is_empty() {
        bail!("file hash must not be empty");
    }
    let ctx = xet_context()?;
    if let Some(parent) = dest.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("creating output directory {}", parent.display()))?;
    }

    let inner_ctx = ctx.clone();
    let work = async move {
        let config = Arc::new(build_config(&inner_ctx)?);
        let session = FileDownloadSession::new(config, None)
            .await
            .context("creating download session")?;

        let file_info = XetFileInfo {
            hash,
            file_size: if file_size == 0 { None } else { Some(file_size) },
            ..Default::default()
        };

        let (_id, n_bytes) = session
            .download_file(&file_info, &dest)
            .await
            .context("reconstructing file")?;

        Ok::<String, anyhow::Error>(json!({ "bytes_written": n_bytes }).to_string())
    };

    ctx.runtime
        .bridge_sync(work)
        .map_err(|e| anyhow!("runtime error: {e}"))?
}

/// Reconstructs a file from a local CAS directory.
fn download_file_local(cas_dir: &str, hash: &str, file_size: u64, dest_path: &str) -> Result<String> {
    let cas_dir = PathBuf::from(cas_dir);
    run_download(
        move |ctx| TranslatorConfig::local_config(ctx, &cas_dir).context("building local TranslatorConfig"),
        hash.to_string(),
        file_size,
        PathBuf::from(dest_path),
    )
}

/// Reconstructs a file from a remote CAS endpoint authenticated with `token`.
fn download_file_remote(
    endpoint: &str,
    token: &str,
    token_expiration: u64,
    repo: &str,
    hash: &str,
    file_size: u64,
    dest_path: &str,
) -> Result<String> {
    let session = remote_session(endpoint, token, token_expiration, repo)?;
    run_download(
        move |ctx| TranslatorConfig::new(ctx, session).context("building remote TranslatorConfig"),
        hash.to_string(),
        file_size,
        PathBuf::from(dest_path),
    )
}

// ---------------------------------------------------------------------------
// FFI boundary
// ---------------------------------------------------------------------------

/// Reads a NUL-terminated UTF-8 C string into an owned `String`.
///
/// # Safety
/// `ptr` must be null or a valid pointer to a NUL-terminated string.
unsafe fn read_cstr(ptr: *const c_char) -> Result<String> {
    if ptr.is_null() {
        bail!("null string argument");
    }
    Ok(CStr::from_ptr(ptr).to_str()?.to_owned())
}

/// Like [`read_cstr`] but treats a null pointer as an empty string.
///
/// # Safety
/// `ptr` must be null or a valid pointer to a NUL-terminated string.
unsafe fn read_cstr_or_empty(ptr: *const c_char) -> Result<String> {
    if ptr.is_null() {
        return Ok(String::new());
    }
    read_cstr(ptr)
}

/// Writes an owned C string into `*out` for the caller to later free via `xet_string_free`.
///
/// # Safety
/// `out` must be null or a valid pointer to writable `*mut c_char` storage.
unsafe fn write_out(out: *mut *mut c_char, s: &str) {
    if out.is_null() {
        return;
    }
    let c = CString::new(s).unwrap_or_else(|_| CString::new("<embedded NUL in message>").unwrap());
    *out = c.into_raw();
}

/// Turns a caught pipeline outcome into a status code + `*out_result` payload.
/// Return codes: 0 = ok, 1 = error (message in `*out_result`), 2 = panic caught.
fn finish(outcome: std::thread::Result<Result<String>>, out_result: *mut *mut c_char) -> c_int {
    match outcome {
        Ok(Ok(json)) => {
            unsafe { write_out(out_result, &json) };
            0
        }
        Ok(Err(err)) => {
            unsafe { write_out(out_result, &format!("{err:#}")) };
            1
        }
        Err(_) => {
            unsafe { write_out(out_result, "panic in native xet function") };
            2
        }
    }
}

/// Chunks and uploads `file_path` into the local CAS directory `cas_dir`.
///
/// The string written to `*out_result` is owned by the caller and MUST be released
/// with [`xet_string_free`]. See [`finish`] for return codes.
#[no_mangle]
pub extern "C" fn xet_upload_file(
    cas_dir: *const c_char,
    file_path: *const c_char,
    out_result: *mut *mut c_char,
) -> c_int {
    let outcome = catch_unwind(AssertUnwindSafe(|| {
        let cas_dir = unsafe { read_cstr(cas_dir) }?;
        let file_path = unsafe { read_cstr(file_path) }?;
        upload_file_local(&cas_dir, &file_path)
    }));
    finish(outcome, out_result)
}

/// Chunks and uploads `file_path` to a remote CAS `endpoint` authenticated with `token`.
///
/// `token_expiration` is epoch seconds (`0` = no expiry). `repo` may be null/empty.
/// The string written to `*out_result` is owned by the caller and MUST be released
/// with [`xet_string_free`]. See [`finish`] for return codes.
#[no_mangle]
pub extern "C" fn xet_upload_file_remote(
    endpoint: *const c_char,
    token: *const c_char,
    token_expiration: u64,
    repo: *const c_char,
    file_path: *const c_char,
    out_result: *mut *mut c_char,
) -> c_int {
    let outcome = catch_unwind(AssertUnwindSafe(|| {
        let endpoint = unsafe { read_cstr(endpoint) }?;
        let token = unsafe { read_cstr(token) }?;
        let repo = unsafe { read_cstr_or_empty(repo) }?;
        let file_path = unsafe { read_cstr(file_path) }?;
        upload_file_remote(&endpoint, &token, token_expiration, &repo, &file_path)
    }));
    finish(outcome, out_result)
}

/// Reconstructs the file with Merkle `hash` from the local CAS directory `cas_dir` into `dest_path`.
///
/// `file_size` may be `0` if unknown. On success `*out_result` holds `{"bytes_written":N}`.
/// The string written to `*out_result` is owned by the caller and MUST be released
/// with [`xet_string_free`]. See [`finish`] for return codes.
#[no_mangle]
pub extern "C" fn xet_download_file(
    cas_dir: *const c_char,
    hash: *const c_char,
    file_size: u64,
    dest_path: *const c_char,
    out_result: *mut *mut c_char,
) -> c_int {
    let outcome = catch_unwind(AssertUnwindSafe(|| {
        let cas_dir = unsafe { read_cstr(cas_dir) }?;
        let hash = unsafe { read_cstr(hash) }?;
        let dest_path = unsafe { read_cstr(dest_path) }?;
        download_file_local(&cas_dir, &hash, file_size, &dest_path)
    }));
    finish(outcome, out_result)
}

/// Reconstructs the file with Merkle `hash` from a remote CAS `endpoint` into `dest_path`.
///
/// `token_expiration` is epoch seconds (`0` = no expiry). `repo` may be null/empty.
/// `file_size` may be `0` if unknown. On success `*out_result` holds `{"bytes_written":N}`.
/// The string written to `*out_result` is owned by the caller and MUST be released
/// with [`xet_string_free`]. See [`finish`] for return codes.
#[no_mangle]
pub extern "C" fn xet_download_file_remote(
    endpoint: *const c_char,
    token: *const c_char,
    token_expiration: u64,
    repo: *const c_char,
    hash: *const c_char,
    file_size: u64,
    dest_path: *const c_char,
    out_result: *mut *mut c_char,
) -> c_int {
    let outcome = catch_unwind(AssertUnwindSafe(|| {
        let endpoint = unsafe { read_cstr(endpoint) }?;
        let token = unsafe { read_cstr(token) }?;
        let repo = unsafe { read_cstr_or_empty(repo) }?;
        let hash = unsafe { read_cstr(hash) }?;
        let dest_path = unsafe { read_cstr(dest_path) }?;
        download_file_remote(&endpoint, &token, token_expiration, &repo, &hash, file_size, &dest_path)
    }));
    finish(outcome, out_result)
}

/// Initializes file logging for xet-core's `tracing` output at `log_path`.
///
/// Idempotent; the log level is controlled by the `XET_LOG` env var (default `info`).
/// See [`finish`] for return codes; on error `*out_result` receives the message (may be null).
#[no_mangle]
pub extern "C" fn xet_init_logging(log_path: *const c_char, out_result: *mut *mut c_char) -> c_int {
    let outcome = catch_unwind(AssertUnwindSafe(|| {
        let log_path = unsafe { read_cstr(log_path) }?;
        init_logging(&log_path).map(|_| String::new())
    }));
    finish(outcome, out_result)
}

/// Frees a string previously returned by this library through an out-parameter.
///
/// # Safety
/// `ptr` must be null or a pointer obtained from a `xet_*` function in this library,
/// and must not be used after this call.
#[no_mangle]
pub extern "C" fn xet_string_free(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        drop(CString::from_raw(ptr));
    }
}
