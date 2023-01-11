using Grpc.Core;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer.Services;

[Authorize(Policy = "Internal")]
public class GrpcFileService : FileService.FileServiceBase
{
    private readonly string _basePath;
    private readonly MareDbContext _mareDbContext;
    private readonly ILogger<GrpcFileService> _logger;
    private readonly MareMetrics _metricsClient;

    public GrpcFileService(MareDbContext mareDbContext, IConfigurationService<StaticFilesServerConfiguration> configuration, ILogger<GrpcFileService> logger, MareMetrics metricsClient)
    {
        _basePath = configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _mareDbContext = mareDbContext;
        _logger = logger;
        _metricsClient = metricsClient;
    }

    [Authorize(Policy = "Internal")]
    public override async Task<Empty> UploadFile(IAsyncStreamReader<UploadFileRequest> requestStream, ServerCallContext context)
    {
        _ = await requestStream.MoveNext().ConfigureAwait(false);
        var uploadMsg = requestStream.Current;
        var filePath = FilePathUtil.GetFilePath(_basePath, uploadMsg.Hash);
        using var fileWriter = File.OpenWrite(filePath);
        var file = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == uploadMsg.Hash && f.UploaderUID == uploadMsg.Uploader).ConfigureAwait(false);
        try
        {
            if (file != null)
            {
                await fileWriter.WriteAsync(uploadMsg.FileData.ToArray()).ConfigureAwait(false);

                while (await requestStream.MoveNext().ConfigureAwait(false))
                {
                    await fileWriter.WriteAsync(requestStream.Current.FileData.ToArray()).ConfigureAwait(false);
                }

                await fileWriter.FlushAsync().ConfigureAwait(false);
                fileWriter.Close();

                var fileSize = new FileInfo(filePath).Length;
                file.Uploaded = true;
                file.Size = fileSize;

                await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

                _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
                _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, fileSize);

                _logger.LogInformation("User {user} uploaded file {hash}", uploadMsg.Uploader, uploadMsg.Hash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during UploadFile");
            var fileNew = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == uploadMsg.Hash && f.UploaderUID == uploadMsg.Uploader).ConfigureAwait(false);
            if (fileNew != null)
            {
                _mareDbContext.Files.Remove(fileNew);
            }

            await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Empty();
    }

    [Authorize(Policy = "Internal")]
    public override async Task<Empty> DeleteFiles(DeleteFilesRequest request, ServerCallContext context)
    {
        foreach (var hash in request.Hash)
        {
            try
            {
                var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
                var file = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash).ConfigureAwait(false);
                if (file != null && fi != null)
                {
                    _mareDbContext.Files.Remove(file);
                    await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);

                    _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotal, fi == null ? 0 : 1);
                    _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotalSize, fi?.Length ?? 0);

                    fi?.Delete();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not delete file for hash {hash}", hash);
            }
        }

        return new Empty();
    }
}
