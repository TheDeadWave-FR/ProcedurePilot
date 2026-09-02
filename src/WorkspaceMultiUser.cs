using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ProcedurePilot
{
    public sealed class WorkspaceWriteLease : IDisposable
    {
        private FileStream stream;

        internal WorkspaceWriteLease(FileStream stream, string lockPath, string ownerToken, DateTime acquiredAtUtc)
        {
            this.stream = stream;
            LockPath = lockPath;
            OwnerToken = ownerToken;
            AcquiredAtUtc = acquiredAtUtc;
        }

        public string LockPath { get; private set; }

        public string OwnerToken { get; private set; }

        public DateTime AcquiredAtUtc { get; private set; }

        public bool IsDisposed
        {
            get { return stream == null; }
        }

        public void Dispose()
        {
            FileStream ownedStream = Interlocked.Exchange<FileStream>(ref stream, null);
            if (ownedStream != null) ownedStream.Dispose();
        }
    }

    public static class WorkspaceMultiUser
    {
        public const string LockFileName = ".procedurepilot-write.lock";
        public const string ChangeFileName = ".procedurepilot-change";
        public const long SmallFileHashLimitBytes = 4L * 1024L * 1024L;

        private const int LockRetryDelayMilliseconds = 75;
        private const int ReadRetryCount = 4;
        private const int PublishRetryCount = 8;

        public static WorkspaceWriteLease TryAcquire(string dataFolder, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(
                    "timeoutMilliseconds",
                    UiText.Get("Le délai du verrou est invalide.", "The lock timeout is invalid."));

            string folder = ResolveDataFolder(dataFolder);
            Directory.CreateDirectory(folder);
            string lockPath = Path.Combine(folder, LockFileName);
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    FileStream lockStream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        4096,
                        FileOptions.None);
                    try
                    {
                        string ownerToken = Guid.NewGuid().ToString("N");
                        DateTime acquiredAtUtc = DateTime.UtcNow;
                        WriteMetadata(lockStream, ownerToken, "write-lock", acquiredAtUtc);
                        return new WorkspaceWriteLease(lockStream, lockPath, ownerToken, acquiredAtUtc);
                    }
                    catch
                    {
                        lockStream.Dispose();
                        throw;
                    }
                }
                catch (IOException ex)
                {
                    if (!IsSharingViolation(ex)) throw;
                    if (HasTimedOut(stopwatch, timeoutMilliseconds)) return null;
                    Thread.Sleep(GetRetryDelay(stopwatch, timeoutMilliseconds));
                }
            }
        }

        public static WorkspaceWriteLease Acquire(string dataFolder, int timeoutMilliseconds)
        {
            WorkspaceWriteLease lease = TryAcquire(dataFolder, timeoutMilliseconds);
            if (lease != null) return lease;

            throw new TimeoutException(UiText.Get(
                "Un autre utilisateur modifie actuellement cet espace de travail. Réessayez dans quelques instants.",
                "Another user is currently modifying this workspace. Please try again in a moment."));
        }

        public static string PublishChange(string dataFolder, string kind)
        {
            string folder = ResolveDataFolder(dataFolder);
            Directory.CreateDirectory(folder);

            string token = Guid.NewGuid().ToString("N");
            string destinationPath = Path.Combine(folder, ChangeFileName);
            string temporaryPath = Path.Combine(folder, ".procedurepilot-change-" + token + ".tmp");
            byte[] contents = Encoding.UTF8.GetBytes(BuildMetadata(token, kind, DateTime.UtcNow));

            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.None))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                Exception lastError = null;
                for (int attempt = 0; attempt < PublishRetryCount; attempt++)
                {
                    try
                    {
                        if (File.Exists(destinationPath))
                        {
                            try { File.Replace(temporaryPath, destinationPath, null, true); }
                            catch (IOException)
                            {
                                File.Copy(temporaryPath, destinationPath, true);
                                File.Delete(temporaryPath);
                            }
                        }
                        else
                            File.Move(temporaryPath, destinationPath);
                        return token;
                    }
                    catch (IOException ex)
                    {
                        lastError = ex;
                        if (!File.Exists(temporaryPath))
                        {
                            if (string.Equals(ReadChangeToken(dataFolder), token, StringComparison.Ordinal)) return token;
                            throw;
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        lastError = ex;
                    }

                    if (attempt < PublishRetryCount - 1) Thread.Sleep(25 * (attempt + 1));
                }

                if (lastError != null) throw lastError;
                throw new IOException(UiText.Get(
                    "Impossible de publier le changement partagé.",
                    "Unable to publish the shared change."));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        public static string ReadChangeToken(string dataFolder)
        {
            string folder = ResolveDataFolder(dataFolder);
            string path = Path.Combine(folder, ChangeFileName);

            for (int attempt = 0; attempt < ReadRetryCount; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return string.Empty;
                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        for (int lineNumber = 0; lineNumber < 16 && !reader.EndOfStream; lineNumber++)
                        {
                            string line = reader.ReadLine();
                            if (line == null || !line.StartsWith("Token=", StringComparison.Ordinal)) continue;
                            Guid parsedToken;
                            if (Guid.TryParseExact(line.Substring(6).Trim(), "N", out parsedToken))
                                return parsedToken.ToString("N");
                            break;
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    return string.Empty;
                }
                catch (DirectoryNotFoundException)
                {
                    return string.Empty;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                if (attempt < ReadRetryCount - 1) Thread.Sleep(20 * (attempt + 1));
            }

            return string.Empty;
        }

        public static string GetFileStamp(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (ArgumentException) { return string.Empty; }
            catch (NotSupportedException) { return string.Empty; }
            catch (PathTooLongException) { return string.Empty; }

            for (int attempt = 0; attempt < ReadRetryCount; attempt++)
            {
                try
                {
                    if (!File.Exists(fullPath)) return string.Empty;

                    FileInfo before = new FileInfo(fullPath);
                    long expectedLength = before.Length;
                    long expectedWriteTicks = before.LastWriteTimeUtc.Ticks;

                    using (var stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        long openedLength = stream.Length;
                        if (openedLength <= SmallFileHashLimitBytes)
                        {
                            byte[] hash;
                            using (SHA256 algorithm = SHA256.Create())
                                hash = algorithm.ComputeHash(stream);

                            FileInfo after = new FileInfo(fullPath);
                            after.Refresh();
                            if (openedLength != expectedLength
                                || stream.Length != openedLength
                                || stream.Position != openedLength
                                || !after.Exists
                                || after.Length != expectedLength
                                || after.LastWriteTimeUtc.Ticks != expectedWriteTicks)
                            {
                                if (attempt < ReadRetryCount - 1) Thread.Sleep(20 * (attempt + 1));
                                continue;
                            }

                            return "sha256:" + openedLength.ToString(CultureInfo.InvariantCulture)
                                + ":" + BytesToHex(hash);
                        }

                        FileInfo largeAfter = new FileInfo(fullPath);
                        largeAfter.Refresh();
                        if (openedLength != expectedLength
                            || !largeAfter.Exists
                            || largeAfter.Length != expectedLength
                            || largeAfter.LastWriteTimeUtc.Ticks != expectedWriteTicks)
                        {
                            if (attempt < ReadRetryCount - 1) Thread.Sleep(20 * (attempt + 1));
                            continue;
                        }

                        return "file:" + openedLength.ToString(CultureInfo.InvariantCulture)
                            + ":" + expectedWriteTicks.ToString(CultureInfo.InvariantCulture);
                    }
                }
                catch (FileNotFoundException)
                {
                    return string.Empty;
                }
                catch (DirectoryNotFoundException)
                {
                    return string.Empty;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                if (attempt < ReadRetryCount - 1) Thread.Sleep(20 * (attempt + 1));
            }

            return string.Empty;
        }

        private static string ResolveDataFolder(string dataFolder)
        {
            if (string.IsNullOrWhiteSpace(dataFolder))
                throw new ArgumentException(
                    UiText.Get("Le dossier des données partagées est invalide.", "The shared data folder is invalid."),
                    "dataFolder");
            return Path.GetFullPath(dataFolder.Trim());
        }

        private static void WriteMetadata(FileStream stream, string token, string kind, DateTime utc)
        {
            byte[] contents = Encoding.UTF8.GetBytes(BuildMetadata(token, kind, utc));
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(contents, 0, contents.Length);
            stream.Flush(true);
        }

        private static string BuildMetadata(string token, string kind, DateTime utc)
        {
            int processId = 0;
            string processName = string.Empty;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    processId = process.Id;
                    processName = process.ProcessName;
                }
            }
            catch
            {
            }

            var metadata = new StringBuilder();
            metadata.AppendLine("Version=1");
            metadata.AppendLine("Token=" + CleanMetadataValue(token));
            metadata.AppendLine("Kind=" + CleanMetadataValue(kind));
            metadata.AppendLine("Machine=" + CleanMetadataValue(Environment.MachineName));
            metadata.AppendLine("User=" + CleanMetadataValue(GetUserName()));
            metadata.AppendLine("ProcessId=" + processId.ToString(CultureInfo.InvariantCulture));
            metadata.AppendLine("ProcessName=" + CleanMetadataValue(processName));
            metadata.AppendLine("Utc=" + utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            return metadata.ToString();
        }

        private static string GetUserName()
        {
            string user = Environment.UserName;
            string domain = Environment.UserDomainName;
            if (string.IsNullOrWhiteSpace(domain)) return user ?? string.Empty;
            if (string.IsNullOrWhiteSpace(user)) return domain;
            return domain + "\\" + user;
        }

        private static string CleanMetadataValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static bool IsSharingViolation(IOException exception)
        {
            int errorCode = exception.HResult & 0xFFFF;
            return errorCode == 32 || errorCode == 33;
        }

        private static bool HasTimedOut(Stopwatch stopwatch, int timeoutMilliseconds)
        {
            return timeoutMilliseconds != Timeout.Infinite
                && stopwatch.ElapsedMilliseconds >= timeoutMilliseconds;
        }

        private static int GetRetryDelay(Stopwatch stopwatch, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds == Timeout.Infinite) return LockRetryDelayMilliseconds;
            long remaining = timeoutMilliseconds - stopwatch.ElapsedMilliseconds;
            if (remaining <= 0) return 1;
            return (int)Math.Min(LockRetryDelayMilliseconds, remaining);
        }

        private static string BytesToHex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
                result.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }
}
