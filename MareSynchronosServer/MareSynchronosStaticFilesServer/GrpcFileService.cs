using Grpc.Core;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Protos;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosStaticFilesServer;

public class GrpcFileService : FileService.FileServiceBase
{
    private readonly string _basePath;
    private readonly MareDbContext _mareDbContext;
    private readonly ILogger<GrpcFileService> _logger;
    private readonly MareMetrics _metricsClient;

    public GrpcFileService(MareDbContext mareDbContext, IConfiguration configuration, ILogger<GrpcFileService> logger, MareMetrics metricsClient)
    {
        _basePath = configuration.GetRequiredSection("MareSynchronos")["CacheDirectory"];
        _mareDbContext = mareDbContext;
        _logger = logger;
        _metricsClient = metricsClient;
    }

    public override async Task<Empty> UploadFile(IAsyncStreamReader<UploadFileRequest> requestStream, ServerCallContext context)
    {
        _ = await requestStream.MoveNext().ConfigureAwait(false);
        var uploadMsg = requestStream.Current;
        var filePath = FilePathUtil.GetFilePath(_basePath, uploadMsg.Hash);
        using var fileWriter = File.OpenWrite(filePath);
        var file = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == uploadMsg.Hash && f.UploaderUID == uploadMsg.Uploader).ConfigureAwait(false);
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

            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotal, 1);
            _metricsClient.IncGauge(MetricsAPI.GaugeFilesTotalSize, fileSize);

            await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);
            _logger.LogInformation("User {user} uploaded file {hash}", uploadMsg.Uploader, uploadMsg.Hash);
        }

        return new Empty();
    }

    public override async Task<Empty> DeleteFiles(DeleteFilesRequest request, ServerCallContext context)
    {
        foreach (var hash in request.Hash)
        {
            try
            {
                var fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
                fi?.Delete();
                var file = await _mareDbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash).ConfigureAwait(false);
                if (file != null)
                {
                    _mareDbContext.Files.Remove(file);

                    _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotal, fi == null ? 0 : 1);
                    _metricsClient.DecGauge(MetricsAPI.GaugeFilesTotalSize, fi?.Length ?? 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not delete file for hash {hash}", hash);
            }
        }

        await _mareDbContext.SaveChangesAsync().ConfigureAwait(false);
        return new Empty();
    }

    public override Task<FileSizeResponse> GetFileSizes(FileSizeRequest request, ServerCallContext context)
    {
        FileSizeResponse response = new();
        foreach (var hash in request.Hash.Distinct(StringComparer.Ordinal))
        {
            FileInfo? fi = FilePathUtil.GetFileInfoForHash(_basePath, hash);
            response.HashToFileSize.Add(hash, fi?.Length ?? 0);
        }

        return Task.FromResult(response);
    }
}
