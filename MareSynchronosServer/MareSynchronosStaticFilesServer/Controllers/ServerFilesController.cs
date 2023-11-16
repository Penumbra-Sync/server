using LZ4;
using MareSynchronos.API.Dto.Files;
using MareSynchronos.API.Routes;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.ServerFiles)]
public class ServerFilesController : ControllerBase
{
    private static readonly SemaphoreSlim _fileLockDictLock = new(1);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileUploadLocks = new(StringComparer.Ordinal);
    private readonly string _basePath;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly IHubContext<MareHub> _hubContext;
    private readonly MareDbContext _mareDbContext;
    private readonly MareMetrics _metricsClient;

    public ServerFilesController(ILogger<ServerFilesController> logger, CachedFileProvider cachedFileProvider,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        IHubContext<MareSynchronosServer.Hubs.MareHub> hubContext,
        MareDbContext mareDbContext, MareMetrics metricsClient) : base(logger)
    {
        _basePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _cachedFileProvider = cachedFileProvider;
        _configuration = configuration;
        _hubContext = hubContext;
        _mareDbContext = mareDbContext;
        _metricsClient = metricsClient;
    }

    [HttpPost(MareFiles.ServerFiles_DeleteAll)]
    public async Task<IActionResult> FilesDeleteAll()
    {
        var ownFiles = await _mareDbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == MareUser).ToListAsync().ConfigureAwait(false);

        foreach (var dbFile in ownFiles)
        {
            var fi = FilePathUtil.GetFileInfoForHash(_basePath, dbFile.Hash);
            if (fi != null)
            {
                _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotal, fi == null ? 0 : 1);
                _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotalSize, fi?.Length ?? 0);

                fi?.Delete();
            }
        }

        _mareDbContext.Files.RemoveRange(ownFiles);
        await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

        return Ok();
    }

    [HttpGet(MareFiles.ServerFiles_GetSizes)]
    public async Task<IActionResult> FilesGetSizes([FromBody] List<string> hashes)
    {
        var allFiles = await _mareDbContext.Files.Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
        var forbiddenFiles = await _mareDbContext.ForbiddenUploadEntries.
            Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
        List<DownloadFileDto> response = new();

        var cacheFile = await _mareDbContext.Files.AsNoTracking().Where(f => hashes.Contains(f.Hash)).AsNoTracking().Select(k => new { k.Hash, k.Size }).AsNoTracking().ToListAsync().ConfigureAwait(false);

        var shardConfig = new List<CdnShardConfiguration>(_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.CdnShardConfiguration), new List<CdnShardConfiguration>()));

        foreach (var file in cacheFile)
        {
            var forbiddenFile = forbiddenFiles.SingleOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));

            var matchedShardConfig = shardConfig.OrderBy(g => Guid.NewGuid()).FirstOrDefault(f => new Regex(f.FileMatch).IsMatch(file.Hash));
            var baseUrl = matchedShardConfig?.CdnFullUrl ?? _configuration.GetValue<Uri>(nameof(StaticFilesServerConfiguration.CdnFullUrl));

            response.Add(new DownloadFileDto
            {
                FileExists = file.Size > 0,
                ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbiddenFile != null,
                Hash = file.Hash,
                Size = file.Size,
                Url = baseUrl.ToString(),
            });
        }

        return Ok(JsonSerializer.Serialize(response));
    }

    [HttpPost(MareFiles.ServerFiles_FilesSend)]
    public async Task<IActionResult> FilesSend([FromBody] FilesSendDto filesSendDto)
    {
        var userSentHashes = new HashSet<string>(filesSendDto.FileHashes.Distinct(StringComparer.Ordinal).Select(s => string.Concat(s.Where(c => char.IsLetterOrDigit(c)))), StringComparer.Ordinal);
        var notCoveredFiles = new Dictionary<string, UploadFileDto>(StringComparer.Ordinal);
        var forbiddenFiles = await _mareDbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var existingFiles = await _mareDbContext.Files.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);

        List<FileCache> fileCachesToUpload = new();
        foreach (var hash in userSentHashes)
        {
            // Skip empty file hashes, duplicate file hashes, forbidden file hashes and existing file hashes
            if (string.IsNullOrEmpty(hash)) { continue; }
            if (notCoveredFiles.ContainsKey(hash)) { continue; }
            if (forbiddenFiles.ContainsKey(hash))
            {
                notCoveredFiles[hash] = new UploadFileDto()
                {
                    ForbiddenBy = forbiddenFiles[hash].ForbiddenBy,
                    Hash = hash,
                    IsForbidden = true,
                };

                continue;
            }
            if (existingFiles.TryGetValue(hash, out var file) && file.Uploaded) { continue; }

            notCoveredFiles[hash] = new UploadFileDto()
            {
                Hash = hash,
            };
        }

        if (notCoveredFiles.Any(p => !p.Value.IsForbidden))
        {
            await _hubContext.Clients.Users(filesSendDto.UIDs).SendAsync(nameof(IMareHub.Client_UserReceiveUploadStatus), new MareSynchronos.API.Dto.User.UserDto(new(MareUser)))
                .ConfigureAwait(false);
        }

        return Ok(JsonSerializer.Serialize(notCoveredFiles.Values.ToList()));
    }

    [HttpGet(MareFiles.ServerFiles_Get + "/{fileId}")]
    [Authorize(Policy = "Internal")]
    public IActionResult GetFile(string fileId)
    {
        _logger.LogInformation($"GetFile:{MareUser}:{fileId}");

        var fs = _cachedFileProvider.GetLocalFileStream(fileId);
        if (fs == null) return NotFound();

        return File(fs, "application/octet-stream");
    }

    [HttpPost(MareFiles.ServerFiles_Upload + "/{hash}")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile(string hash, CancellationToken requestAborted)
    {
        _logger.LogInformation("{user} uploading file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim fileLock;
        lock (_fileUploadLocks)
        {
            if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
        }

        await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);

        try
        {
            var existingFileCheck2 = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            // copy the request body to memory
            using var compressedFileStream = new MemoryStream();
            await Request.Body.CopyToAsync(compressedFileStream, requestAborted).ConfigureAwait(false);

            // decompress and copy the decompressed stream to memory
            var data = LZ4Codec.Unwrap(compressedFileStream.ToArray());

            // reset streams
            compressedFileStream.Seek(0, SeekOrigin.Begin);

            // compute hash to verify
            var hashString = BitConverter.ToString(SHA1.HashData(data))
                .Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            if (!string.Equals(hashString, hash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hash does not match file, computed: {hashString}, expected: {hash}");

            // save file
            var path = FilePathUtil.GetFilePath(_basePath, hash);
            using var fileStream = new FileStream(path, FileMode.Create);
            await compressedFileStream.CopyToAsync(fileStream).ConfigureAwait(false);

            // update on db
            await _mareDbContext.Files.AddAsync(new FileCache()
            {
                Hash = hash,
                UploadDate = DateTime.UtcNow,
                UploaderUID = MareUser,
                Size = compressedFileStream.Length,
                Uploaded = true
            }).ConfigureAwait(false);
            await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, compressedFileStream.Length);

            _fileUploadLocks.TryRemove(hash, out _);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            fileLock.Release();
            fileLock.Dispose();
        }
    }

    [HttpPost(MareFiles.ServerFiles_UploadMunged + "/{hash}")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadFileMunged(string hash, CancellationToken requestAborted)
    {
        _logger.LogInformation("{user} uploading munged file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim fileLock;
        lock (_fileUploadLocks)
        {
            if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
        }

        await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);

        try
        {
            var existingFileCheck2 = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            // copy the request body to memory
            using var compressedMungedStream = new MemoryStream();
            await Request.Body.CopyToAsync(compressedMungedStream, requestAborted).ConfigureAwait(false);
            var unmungedFile = compressedMungedStream.ToArray();
            MungeBuffer(unmungedFile.AsSpan());

            // decompress and copy the decompressed stream to memory
            var data = LZ4Codec.Unwrap(unmungedFile);

            // compute hash to verify
            var hashString = BitConverter.ToString(SHA1.HashData(data))
                .Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            if (!string.Equals(hashString, hash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hash does not match file, computed: {hashString}, expected: {hash}");

            // save file
            var path = FilePathUtil.GetFilePath(_basePath, hash);
            using var fileStream = new FileStream(path, FileMode.Create);
            await fileStream.WriteAsync(unmungedFile.AsMemory()).ConfigureAwait(false);

            // update on db
            await _mareDbContext.Files.AddAsync(new FileCache()
            {
                Hash = hash,
                UploadDate = DateTime.UtcNow,
                UploaderUID = MareUser,
                Size = compressedMungedStream.Length,
                Uploaded = true
            }).ConfigureAwait(false);
            await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, compressedMungedStream.Length);

            _fileUploadLocks.TryRemove(hash, out _);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            fileLock.Release();
            fileLock.Dispose();
        }
    }

    private static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= 42;
        }
    }

    [HttpPost(MareFiles.ServerFiles_UploadRaw + "/{hash}")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadFileRaw(string hash, CancellationToken requestAborted)
    {
        _logger.LogInformation("{user} uploading raw file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim fileLock;
        lock (_fileUploadLocks)
        {
            if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
        }

        await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);

        try
        {
            var existingFileCheck2 = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            // copy the request body to memory
            using var rawFileStream = new MemoryStream();
            await Request.Body.CopyToAsync(rawFileStream, requestAborted).ConfigureAwait(false);

            // reset streams
            rawFileStream.Seek(0, SeekOrigin.Begin);

            // compute hash to verify
            var hashString = BitConverter.ToString(SHA1.HashData(rawFileStream.ToArray()))
                .Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            if (!string.Equals(hashString, hash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hash does not match file, computed: {hashString}, expected: {hash}");

            // save file
            var path = FilePathUtil.GetFilePath(_basePath, hash);
            using var fileStream = new FileStream(path, FileMode.Create);
            var lz4 = LZ4Codec.WrapHC(rawFileStream.ToArray(), 0, (int)rawFileStream.Length);
            using var compressedStream = new MemoryStream(lz4);
            await compressedStream.CopyToAsync(fileStream).ConfigureAwait(false);

            // update on db
            await _mareDbContext.Files.AddAsync(new FileCache()
            {
                Hash = hash,
                UploadDate = DateTime.UtcNow,
                UploaderUID = MareUser,
                Size = compressedStream.Length,
                Uploaded = true
            }).ConfigureAwait(false);
            await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, rawFileStream.Length);

            _fileUploadLocks.TryRemove(hash, out _);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            fileLock.Release();
            fileLock.Dispose();
        }
    }
}