using BasicFtpServer.Core.Config;

namespace BasicFtpServer.Core.Files;

public sealed record UploadResult(bool Success, string FinalPath, long BytesWritten, string? Error);

/// <summary>
/// Receives an upload to disk.
///
/// Two things here exist purely because of how these files get consumed downstream:
/// the .part staging file (so a folder watcher never picks up a half-written scan), and
/// the retry around the final rename (Defender's real-time scan can hold a brand-new file
/// open for a beat after the last write, producing a spurious sharing violation).
/// </summary>
public static class UploadWriter
{
    private const int RenameAttempts = 5;
    private const int RenameBackoffMs = 120;

    public static async Task<UploadResult> ReceiveAsync(
        Stream source,
        string targetPhysical,
        CompatibilitySettings compat,
        bool append,
        long restartOffset,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPhysical);
        if (!string.IsNullOrEmpty(directory))
        {
            if (!Directory.Exists(directory))
            {
                if (!compat.AutoCreateDirectories)
                {
                    return new UploadResult(false, targetPhysical, 0, "Destination directory does not exist.");
                }

                Directory.CreateDirectory(directory);
            }
        }

        var resumingOrAppending = append || restartOffset > 0;
        var finalPath = targetPhysical;

        if (!resumingOrAppending && File.Exists(finalPath))
        {
            switch (compat.DuplicatePolicy)
            {
                case DuplicatePolicy.Reject:
                    return new UploadResult(false, finalPath, 0, "File already exists.");
                case DuplicatePolicy.Rename:
                    finalPath = NextAvailableName(finalPath);
                    break;
                case DuplicatePolicy.Overwrite:
                    break;
            }
        }

        // Staging only makes sense for a fresh transfer; appending has to touch the real file.
        var useStaging = compat.WriteToPartFile && !resumingOrAppending;
        var writePath = useStaging ? StagingPathFor(finalPath) : finalPath;

        long written = 0;
        try
        {
            var mode = resumingOrAppending ? FileMode.OpenOrCreate : FileMode.Create;

            await using (var target = new FileStream(
                writePath, mode, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                if (append)
                {
                    target.Seek(0, SeekOrigin.End);
                }
                else if (restartOffset > 0)
                {
                    target.Seek(Math.Min(restartOffset, target.Length), SeekOrigin.Begin);
                }

                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (useStaging)
            {
                MoveWithRetry(writePath, finalPath, compat.DuplicatePolicy);
            }

            return new UploadResult(true, finalPath, written, null);
        }
        catch (Exception ex)
        {
            if (useStaging)
            {
                TryDelete(writePath);
            }

            var message = ex switch
            {
                IOException io when IsDiskFull(io) => "Insufficient storage space on the server.",
                UnauthorizedAccessException => "Permission denied writing to the destination folder.",
                OperationCanceledException => "Transfer aborted.",
                _ => ex.Message,
            };

            return new UploadResult(false, finalPath, written, message);
        }
    }

    private static string StagingPathFor(string finalPath)
    {
        var candidate = finalPath + ".part";
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{finalPath}.{counter++}.part";
        }

        return candidate;
    }

    /// <summary>Turns "scan.pdf" into "scan (1).pdf", "scan (2).pdf", ...</summary>
    public static string NextAvailableName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    private static void MoveWithRetry(string from, string to, DuplicatePolicy policy)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // The target can appear between the pre-flight check and here if two copiers
                // scan the same name at once.
                if (File.Exists(to) && policy == DuplicatePolicy.Rename)
                {
                    to = NextAvailableName(to);
                }

                File.Move(from, to, overwrite: policy == DuplicatePolicy.Overwrite);
                return;
            }
            catch (IOException) when (attempt < RenameAttempts)
            {
                Thread.Sleep(RenameBackoffMs * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stranded .part file is untidy but harmless; never mask the original error.
        }
    }

    private static bool IsDiskFull(IOException ex)
    {
        const int ErrorDiskFull = 0x70;
        const int ErrorHandleDiskFull = 0x27;
        var code = ex.HResult & 0xFFFF;
        return code is ErrorDiskFull or ErrorHandleDiskFull;
    }
}
