﻿using K4os.Compression.LZ4.Legacy;
using MareSynchronos.API.Dto.Files;
using MareSynchronos.API.Routes;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
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
    private readonly IDbContextFactory<MareDbContext> _mareDbContext;
    private readonly MareMetrics _metricsClient;
    private readonly MainServerShardRegistrationService _shardRegistrationService;

    public ServerFilesController(ILogger<ServerFilesController> logger, CachedFileProvider cachedFileProvider,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        IHubContext<MareHub> hubContext,
        IDbContextFactory<MareDbContext> mareDbContext, MareMetrics metricsClient,
        MainServerShardRegistrationService shardRegistrationService) : base(logger)
    {
        _basePath = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false)
            ? configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory))
            : configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _cachedFileProvider = cachedFileProvider;
        _configuration = configuration;
        _hubContext = hubContext;
        _mareDbContext = mareDbContext;
        _metricsClient = metricsClient;
        _shardRegistrationService = shardRegistrationService;
    }

    [HttpPost(MareFiles.ServerFiles_DeleteAll)]
    public async Task<IActionResult> FilesDeleteAll()
    {
        using var dbContext = await _mareDbContext.CreateDbContextAsync();
        var ownFiles = await dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == MareUser).ToListAsync().ConfigureAwait(false);
        bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

        foreach (var dbFile in ownFiles)
        {
            var fi = FilePathUtil.GetFileInfoForHash(_basePath, dbFile.Hash);
            if (fi != null)
            {
                _metricsClient.DecGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, fi == null ? 0 : 1);
                _metricsClient.DecGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, fi?.Length ?? 0);

                fi?.Delete();
            }
        }

        dbContext.Files.RemoveRange(ownFiles);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        return Ok();
    }

    [HttpGet(MareFiles.ServerFiles_GetSizes)]
    public async Task<IActionResult> FilesGetSizes([FromBody] List<string> hashes)
    {
        using var dbContext = await _mareDbContext.CreateDbContextAsync();
        var forbiddenFiles = await dbContext.ForbiddenUploadEntries.
            Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
        List<DownloadFileDto> response = new();

        var cacheFile = await dbContext.Files.AsNoTracking()
            .Where(f => hashes.Contains(f.Hash))
            .Select(k => new { k.Hash, k.Size, k.RawSize })
            .ToListAsync().ConfigureAwait(false);

        var allFileShards = _shardRegistrationService.GetConfigurationsByContinent(Continent);

        foreach (var file in cacheFile)
        {
            var forbiddenFile = forbiddenFiles.SingleOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));
            Uri? baseUrl = null;

            if (forbiddenFile == null)
            {
                var matchingShards = allFileShards.Where(f => new Regex(f.FileMatch).IsMatch(file.Hash)).ToList();

                var shard = matchingShards.SelectMany(g => g.RegionUris)
                    .OrderBy(g => Guid.NewGuid()).FirstOrDefault();

                baseUrl = shard.Value ?? _configuration.GetValue<Uri>(nameof(StaticFilesServerConfiguration.CdnFullUrl));
            }

            response.Add(new DownloadFileDto
            {
                FileExists = file.Size > 0,
                ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbiddenFile != null,
                Hash = file.Hash,
                Size = file.Size,
                Url = baseUrl?.ToString() ?? string.Empty,
                RawSize = file.RawSize
            });
        }

        return Ok(JsonSerializer.Serialize(response));
    }

    [HttpGet(MareFiles.ServerFiles_DownloadServers)]
    public async Task<IActionResult> GetDownloadServers()
    {
        var allFileShards = _shardRegistrationService.GetConfigurationsByContinent(Continent);
        return Ok(JsonSerializer.Serialize(allFileShards.SelectMany(t => t.RegionUris.Select(v => v.Value.ToString()))));
    }

    [HttpPost(MareFiles.ServerFiles_FilesSend)]
    public async Task<IActionResult> FilesSend([FromBody] FilesSendDto filesSendDto)
    {
        using var dbContext = await _mareDbContext.CreateDbContextAsync();

        var userSentHashes = new HashSet<string>(filesSendDto.FileHashes.Distinct(StringComparer.Ordinal).Select(s => string.Concat(s.Where(c => char.IsLetterOrDigit(c)))), StringComparer.Ordinal);
        var notCoveredFiles = new Dictionary<string, UploadFileDto>(StringComparer.Ordinal);
        var forbiddenFiles = await dbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var existingFiles = await dbContext.Files.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).AsNoTracking().ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);

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

    [HttpPost(MareFiles.ServerFiles_Upload + "/{hash}")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile(string hash, CancellationToken requestAborted)
    {
        using var dbContext = await _mareDbContext.CreateDbContextAsync();

        _logger.LogInformation("{user} uploading file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim? fileLock = null;
        bool successfullyWaited = false;
        while (!successfullyWaited && !requestAborted.IsCancellationRequested)
        {
            lock (_fileUploadLocks)
            {
                if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                    _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
            }

            try
            {
                await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);
                successfullyWaited = true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("Semaphore disposed for {hash}, recreating", hash);
            }
        }

        try
        {
            var existingFileCheck2 = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            // copy the request body to memory
            using var compressedFileStream = new MemoryStream();
            await Request.Body.CopyToAsync(compressedFileStream, requestAborted).ConfigureAwait(false);

            // decompress and copy the decompressed stream to memory
            var data = LZ4Wrapper.Unwrap(compressedFileStream.ToArray());

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
            await dbContext.Files.AddAsync(new FileCache()
            {
                Hash = hash,
                UploadDate = DateTime.UtcNow,
                UploaderUID = MareUser,
                Size = compressedFileStream.Length,
                Uploaded = true,
                RawSize = data.LongLength
            }).ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

            _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, compressedFileStream.Length);

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
            try
            {
                fileLock?.Release();
                fileLock?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // it's disposed whatever
            }
            finally
            {
                _fileUploadLocks.TryRemove(hash, out _);
            }
        }
    }

    [HttpPost(MareFiles.ServerFiles_UploadMunged + "/{hash}")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadFileMunged(string hash, CancellationToken requestAborted)
    {
        using var dbContext = await _mareDbContext.CreateDbContextAsync();

        _logger.LogInformation("{user} uploading munged file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim? fileLock = null;
        bool successfullyWaited = false;
        while (!successfullyWaited && !requestAborted.IsCancellationRequested)
        {
            lock (_fileUploadLocks)
            {
                if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                    _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
            }

            try
            {
                await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);
                successfullyWaited = true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("Semaphore disposed for {hash}, recreating", hash);
            }
        }

        try
        {
            var existingFileCheck2 = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
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
            var data = LZ4Wrapper.Unwrap(unmungedFile);

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
            await dbContext.Files.AddAsync(new FileCache()
            {
                Hash = hash,
                UploadDate = DateTime.UtcNow,
                UploaderUID = MareUser,
                Size = compressedMungedStream.Length,
                Uploaded = true,
                RawSize = data.LongLength
            }).ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

            _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, compressedMungedStream.Length);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            try
            {
                fileLock?.Release();
                fileLock?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // it's disposed whatever
            }
            finally
            {
                _fileUploadLocks.TryRemove(hash, out _);
            }
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
        using var dbContext = await _mareDbContext.CreateDbContextAsync();

        _logger.LogInformation("{user} uploading raw file {file}", MareUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim? fileLock = null;
        bool successfullyWaited = false;
        while (!successfullyWaited && !requestAborted.IsCancellationRequested)
        {
            lock (_fileUploadLocks)
            {
                if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                    _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
            }

            try
            {
                await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);
                successfullyWaited = true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("Semaphore disposed for {hash}, recreating", hash);
            }
        }

        try
        {
            var existingFileCheck2 = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
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
            var lz4 = LZ4Wrapper.WrapHC(rawFileStream.ToArray(), 0, (int)rawFileStream.Length);
            using var compressedStream = new MemoryStream(lz4);
            await compressedStream.CopyToAsync(fileStream).ConfigureAwait(false);

            // update on db
            await dbContext.Files.AddAsync(new FileCache()
            {
                Hash = hash,
                UploadDate = DateTime.UtcNow,
                UploaderUID = MareUser,
                Size = compressedStream.Length,
                Uploaded = true,
                RawSize = rawFileStream.Length
            }).ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

            _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, rawFileStream.Length);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during file upload");
            return BadRequest();
        }
        finally
        {
            try
            {
                fileLock?.Release();
                fileLock?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // it's disposed whatever
            }
            finally
            {
                _fileUploadLocks.TryRemove(hash, out _);
            }
        }
    }
}