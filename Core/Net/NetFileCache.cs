﻿using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Security.Permissions;
using ChinhDo.Transactions;
using ICSharpCode.SharpZipLib.Zip;
using log4net;
using CKAN.Extensions;

namespace CKAN
{

    /// <summary>
    /// A local cache dedicated to storing and retrieving files based upon their
    /// URL.
    /// </summary>

    // We require fancy permissions to use the FileSystemWatcher
    [PermissionSet(SecurityAction.Demand, Name="FullTrust")]
    public class NetFileCache : IDisposable
    {
        private FileSystemWatcher watcher;
        private string[] cachedFiles;
        private string cachePath;
        private KSPManager manager;
        private static readonly ILog log = LogManager.GetLogger(typeof (NetFileCache));

        /// <summary>
        /// Initialize a cache given a KSPManager
        /// </summary>
        /// <param name="mgr">KSPManager object containing the Instances that might have old caches</param>
        public NetFileCache(KSPManager mgr, string path)
            : this(path)
        {
            manager = mgr;
        }

        /// <summary>
        /// Initialize a cache given a path
        /// </summary>
        /// <param name="path">Location of folder to use for caching</param>
        public NetFileCache(string path)
        {
            cachePath = path;

            // Basic validation, our cache has to exist.
            if (!Directory.Exists(cachePath))
            {
                throw new DirectoryNotFoundKraken(cachePath, $"Cannot find cache directory: {cachePath}");
            }

            // Establish a watch on our cache. This means we can cache the directory contents,
            // and discard that cache if we spot changes.
            watcher = new FileSystemWatcher(cachePath, "");

            // While we should only care about files appearing and disappearing, I've over-asked
            // for permissions to get things to work on Mono.

            watcher.NotifyFilter =
                NotifyFilters.LastWrite | NotifyFilters.LastAccess | NotifyFilters.DirectoryName | NotifyFilters.FileName;

            // If we spot any changes, we fire our event handler.
            watcher.Changed += new FileSystemEventHandler(OnCacheChanged);
            watcher.Created += new FileSystemEventHandler(OnCacheChanged);
            watcher.Deleted += new FileSystemEventHandler(OnCacheChanged);
            watcher.Renamed += new RenamedEventHandler(OnCacheChanged);

            // Enable events!
            watcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Releases all resource used by the <see cref="CKAN.NetFileCache"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="CKAN.NetFileCache"/>. The
        /// <see cref="Dispose"/> method leaves the <see cref="CKAN.NetFileCache"/> in an unusable state. After calling
        /// <see cref="Dispose"/>, you must release all references to the <see cref="CKAN.NetFileCache"/> so the garbage
        /// collector can reclaim the memory that the <see cref="CKAN.NetFileCache"/> was occupying.</remarks>
        public void Dispose()
        {
            // All we really need to do is clear our FileSystemWatcher.
            // We disable its event raising capabilities first for good measure.
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        /// <summary>
        /// Called from our FileSystemWatcher. Use OnCacheChanged()
        /// without arguments to signal manually.
        /// </summary>
        private void OnCacheChanged(object source, FileSystemEventArgs e)
        {
            OnCacheChanged();
        }

        /// <summary>
        /// When our cache dirctory changes, we just clear the list of
        /// files we know about.
        /// </summary>
        public void OnCacheChanged()
        {
            cachedFiles = null;
        }

        // returns true if a url is already in the cache
        public bool IsCached(Uri url)
        {
            return GetCachedFilename(url) != null;
        }

        // returns true if a url is already in the cache
        // returns the filename in the outFilename parameter
        public bool IsCached(Uri url, out string outFilename)
        {
            outFilename = GetCachedFilename(url);

            return outFilename != null;
        }

        /// <summary>
        /// Returns true if our given URL is cached, *and* it passes zip
        /// validation tests. Prefer this over IsCached when working with
        /// zip files.
        /// </summary>
        public bool IsCachedZip(Uri url)
        {
            return GetCachedZip(url) != null;
        }

        /// <summary>
        /// Returns true if a file matching the given URL is cached, but makes no
        /// attempts to check if it's even valid. This is very fast.
        ///
        /// Use IsCachedZip() for a slower but more reliable method.
        /// </summary>
        public bool IsMaybeCachedZip(Uri url)
        {
            return GetCachedFilename(url) != null;
        }

        /// <summary>>
        /// Returns the filename of an already cached url or null otherwise
        /// </summary>
        /// <param name="url">The URL to check for in the cache</param>
        /// <param name="remoteTimestamp">Timestamp of the remote file, if known; cached files older than this will be considered invalid</param>
        public string GetCachedFilename(Uri url, DateTime? remoteTimestamp = null)
        {
            log.DebugFormat("Checking cache for {0}", url);

            if (url == null)
            {
                return null;
            }

            string hash = CreateURLHash(url);

            // Use our existing list of files, or retrieve and
            // store the list of files in our cache. Note that
            // we copy cachedFiles into our own variable as it
            // *may* get cleared by OnCacheChanged while we're
            // using it.

            string[] files = cachedFiles;

            if (files == null)
            {
                log.Debug("Rebuilding cache index");
                cachedFiles = files = Directory.GetFiles(cachePath);
            }

            // Now that we have a list of files one way or another,
            // check them to see if we can find the one we're looking
            // for.

            string found = scanDirectory(files, hash, remoteTimestamp);
            if (!string.IsNullOrEmpty(found))
            {
                return found;
            }

            // Check legacy caches if we have them
            if (manager != null)
            {
                string foundLegacy = legacyDirs()
                    .Select(dir => scanDirectory(Directory.EnumerateFiles(dir), hash, remoteTimestamp))
                    .FirstOrDefault(f => !string.IsNullOrEmpty(f));
                if (!string.IsNullOrEmpty(foundLegacy))
                {
                    return foundLegacy;
                }
            }

            return null;
        }

        private string scanDirectory(IEnumerable<string> files, string findHash, DateTime? remoteTimestamp = null)
        {
            foreach (string file in files)
            {
                string filename = Path.GetFileName(file);
                if (filename.StartsWith(findHash))
                {
                    // Check local vs remote timestamps; if local is older, then it's invalid.
                    // null means we don't know the remote timestamp (so file is OK)
                    if (remoteTimestamp == null
                        || remoteTimestamp < File.GetLastWriteTime(file).ToUniversalTime())
                    {
                        // File not too old, use it
                        return file;
                    }
                    else
                    {
                        // Local file too old, delete it
                        File.Delete(file);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the filename for a cached URL, if and only if it
        /// passes zipfile validation tests. Prefer this to GetCachedFilename
        /// when working with zip files. Returns null if not available, or
        /// validation failed.
        ///
        /// Low level CRC (cyclic redundancy check) checks will be done.
        /// This can take time on order of seconds for larger zip files.
        /// </summary>
        public string GetCachedZip(Uri url)
        {
            string filename = GetCachedFilename(url);
            if (string.IsNullOrEmpty(filename))
            {
                return null;
            }
            else
            {
                string invalidReason;
                if (ZipValid(filename, out invalidReason))
                {
                    return filename;
                }
                else
                {
                    // Purge invalid cache entries
                    File.Delete(filename);
                    return null;
                }
            }
        }

        /// <summary>
        /// Count the files and bytes in the cache
        /// </summary>
        /// <param name="numFiles">Output parameter set to number of files in cache</param>
        /// <param name="numBytes">Output parameter set to number of bytes in cache</param>
        public void GetSizeInfo(out int numFiles, out long numBytes)
        {
            numFiles = 0;
            numBytes = 0;
            GetSizeInfo(cachePath, ref numFiles, ref numBytes);
            foreach (var legacyDir in legacyDirs())
            {
                GetSizeInfo(legacyDir, ref numFiles, ref numBytes);
            }
        }

        private void GetSizeInfo(string path, ref int numFiles, ref long numBytes)
        {
            DirectoryInfo cacheDir = new DirectoryInfo(path);
            foreach (var file in cacheDir.EnumerateFiles())
            {
                ++numFiles;
                numBytes += file.Length;
            }
        }

        private HashSet<string> legacyDirs()
        {
            return manager?.Instances.Values
                .Where(ksp => ksp.Valid)
                .Select(ksp => ksp.DownloadCacheDir())
                .Where(dir => Directory.Exists(dir))
                .ToHashSet();
        }

        /// <summary>
        /// Check whether a ZIP file is valid
        /// </summary>
        /// <param name="filename">Path to zip file to check</param>
        /// <param name="invalidReason">Description of problem with the file</param>
        /// <returns>
        /// True if valid, false otherwise. See invalidReason param for explanation.
        /// </returns>
        public static bool ZipValid(string filename, out string invalidReason)
        {
            try
            {
                if (filename != null)
                {
                    using (ZipFile zip = new ZipFile(filename))
                    {
                        string zipErr = null;
                        // Perform CRC and other checks
                        if (zip.TestArchive(true, TestStrategy.FindFirstError,
                            (TestStatus st, string msg) =>
                            {
                                // This delegate is called as TestArchive proceeds through its
                                // steps, both routine and abnormal.
                                // The second parameter is non-null if an error occurred.
                                if (st != null && !st.EntryValid && !string.IsNullOrEmpty(msg))
                                {
                                    // Capture the error string so we can return it
                                    zipErr = $"Error in step {st.Operation} for {st.Entry?.Name}: {msg}";
                                }
                            }))
                        {
                            invalidReason = "";
                            return true;
                        }
                        else
                        {
                            invalidReason = zipErr ?? "ZipFile.TestArchive(true) returned false";
                            return false;
                        }
                    }
                }
                else
                {
                    invalidReason = "Null file name";
                    return false;
                }
            }
            catch (ZipException ze)
            {
                // Save the errors someplace useful
                invalidReason = ze.Message;
                return false;
            }
            catch (ArgumentException ex)
            {
                invalidReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Stores the results of a given URL in the cache.
        /// Description is adjusted to be filesystem-safe and then appended to the file hash when saving.
        /// If not present, the filename will be used.
        /// If `move` is true, then the file will be moved; otherwise, it will be copied.
        ///
        /// Returns a path to the newly cached file.
        ///
        /// This method is filesystem transaction aware.
        /// </summary>
        public string Store(Uri url, string path, string description = null, bool move = false)
        {
            log.DebugFormat("Storing {0}", url);

            TxFileManager tx_file = new TxFileManager();

            // Make sure we clear our cache entry first.
            Remove(url);

            string hash = CreateURLHash(url);

            description = description ?? Path.GetFileName(path);

            Debug.Assert(
                Regex.IsMatch(description, "^[A-Za-z0-9_.-]*$"),
                "description isn't as filesystem safe as we thought... (#1266)"
            );

            string fullName = String.Format("{0}-{1}", hash, Path.GetFileName(description));
            string targetPath = Path.Combine(cachePath, fullName);

            log.InfoFormat("Storing {0} in {1}", path, targetPath);

            if (move)
            {
                tx_file.Move(path, targetPath);
            }
            else
            {
                tx_file.Copy(path, targetPath, true);
            }

            // We've changed our cache, so signal that immediately.
            OnCacheChanged();

            return targetPath;
        }

        /// <summary>
        /// Removes the given URL from the cache.
        /// Returns true if any work was done, false otherwise.
        /// This method is filesystem transaction aware.
        /// </summary>
        public bool Remove(Uri url)
        {
            TxFileManager tx_file = new TxFileManager();

            string file = GetCachedFilename(url);

            if (file != null)
            {
                tx_file.Delete(file);

                // We've changed our cache, so signal that immediately.
                OnCacheChanged();

                return true;
            }

            return false;
        }

        /// <summary>
        /// Clear all files in cache, including main directory and legacy directories
        /// </summary>
        public void RemoveAll()
        {
            foreach (string file in Directory.EnumerateFiles(cachePath))
            {
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
            foreach (string dir in legacyDirs())
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
            }
            OnCacheChanged();
        }

        /// <summary>
        /// Move files from another folder into this cache
        /// May throw an IOException if disk is full!
        /// </summary>
        /// <param name="fromDir">Path from which to move files</param>
        public void MoveFrom(string fromDir)
        {
            if (cachePath != fromDir && Directory.Exists(fromDir))
            {
                bool hasAny = false;
                foreach (string fromFile in Directory.EnumerateFiles(fromDir))
                {
                    string toFile = Path.Combine(cachePath, Path.GetFileName(fromFile));
                    if (File.Exists(toFile))
                    {
                        // Don't need multiple copies of the same file
                        File.Delete(fromFile);
                    }
                    else
                    {
                        File.Move(fromFile, toFile);
                        hasAny = true;
                    }
                }
                if (hasAny)
                {
                    OnCacheChanged();
                }
            }
        }

        /// <summary>
        /// Generate the hash used for caching
        /// </summary>
        /// <param name="url">URL to hash</param>
        /// <returns>
        /// Returns the 8-byte hash for a given url
        /// </returns>
        public static string CreateURLHash(Uri url)
        {
            using (var sha1 = new SHA1Cng())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(url.ToString()));

                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
            }
        }
    }
}
