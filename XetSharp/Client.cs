using System;
using System.Runtime.InteropServices;
#if NET5_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace XetSharp
{
    /// <summary>
    /// Raised when the native xet engine returns a non-zero status code.
    /// </summary>
    public sealed class XetException : Exception
    {
        /// <summary>Native return code (1 = error, 2 = panic caught in native code).</summary>
        public int Code { get; }

        public XetException(int code, string message)
            : base($"xet native error ({code}): {message}")
        {
            Code = code;
        }
    }

    /// <summary>
    /// Produces a fresh CAS token. Invoked by the native engine when the current token is at or near
    /// its expiry, so long-running sessions can keep authenticating without restarting.
    /// </summary>
    /// <returns>The new token and its expiry as epoch seconds (0 = no expiry).</returns>
    public delegate (string Token, long ExpirationEpochSeconds) TokenRefreshCallback();

    /// <summary>
    /// Managed entry point to the xet-core chunking / deduplication / CAS-write engine.
    /// </summary>
    public sealed unsafe class XetClient
    {
        /// <summary>
        /// Chunks <paramref name="filePath"/> with content-defined chunking, deduplicates it,
        /// and writes the resulting xorbs and shard into the local CAS directory
        /// <paramref name="casDirectory"/> (created if it does not exist).
        /// </summary>
        /// <returns>
        /// A JSON document describing the uploaded file (Merkle hash, size, sha256) and the
        /// deduplication metrics for the operation.
        /// </returns>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public string UploadFile(string casDirectory, string filePath)
        {
            if (casDirectory is null) throw new ArgumentNullException(nameof(casDirectory));
            if (filePath is null) throw new ArgumentNullException(nameof(filePath));

            IntPtr casPtr = Marshal.StringToCoTaskMemUTF8(casDirectory);
            IntPtr filePtr = Marshal.StringToCoTaskMemUTF8(filePath);
            byte* result = null;
            try
            {
                int code = NativeMethods.xet_upload_file((byte*)casPtr, (byte*)filePtr, &result);
                return Decode(code, result);
            }
            finally
            {
                if (result != null) NativeMethods.xet_string_free(result);
                Marshal.FreeCoTaskMem(casPtr);
                Marshal.FreeCoTaskMem(filePtr);
            }
        }

        /// <summary>
        /// Chunks and deduplicates <paramref name="filePath"/> and uploads it to a remote CAS
        /// endpoint (e.g. the HuggingFace Hub) authenticated with <paramref name="token"/>.
        /// </summary>
        /// <param name="tokenExpiration">Token expiry in epoch seconds; 0 means "no expiry".</param>
        /// <returns>The same JSON result document as <see cref="UploadFile"/>.</returns>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public string UploadFileRemote(
            string endpoint,
            string token,
            string filePath,
            string? repo = null,
            ulong tokenExpiration = 0)
        {
            return UploadFileRemoteCore(endpoint, token, filePath, repo, tokenExpiration, null, null, null);
        }

        /// <summary>
        /// Reconstructs the file identified by <paramref name="hash"/> from the local CAS directory
        /// <paramref name="casDirectory"/> into <paramref name="destPath"/>.
        /// </summary>
        /// <param name="fileSize">Known size in bytes, or 0 if unknown.</param>
        /// <returns>A JSON document of the form <c>{"bytes_written":N}</c>.</returns>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public string DownloadFile(string casDirectory, string hash, string destPath, ulong fileSize = 0)
        {
            if (casDirectory is null) throw new ArgumentNullException(nameof(casDirectory));
            if (hash is null) throw new ArgumentNullException(nameof(hash));
            if (destPath is null) throw new ArgumentNullException(nameof(destPath));

            IntPtr casPtr = Marshal.StringToCoTaskMemUTF8(casDirectory);
            IntPtr hashPtr = Marshal.StringToCoTaskMemUTF8(hash);
            IntPtr destPtr = Marshal.StringToCoTaskMemUTF8(destPath);
            byte* result = null;
            try
            {
                int code = NativeMethods.xet_download_file(
                    (byte*)casPtr, (byte*)hashPtr, fileSize, (byte*)destPtr, &result);
                return Decode(code, result);
            }
            finally
            {
                if (result != null) NativeMethods.xet_string_free(result);
                Marshal.FreeCoTaskMem(casPtr);
                Marshal.FreeCoTaskMem(hashPtr);
                Marshal.FreeCoTaskMem(destPtr);
            }
        }

        /// <summary>
        /// Reconstructs the file identified by <paramref name="hash"/> from a remote CAS
        /// <paramref name="endpoint"/> (e.g. the HuggingFace Hub) into <paramref name="destPath"/>.
        /// </summary>
        /// <returns>A JSON document of the form <c>{"bytes_written":N}</c>.</returns>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public string DownloadFileRemote(
            string endpoint,
            string token,
            string hash,
            string destPath,
            string? repo = null,
            ulong tokenExpiration = 0,
            ulong fileSize = 0)
        {
            return DownloadFileRemoteCore(endpoint, token, hash, destPath, repo, tokenExpiration, fileSize, null, null, null);
        }

        /// <summary>
        /// Routes xet-core's tracing output to <paramref name="logPath"/>. Call once at startup.
        /// The log level is controlled by the <c>XET_LOG</c> environment variable (default <c>info</c>).
        /// </summary>
        /// <exception cref="XetException">The native engine failed to initialize logging.</exception>
        public void InitLogging(string logPath)
        {
            if (logPath is null) throw new ArgumentNullException(nameof(logPath));

            IntPtr pathPtr = Marshal.StringToCoTaskMemUTF8(logPath);
            byte* result = null;
            try
            {
                int code = NativeMethods.xet_init_logging((byte*)pathPtr, &result);
                Decode(code, result);
            }
            finally
            {
                if (result != null) NativeMethods.xet_string_free(result);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

#if NET5_0_OR_GREATER
        /// <summary>
        /// Uploads to a remote CAS with automatic token refresh: <paramref name="tokenRefresh"/> is
        /// invoked by the native engine whenever the current token nears expiry.
        /// </summary>
        /// <param name="token">Initial token (may be empty if the refresher provides the first one).</param>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public string UploadFileRemote(
            string endpoint,
            string token,
            string filePath,
            TokenRefreshCallback tokenRefresh,
            string? repo = null,
            ulong tokenExpiration = 0)
        {
            if (tokenRefresh is null) throw new ArgumentNullException(nameof(tokenRefresh));
            var handle = GCHandle.Alloc(tokenRefresh);
            try
            {
                return UploadFileRemoteCore(endpoint, token, filePath, repo, tokenExpiration,
                    &RefreshThunk, &FreeTokenThunk, (void*)GCHandle.ToIntPtr(handle));
            }
            finally { handle.Free(); }
        }

        /// <summary>
        /// Downloads from a remote CAS with automatic token refresh (see
        /// <see cref="UploadFileRemote(string,string,string,TokenRefreshCallback,string,ulong)"/>).
        /// </summary>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public string DownloadFileRemote(
            string endpoint,
            string token,
            string hash,
            string destPath,
            TokenRefreshCallback tokenRefresh,
            string? repo = null,
            ulong tokenExpiration = 0,
            ulong fileSize = 0)
        {
            if (tokenRefresh is null) throw new ArgumentNullException(nameof(tokenRefresh));
            var handle = GCHandle.Alloc(tokenRefresh);
            try
            {
                return DownloadFileRemoteCore(endpoint, token, hash, destPath, repo, tokenExpiration, fileSize,
                    &RefreshThunk, &FreeTokenThunk, (void*)GCHandle.ToIntPtr(handle));
            }
            finally { handle.Free(); }
        }

        // Native calls this (via a function pointer) to obtain a fresh token. Recovers the managed
        // delegate from the GCHandle passed as `state`, invokes it, and hands back a UTF-8 copy the
        // native side later releases via FreeTokenThunk. Never lets a managed exception escape.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int RefreshThunk(void* state, byte** outToken, ulong* outExpiry)
        {
            try
            {
                var callback = (TokenRefreshCallback)GCHandle.FromIntPtr((IntPtr)state).Target!;
                (string token, long expiration) = callback();
                *outToken = (byte*)Marshal.StringToCoTaskMemUTF8(token ?? string.Empty);
                *outExpiry = (ulong)expiration;
                return 0;
            }
            catch
            {
                *outToken = null;
                return 1;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void FreeTokenThunk(byte* ptr) => Marshal.FreeCoTaskMem((IntPtr)ptr);
#endif

        private string UploadFileRemoteCore(
            string endpoint, string token, string filePath, string? repo, ulong tokenExpiration,
            delegate* unmanaged[Cdecl]<void*, byte**, ulong*, int> refreshCb,
            delegate* unmanaged[Cdecl]<byte*, void> freeCb, void* state)
        {
            if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
            if (token is null) throw new ArgumentNullException(nameof(token));
            if (filePath is null) throw new ArgumentNullException(nameof(filePath));

            IntPtr endpointPtr = Marshal.StringToCoTaskMemUTF8(endpoint);
            IntPtr tokenPtr = Marshal.StringToCoTaskMemUTF8(token);
            IntPtr repoPtr = Marshal.StringToCoTaskMemUTF8(repo ?? string.Empty);
            IntPtr filePtr = Marshal.StringToCoTaskMemUTF8(filePath);
            byte* result = null;
            try
            {
                int code = NativeMethods.xet_upload_file_remote(
                    (byte*)endpointPtr, (byte*)tokenPtr, tokenExpiration, (byte*)repoPtr, (byte*)filePtr,
                    refreshCb, freeCb, state, &result);
                return Decode(code, result);
            }
            finally
            {
                if (result != null) NativeMethods.xet_string_free(result);
                Marshal.FreeCoTaskMem(endpointPtr);
                Marshal.FreeCoTaskMem(tokenPtr);
                Marshal.FreeCoTaskMem(repoPtr);
                Marshal.FreeCoTaskMem(filePtr);
            }
        }

        private string DownloadFileRemoteCore(
            string endpoint, string token, string hash, string destPath, string? repo,
            ulong tokenExpiration, ulong fileSize,
            delegate* unmanaged[Cdecl]<void*, byte**, ulong*, int> refreshCb,
            delegate* unmanaged[Cdecl]<byte*, void> freeCb, void* state)
        {
            if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
            if (token is null) throw new ArgumentNullException(nameof(token));
            if (hash is null) throw new ArgumentNullException(nameof(hash));
            if (destPath is null) throw new ArgumentNullException(nameof(destPath));

            IntPtr endpointPtr = Marshal.StringToCoTaskMemUTF8(endpoint);
            IntPtr tokenPtr = Marshal.StringToCoTaskMemUTF8(token);
            IntPtr repoPtr = Marshal.StringToCoTaskMemUTF8(repo ?? string.Empty);
            IntPtr hashPtr = Marshal.StringToCoTaskMemUTF8(hash);
            IntPtr destPtr = Marshal.StringToCoTaskMemUTF8(destPath);
            byte* result = null;
            try
            {
                int code = NativeMethods.xet_download_file_remote(
                    (byte*)endpointPtr, (byte*)tokenPtr, tokenExpiration, (byte*)repoPtr,
                    (byte*)hashPtr, fileSize, (byte*)destPtr, refreshCb, freeCb, state, &result);
                return Decode(code, result);
            }
            finally
            {
                if (result != null) NativeMethods.xet_string_free(result);
                Marshal.FreeCoTaskMem(endpointPtr);
                Marshal.FreeCoTaskMem(tokenPtr);
                Marshal.FreeCoTaskMem(repoPtr);
                Marshal.FreeCoTaskMem(hashPtr);
                Marshal.FreeCoTaskMem(destPtr);
            }
        }

        /// <summary>Turns a native (code, message) pair into a result string or an exception.</summary>
        private static string Decode(int code, byte* result)
        {
            string message = result != null
                ? Marshal.PtrToStringUTF8((IntPtr)result) ?? string.Empty
                : string.Empty;

            if (code != 0)
                throw new XetException(code, message);

            return message;
        }
    }
}
