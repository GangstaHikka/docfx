// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Microsoft.Docs.Build
{
    internal static class RestoreFile
    {
        public static async Task Restore(string url, Config config, bool @implict = false)
        {
            if (RestoreMap.TryGetFileRestorePath(url, out var existingPath) && implict)
            {
                return;
            }

            EntityTagHeaderValue existingEtag = null;
            if (!string.IsNullOrEmpty(existingPath))
            {
                existingEtag = GetEtag(existingPath);
            }

            var (tempFile, etag) = await DownloadToTempFile(url, config, existingEtag);
            if (tempFile == null)
            {
                return;
            }

            var fileName = GetRestoreFileName(HashUtility.GetFileSha1Hash(tempFile), etag);
            var filePath = PathUtility.NormalizeFile(Path.Combine(AppData.GetFileDownloadDir(url), fileName));
            await ProcessUtility.RunInsideMutex(filePath, MoveFile);

            Task MoveFile()
            {
                if (!File.Exists(filePath))
                {
                    PathUtility.CreateDirectoryFromFilePath(filePath);
                    File.Move(tempFile, filePath);
                }
                else
                {
                    File.Delete(tempFile);
                }

                // update the last write date
                File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Get restore file name from hash and ETag
        /// </summary>
        /// <param name="hash">SHA1 hash of file content</param>
        /// <param name="etag">ETag of the resource, null if server doesn't specify ETag</param>
        public static string GetRestoreFileName(string hash, EntityTagHeaderValue etag = null)
        {
            Debug.Assert(!string.IsNullOrEmpty(hash));

            var result = hash;
            if (etag != null)
            {
                result += $"+{HrefUtility.EscapeUrlSegment(etag.ToString())}";
            }
            return result;
        }

        /// <summary>
        /// Get ETag from restore file name
        /// </summary>
        /// <returns>ETag of the resource, null if server doesn't specify ETag</returns>
        public static EntityTagHeaderValue GetEtag(string restorePath)
        {
            Debug.Assert(!string.IsNullOrEmpty(restorePath));

            var parts = Path.GetFileName(restorePath).Split('+');
            return parts.Length == 2 ? new EntityTagHeaderValue(HrefUtility.UnescapeUrl(parts[1])) : null;
        }

        private static async Task<(string filename, EntityTagHeaderValue etag)> DownloadToTempFile(
            string url,
            Config config,
            EntityTagHeaderValue existingEtag)
        {
            Directory.CreateDirectory(AppData.DownloadsRoot);
            var tempFile = Path.Combine(AppData.DownloadsRoot, "." + Guid.NewGuid().ToString("N"));
            EntityTagHeaderValue etag = null;

            try
            {
                var response = await HttpClientUtility.GetAsync(url, config, existingEtag);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return (null, existingEtag);
                }

                etag = response.Headers.ETag;
                using (var stream = await response.EnsureSuccessStatusCode().Content.ReadAsStreamAsync())
                using (var file = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(file);
                }
            }
            catch (Exception ex)
            {
                throw Errors.DownloadFailed(url, ex.Message).ToException(ex);
            }
            return (tempFile, etag);
        }
    }
}