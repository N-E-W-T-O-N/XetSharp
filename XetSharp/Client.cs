using System;
using System.Runtime.InteropServices;

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
    /// Managed entry point to the xet-core chunking / deduplication / CAS-write engine.
    /// </summary>
    public sealed class XetClient
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
        public unsafe string UploadFile(string casDirectory, string filePath)
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
        /// <param name="endpoint">CAS endpoint URL.</param>
        /// <param name="token">Bearer token used as-is until <paramref name="tokenExpiration"/>.</param>
        /// <param name="filePath">Path of the file to upload.</param>
        /// <param name="repo">Optional repo scope; pass null/empty when not required.</param>
        /// <param name="tokenExpiration">Token expiry in epoch seconds; 0 means "no expiry".</param>
        /// <returns>The same JSON result document as <see cref="UploadFile"/>.</returns>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public unsafe string UploadFileRemote(
            string endpoint,
            string token,
            string filePath,
            string? repo = null,
            ulong tokenExpiration = 0)
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
                    (byte*)endpointPtr, (byte*)tokenPtr, tokenExpiration, (byte*)repoPtr, (byte*)filePtr, &result);
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

        /// <summary>
        /// Reconstructs the file identified by <paramref name="hash"/> from the local CAS directory
        /// <paramref name="casDirectory"/> into <paramref name="destPath"/>.
        /// </summary>
        /// <param name="fileSize">Known size in bytes, or 0 if unknown.</param>
        /// <returns>A JSON document of the form <c>{"bytes_written":N}</c>.</returns>
        /// <exception cref="XetException">The native engine reported a failure.</exception>
        public unsafe string DownloadFile(string casDirectory, string hash, string destPath, ulong fileSize = 0)
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
        public unsafe string DownloadFileRemote(
            string endpoint,
            string token,
            string hash,
            string destPath,
            string? repo = null,
            ulong tokenExpiration = 0,
            ulong fileSize = 0)
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
                    (byte*)hashPtr, fileSize, (byte*)destPtr, &result);
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

        /// <summary>
        /// Routes xet-core's tracing output to <paramref name="logPath"/>. Call once at startup.
        /// The log level is controlled by the <c>XET_LOG</c> environment variable (default <c>info</c>).
        /// </summary>
        /// <exception cref="XetException">The native engine failed to initialize logging.</exception>
        public unsafe void InitLogging(string logPath)
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

        /// <summary>Turns a native (code, message) pair into a result string or an exception.</summary>
        private static unsafe string Decode(int code, byte* result)
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
